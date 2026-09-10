using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

// SteamManifestCache_Pro 仓库取用：分支名 = AppId，Tag 名 = Manifest 文件名。
// 策略 raw-first：PICS 最新 gid 直链（命中则 0 API 开销）→ 分支文件 → Tag 旧版，
// 全程走 IHttpClientProvider（代理重试），取到的文件经 ILuaFileManager 落到 depotcache。
public class SteamManifestRepoService : ISteamManifestRepoService
{
    private const string Owner = "P-ToyStore";
    private const string Repo = "SteamManifestCache_Pro";
    private const string ClientName = "manifest-repo";
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(120);
    private const int FetchParallelism = 5;

    private static readonly Regex ManifestFileRegex = new(@"^(\d+)_(\d+)\.manifest$", RegexOptions.IgnoreCase);

    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamDepotService _depotService;
    private readonly ILuaFileManager _luaFileManager;

    private readonly object _treeCacheLock = new();
    private readonly Dictionary<int, Dictionary<int, string>?> _treeCache = new();

    // GitHub API 403 限流标记：单次 Fetch 内有效，调用方据此提示"缺失或为误判"
    private int _rateLimited;

    public SteamManifestRepoService(
        IHttpClientProvider httpClientProvider,
        ISteamDepotService depotService,
        ILuaFileManager luaFileManager)
    {
        _httpClientProvider = httpClientProvider;
        _depotService = depotService;
        _luaFileManager = luaFileManager;
    }

    public async Task<ManifestRepoFetchResult> FetchManifestsAsync(
        int appId,
        IReadOnlyList<int> luaAppIds,
        IProgress<(int done, int total, string text)>? progress,
        CancellationToken ct = default)
    {
        _rateLimited = 0;
        // 1. PICS：base 仓库 gid + DLC 列表（最新性比对与主/DLC 分类都靠它）
        var basePics = await _depotService.QueryAppAsync(appId, ct);
        if (basePics == null)
            return new ManifestRepoFetchResult(false, "无法查询该游戏的仓库信息，请检查网络后重试",
                [], [], [], [], false, null);

        var baseMap = basePics.GameDepots
            .Where(d => !string.IsNullOrEmpty(d.ManifestId))
            .ToDictionary(d => d.DepotId, d => d.ManifestId);
        var baseIds = new HashSet<int>(basePics.GameDepots.Select(d => d.DepotId));
        var dlcSet = new HashSet<int>(basePics.DlcAppIds);

        if (baseIds.Count == 0 && dlcSet.Count == 0)
            LogService.Info("Manifest仓库", "PICS 无仓库信息（token 保护或未收录），降级按 lua 行逐个尝试");

        // 需要 manifest 的 id 集合 + 已知的 PICS gid（gid 为空不影响需求判定，只影响最新性比对）。
        // 注意：兜底/无 gid 时 baseMap 为空是常态，不能因此判定为无需获取。
        var needIds = new HashSet<int>();
        var picsGids = new Dictionary<int, string>();
        if (baseIds.Count == 0 && dlcSet.Count == 0)
        {
            // PICS 完全无信息：以 lua 为准，除主 appid 外全部尝试；缺失归属未知，单独归类
            foreach (var id in luaAppIds)
                if (id != appId) needIds.Add(id);
        }
        else
        {
            foreach (var id in luaAppIds)
            {
                if (id == appId) continue;
                if (baseIds.Contains(id))
                {
                    needIds.Add(id);
                    picsGids[id] = baseMap.GetValueOrDefault(id, "");
                }
            }
        }
        var dlcLuaIds = luaAppIds.Where(id => id != appId && dlcSet.Contains(id) && !baseMap.ContainsKey(id)).Distinct().ToList();
        if (dlcLuaIds.Count > 0)
        {
            var luaIdSet = new HashSet<int>(luaAppIds);
            var subResults = new Dictionary<int, DepotQueryResult?>();
            await Parallel.ForEachAsync(dlcLuaIds,
                new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
                async (dlcId, innerCt) =>
                {
                    DepotQueryResult? sub = null;
                    try { sub = await _depotService.QueryAppAsync(dlcId, innerCt); }
                    catch (OperationCanceledException) when (innerCt.IsCancellationRequested) { throw; }
                    catch (Exception ex) { LogService.Info("Manifest仓库", $"DLC {dlcId} 仓库查询失败: {ex.Message}"); }
                    lock (subResults) { subResults[dlcId] = sub; }
                });
            foreach (var dlcId in dlcLuaIds)
            {
                var sub = subResults.GetValueOrDefault(dlcId);
                if (sub == null) continue;
                foreach (var depot in sub.GameDepots)
                    if (luaIdSet.Contains(depot.DepotId) && needIds.Add(depot.DepotId))
                        picsGids[depot.DepotId] = depot.ManifestId ?? "";
            }
        }

        if (needIds.Count == 0)
            return new ManifestRepoFetchResult(true, null, [], [], [], [], false, null);

        // 2. 分支存在性：404 即整游戏缺失，直接返回
        var branchFiles = await GetBranchFilesAsync(appId, ct);
        if (branchFiles == null)
            return new ManifestRepoFetchResult(false, $"仓库中没有游戏 {appId} 的分支，无法获取",
                [], [], [], [], false, null);

        // 主仓库判定：base 专属 depot；其余（含 DLC 共享 depot）走 DLC 可移除流程。
        // PICS 完全无信息时无法分类，缺失统一进未知桶。
        bool picsEmpty = baseIds.Count == 0 && dlcSet.Count == 0;
        bool IsMainDepot(int id) => baseIds.Contains(id) && !dlcSet.Contains(id);

        // 3. 逐 depot 拉取（并发），分类收集后由调用方展示
        var needList = needIds.ToList();
        var fetched = new List<FetchedDepot>();
        var missingMain = new List<int>();
        var missingDlc = new List<int>();
        var missingUnknown = new List<int>();
        int done = 0;
        var resultLock = new object();

        await Parallel.ForEachAsync(needList,
            new ParallelOptions { MaxDegreeOfParallelism = FetchParallelism, CancellationToken = ct },
            async (depotId, innerCt) =>
            {
                picsGids.TryGetValue(depotId, out var picsGid);
                var r = await FetchOneDepotAsync(appId, depotId, picsGid ?? "", branchFiles, innerCt);
                lock (resultLock)
                {
                    done++;
                    if (r != null)
                        fetched.Add(r);
                    else if (picsEmpty)
                        missingUnknown.Add(depotId);
                    else if (IsMainDepot(depotId))
                        missingMain.Add(depotId);
                    else
                        missingDlc.Add(depotId);
                    progress?.Report((done, needList.Count,
                        r == null ? $"depot {depotId} 未找到可用 manifest" : $"depot {depotId} 已获取 ({r.Gid})"));
                }
            });

        fetched.Sort((a, b) => a.DepotId.CompareTo(b.DepotId));
        missingMain.Sort();
        missingDlc.Sort();
        missingUnknown.Sort();
        var depotCacheDir = fetched.Select(f => f.PlacedPath).FirstOrDefault(p => !string.IsNullOrEmpty(p)) is string placed
            ? Path.GetDirectoryName(placed)
            : null;
        return new ManifestRepoFetchResult(true, null, fetched, missingMain, missingDlc, missingUnknown, _rateLimited != 0, depotCacheDir);
    }

    private void NoteRateLimited(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
            Interlocked.Exchange(ref _rateLimited, 1);
    }

    private async Task<FetchedDepot?> FetchOneDepotAsync(
        int appId, int depotId, string picsGid,
        Dictionary<int, string> branchFiles, CancellationToken ct)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"manifestfetch_{appId}");
        Directory.CreateDirectory(tmpDir);

        // 路径 1：PICS 最新 gid 直链（命中则 0 API 开销）
        if (ulong.TryParse(picsGid, out var picsNum) && picsNum != 0)
        {
            var name = $"{depotId}_{picsGid}.manifest";
            var bytes = await TryDownloadBytesAsync(BranchRawUrl(appId.ToString(), name), ct);
            if (TryPrepareManifestBytes(depotId, bytes, out var placed)
                && await PlaceBytesAsync(depotId, picsGid, placed, tmpDir, name) is string saved)
                return new FetchedDepot(depotId, picsGid, RepoDepotKind.Latest, picsGid, saved);
        }

        // 路径 2：分支文件（仓库现有最新，多文件取最大 gid）。
        // PICS 无 gid 时无法比对，按仓库最新处理。
        if (branchFiles.TryGetValue(depotId, out var branchGid))
        {
            var name = $"{depotId}_{branchGid}.manifest";
            var bytes = await TryDownloadBytesAsync(BranchRawUrl(appId.ToString(), name), ct);
            if (TryPrepareManifestBytes(depotId, bytes, out var placed)
                && await PlaceBytesAsync(depotId, branchGid, placed, tmpDir, name) is string saved)
            {
                var kind = string.IsNullOrEmpty(picsGid) || branchGid == picsGid ? RepoDepotKind.Latest : RepoDepotKind.RepoStale;
                return new FetchedDepot(depotId, branchGid, kind, picsGid, saved);
            }
        }

        // 路径 3：Tag 旧版（取最大 gid）
        var tagGid = await GetMaxTagGidAsync(depotId, ct);
        if (tagGid != null)
        {
            var tag = $"{depotId}_{tagGid}";
            var name = $"{tag}.manifest";
            var bytes = await TryDownloadBytesAsync($"https://raw.githubusercontent.com/{Owner}/{Repo}/refs/tags/{tag}/{name}", ct);
            if (TryPrepareManifestBytes(depotId, bytes, out var placed)
                && await PlaceBytesAsync(depotId, tagGid, placed, tmpDir, name) is string saved)
                return new FetchedDepot(depotId, tagGid, RepoDepotKind.OldVersion, picsGid, saved);
        }

        return null;
    }

    private static string BranchRawUrl(string branch, string fileName) =>
        $"https://raw.githubusercontent.com/{Owner}/{Repo}/{branch}/{fileName}";

    private async Task<byte[]?> TryDownloadBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            var bytes = await _httpClientProvider.SendWithProxyRetryAsync(
                ClientName, DownloadTimeout,
                client => client.GetByteArrayAsync(url, ct),
                HttpHeaderHelper.ConfigureBrowser);
            return bytes is { Length: > 0 } ? bytes : null;
        }
        // 只有用户取消才重抛；超时等 OCE 视为单源失败，交由上层降级找旧版
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            LogService.Info("Manifest仓库", $"raw 下载中断（超时或连接被回收） {url}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            LogService.Info("Manifest仓库", $"raw 下载失败 {url}: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> PlaceBytesAsync(int depotId, string gid, byte[] placed, string tmpDir, string fileName)
    {
        try
        {
            var tmpPath = Path.Combine(tmpDir, fileName);
            await File.WriteAllBytesAsync(tmpPath, placed);
            var dest = await _luaFileManager.AddManifestFileAsync(tmpPath);
            try { File.Delete(tmpPath); } catch { }
            if (string.IsNullOrEmpty(dest) || !File.Exists(dest))
            {
                LogService.Warn("Manifest仓库", $"depot {depotId} 未检测到 Steam 路径，无法放入 depotcache");
                return null;
            }
            return dest;
        }
        catch (Exception ex)
        {
            LogService.Warn("Manifest仓库", $"depot {depotId} 文件落盘失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 仓库文件解包+校验：原样可解析直接用；否则尝试去压缩壳（Pro 系为 10 字节头 + raw deflate），
    /// 解出的内容必须能被 SteamKit2 解析才算有效。损坏文件直接判无效，调用方自动降级找旧版。
    /// </summary>
    private static bool TryPrepareManifestBytes(int depotId, byte[]? raw, out byte[] placed)
    {
        placed = null!;
        if (raw == null || raw.Length < 16) return false;

        if (TryParseManifest(raw))
        {
            placed = raw;
            return true;
        }
        // Pro 系仓库格式与标准 zlib 全包、0 偏移 raw deflate 逐个尝试
        foreach (var offset in new[] { 10, 2, 0 })
        {
            if (TryInflateAt(raw, offset, out var inflated) && TryParseManifest(inflated))
            {
                LogService.Info("Manifest仓库", $"depot {depotId} 仓库文件为压缩格式，已解包验证（去头 {offset} 字节）");
                placed = inflated;
                return true;
            }
        }
        LogService.Warn("Manifest仓库", $"depot {depotId} 仓库文件损坏无法解析，已跳过");
        return false;
    }

    private static bool TryParseManifest(byte[] data)
    {
        try
        {
            var m = SteamKit2.DepotManifest.Deserialize(data);
            return m.Files != null && m.ManifestGID != 0;
        }
        catch { return false; }
    }

    private static bool TryInflateAt(byte[] raw, int offset, out byte[] result)
    {
        result = null!;
        // 解压上限 512MB，防畸形炸弹
        const long maxBytes = 512L * 1024 * 1024;
        try
        {
            if (offset < 0 || offset >= raw.Length) return false;
            using var ms = new MemoryStream(raw, offset, raw.Length - offset);
            using var df = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
            using var @out = new MemoryStream();
            var buf = new byte[65536];
            int read;
            long total = 0;
            while ((read = df.Read(buf, 0, buf.Length)) > 0)
            {
                total += read;
                if (total > maxBytes) return false;
                @out.Write(buf, 0, read);
            }
            if (@out.Length == 0) return false;
            result = @out.ToArray();
            return true;
        }
        catch { return false; }
    }

    /// <summary>分支根文件列表（depot→gid），会话内缓存；分支不存在返回 null。</summary>
    private async Task<Dictionary<int, string>?> GetBranchFilesAsync(int appId, CancellationToken ct)
    {
        lock (_treeCacheLock)
        {
            if (_treeCache.TryGetValue(appId, out var cached)) return cached;
        }
        Dictionary<int, string>? result = null;
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/contents/?ref={appId}";
            var json = await _httpClientProvider.SendWithProxyRetryAsync(
                ClientName, ApiTimeout,
                client => client.GetStringAsync(url, ct),
                HttpHeaderHelper.ConfigureBrowser);
            var map = new Dictionary<int, string>();
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.GetString() != "file") continue;
                if (!item.TryGetProperty("name", out var n)) continue;
                var m = ManifestFileRegex.Match(n.GetString() ?? "");
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[1].Value, out var depotId)) continue;
                var gid = m.Groups[2].Value;
                // 同 depot 多文件取最大 gid（十进制可能超 long，按长度+字典序比）
                if (!map.TryGetValue(depotId, out var cur) || CompareGid(gid, cur) > 0)
                    map[depotId] = gid;
            }
            result = map;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            LogService.Info("Manifest仓库", $"游戏 {appId} 在仓库中无分支");
            result = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            NoteRateLimited(ex);
            LogService.Warn("Manifest仓库", $"获取分支文件列表失败: {ex.Message}");
            result = new Dictionary<int, string>();
        }
        lock (_treeCacheLock) { _treeCache[appId] = result; }
        return result;
    }

    /// <summary>Tag 中该 depot 的最大 gid，无则返回 null。</summary>
    private async Task<string?> GetMaxTagGidAsync(int depotId, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/git/matching-refs/tags/{depotId}_";
            var json = await _httpClientProvider.SendWithProxyRetryAsync(
                ClientName, ApiTimeout,
                client => client.GetStringAsync(url, ct),
                HttpHeaderHelper.ConfigureBrowser);
            string? best = null;
            using var doc = JsonDocument.Parse(json);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("ref", out var r)) continue;
                var tag = (r.GetString() ?? "").Split('/').Last();
                var m = ManifestFileRegex.Match(tag);
                if (!m.Success || m.Groups[1].Value != depotId.ToString()) continue;
                var gid = m.Groups[2].Value;
                if (best == null || CompareGid(gid, best) > 0) best = gid;
            }
            return best;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            NoteRateLimited(ex);
            LogService.Info("Manifest仓库", $"depot {depotId} 查询 Tag 失败: {ex.Message}");
            return null;
        }
    }

    // gid 为十进制大整数：先比长度再比字典序
    private static int CompareGid(string a, string b)
    {
        var x = a.TrimStart('0');
        var y = b.TrimStart('0');
        if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
        return string.Compare(x, y, StringComparison.Ordinal);
    }
}
