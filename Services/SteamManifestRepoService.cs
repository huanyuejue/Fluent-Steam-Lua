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
    // manifest 文件很小（KB~MB 级），20s 无响应即判定该路不通快速降级；
    // provider 内部还有 3 次重试，单 URL 最坏 60s，一定比用户手动取消的耐心短。
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(20);
    // 分阶段总预算（provider 重试是静默的，这里兜底保证每阶段最多静默这么久就有动静；
    // 用户取消走 ct 立刻中断，不受预算影响）。
    private static readonly TimeSpan ApiPhaseBudget = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan TagPhaseBudget = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan PathPhaseBudget = TimeSpan.FromSeconds(25);
    private const int FetchParallelism = 5;

    private static readonly Regex ManifestFileRegex = new(@"^(\d+)_(\d+)\.manifest$", RegexOptions.IgnoreCase);

    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamDepotService _depotService;
    private readonly ILuaFileManager _luaFileManager;
    private readonly ISettingsService _settingsService;

    private readonly object _treeCacheLock = new();
    private readonly Dictionary<int, (Dictionary<int, string>? Files, bool ListOk)> _treeCache = new();

    // GitHub API 403 限流标记：单次 Fetch 内有效，调用方据此提示"缺失或为误判"
    private int _rateLimited;

    public SteamManifestRepoService(
        IHttpClientProvider httpClientProvider,
        ISteamDepotService depotService,
        ILuaFileManager luaFileManager,
        ISettingsService settingsService)
    {
        _httpClientProvider = httpClientProvider;
        _depotService = depotService;
        _luaFileManager = luaFileManager;
        _settingsService = settingsService;
    }

    public async Task<ManifestRepoFetchResult> FetchManifestsAsync(
        int appId,
        IReadOnlyList<int> luaAppIds,
        IProgress<(int done, int total, string text)>? progress,
        CancellationToken ct = default)
    {
        _rateLimited = 0;
        // 首选镜像源一次读出，本轮获取内保持一致（中途改设置下次生效）
        var preferredMirror = _settingsService.Load().ManifestMirror;
        // 20s 仍未进入逐 depot 获取：大概率网络受限，提示开 VPN/代理（只提示一次，不中断；
        // 提前返回/进入获取循环时置位抑制，避免事后误报）。
        var hintSuppressed = 0;
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ =>
        {
            if (Interlocked.CompareExchange(ref hintSuppressed, 0, 0) == 0)
            {
                try { progress?.Report((0, 1, "等待 20s 仍未开始获取，网络可能受限，可尝试开启 VPN/代理，或在设置 → 接口设置中切换镜像加速源后重试…")); }
                catch { }
            }
        }, TaskScheduler.Default);

        // 1. PICS：base 仓库 gid + DLC 列表（最新性比对与主/DLC 分类都靠它）。
        // PICS 查询抛异常（SSL/连接/超时等）也按查不到处理，给友好提示而非英文原文弹窗。
        progress?.Report((0, 1, "正在查询游戏仓库信息…"));
        DepotQueryResult? basePics = null;
        try
        {
            basePics = await _depotService.QueryAppAsync(appId, ct).WaitAsync(ApiPhaseBudget, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogService.Warn("Manifest仓库", $"PICS 查询失败，已按网络问题处理: {ex}");
        }
        if (basePics == null)
        {
            Interlocked.Exchange(ref hintSuppressed, 1);
            return new ManifestRepoFetchResult(false, "无法查询该游戏的仓库信息，请检查网络（可尝试开启 VPN/代理，或在设置 → 接口设置中切换镜像加速源）后重试",
                [], [], [], [], [], false, null);
        }

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
            progress?.Report((0, 1, $"正在查询 {dlcLuaIds.Count} 个 DLC 的仓库信息…"));
            var luaIdSet = new HashSet<int>(luaAppIds);
            var subResults = new Dictionary<int, DepotQueryResult?>();
            await Parallel.ForEachAsync(dlcLuaIds,
                new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
                async (dlcId, innerCt) =>
                {
                    DepotQueryResult? sub = null;
                    try { sub = await _depotService.QueryAppAsync(dlcId, innerCt).WaitAsync(ApiPhaseBudget, innerCt); }
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
        {
            Interlocked.Exchange(ref hintSuppressed, 1);
            return new ManifestRepoFetchResult(true, null, [], [], [], [], [], false, null);
        }

        // 2. 分支存在性：404 即整游戏缺失，直接返回；
        // 非 404 失败（限流/断网）不按空分支处理，后续各 depot 记未知而非缺失。
        progress?.Report((0, 1, "正在获取仓库文件列表…"));
        var (branchFiles, branchListOk) = await GetBranchFilesAsync(appId, ct).WaitAsync(ApiPhaseBudget, ct);
        if (branchFiles == null)
        {
            Interlocked.Exchange(ref hintSuppressed, 1);
            return new ManifestRepoFetchResult(false, $"仓库中没有游戏 {appId} 的分支，无法获取",
                [], [], [], [], [], false, null);
        }

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
        var unknownIds = new List<int>();
        int done = 0;
        var resultLock = new object();
        Interlocked.Exchange(ref hintSuppressed, 1);
        progress?.Report((0, needList.Count, $"共 {needList.Count} 个仓库，开始获取…"));
        int GetDone() { lock (resultLock) { return done; } }

        await Parallel.ForEachAsync(needList,
            new ParallelOptions { MaxDegreeOfParallelism = FetchParallelism, CancellationToken = ct },
            async (depotId, innerCt) =>
            {
                picsGids.TryGetValue(depotId, out var picsGid);
                var r = await FetchOneDepotAsync(appId, depotId, picsGid ?? "", branchFiles, branchListOk, progress, GetDone, needList.Count, preferredMirror, innerCt);
                lock (resultLock)
                {
                    done++;
                    if (r.Fetched != null)
                        fetched.Add(r.Fetched);
                    else if (r.Unknown)
                        unknownIds.Add(depotId);
                    else if (picsEmpty)
                        missingUnknown.Add(depotId);
                    else if (IsMainDepot(depotId))
                        missingMain.Add(depotId);
                    else
                        missingDlc.Add(depotId);
                    progress?.Report((done, needList.Count,
                        r.Fetched != null ? $"depot {depotId} 已获取 ({r.Fetched.Gid})"
                        : r.Unknown ? $"depot {depotId} 网络未知，稍后重试"
                        : $"depot {depotId} 未找到可用 manifest"));
                }
            });

        fetched.Sort((a, b) => a.DepotId.CompareTo(b.DepotId));
        missingMain.Sort();
        missingDlc.Sort();
        missingUnknown.Sort();
        unknownIds.Sort();
        var depotCacheDir = fetched.Select(f => f.PlacedPath).FirstOrDefault(p => !string.IsNullOrEmpty(p)) is string placed
            ? Path.GetDirectoryName(placed)
            : null;
        return new ManifestRepoFetchResult(true, null, fetched, missingMain, missingDlc, missingUnknown, unknownIds, _rateLimited != 0, depotCacheDir);
    }

    private void NoteRateLimited(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
            Interlocked.Exchange(ref _rateLimited, 1);
    }

    // 测速探测文件：真实存在的小 manifest（分支 1433130 下）。仓库删了它则全源失败，
    // 与"均不可达"同一种展示，换个稳定文件即可，不影响主流程。
    private const string SpeedTestBranch = "1433130";
    private const string SpeedTestFile = "1433131_5676412693243015272.manifest";

    /// <summary>直连 + 各镜像并发下载同一探测文件；单个 15s 超时，互不等待。</summary>
    public async Task<List<(string Name, long LatencyMs, bool IsSuccess)>> TestMirrorSpeedAsync(
        IProgress<(string Name, long LatencyMs, bool IsSuccess)>? progress = null)
    {
        var rawProbe = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{SpeedTestBranch}/{SpeedTestFile}";
        // 测速与用户偏好无关：全源都测，默认顺序即直连首位
        var urls = GitHubMirror.ManifestRawMirrors(rawProbe);

        var taskList = urls.Select(async url =>
        {
            var name = GitHubMirror.SourceDisplayName(url);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                    ClientName, TimeSpan.FromSeconds(15),
                    client => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead),
                    HttpHeaderHelper.ConfigureBrowser);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();
                sw.Stop();
                return (name, sw.ElapsedMilliseconds, bytes.Length > 0);
            }
            catch
            {
                sw.Stop();
                return (name, sw.ElapsedMilliseconds, false);
            }
        }).Select(async task =>
        {
            var result = await task;
            progress?.Report(result);
            return result;
        }).ToList();

        var pending = new List<Task<(string Name, long LatencyMs, bool IsSuccess)>>(taskList);
        var results = new List<(string Name, long LatencyMs, bool IsSuccess)>();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            results.Add(await done);
        }
        return results;
    }

    // 单 depot 获取结果：任何一条路径网络未知都记未知（未知优先于缺失，不诱导删行）
    private record DepotFetchOutcome(FetchedDepot? Fetched, bool Unknown);

    // 单文件下载三态：404 确认不存在；超时/连接等网络问题记未知
    private enum RepoDownloadKind { Found, NotFound, Unknown }
    private record RepoDownload(RepoDownloadKind Kind, byte[]? Bytes);

    private async Task<DepotFetchOutcome> FetchOneDepotAsync(
        int appId, int depotId, string picsGid,
        Dictionary<int, string> branchFiles, bool branchListOk,
        IProgress<(int done, int total, string text)>? progress, Func<int> getDone, int total,
        string preferredMirror,
        CancellationToken ct)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"manifestfetch_{appId}");
        Directory.CreateDirectory(tmpDir);

        FetchedDepot? fetched = null;
        var unknown = false;

        // 单路径尝试：同文件多源按顺序试，首个成功落盘即返回；
        // 网络未知/确认不存在记证据后找下一条路。
        // 日志只记关键节点：成功走的源、未知失败的源、整条路全灭；例行的 404 不刷屏。
        async Task<bool> TryPlaceAsync(string gid, RepoDepotKind kind, string fileName, List<string> urls, string label)
        {
            foreach (var url in urls)
            {
                var host = new Uri(url).Host;
                try { progress?.Report((getDone(), total, $"depot {depotId} 正在尝试{label}（{host}）…")); } catch { }
                // 单路总预算：provider 内部静默重试叠起来太久，这里到时换下一条路（超时记未知）
                RepoDownload dl;
                try
                {
                    dl = await TryDownloadBytesAsync(url, ct).WaitAsync(PathPhaseBudget, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    LogService.Info("Manifest仓库", $"depot {depotId} {label}经 {host} 超时未完成，已换下一源: {ex.Message}");
                    unknown = true;
                    continue;
                }
                if (dl.Kind == RepoDownloadKind.Unknown)
                {
                    LogService.Info("Manifest仓库", $"depot {depotId} {label}经 {host} 失败，已换下一源");
                    unknown = true;
                    continue;
                }
                // 直连源的 404 是权威的：镜像取的都是源站，不可能有源站没有的文件，直接整条路结束
                if (dl.Kind == RepoDownloadKind.NotFound || dl.Bytes == null)
                {
                    if (GitHubMirror.IsDirectUrl(url)) return false;
                    continue;
                }
                if (TryPrepareManifestBytes(depotId, dl.Bytes, out var placed)
                    && await PlaceBytesAsync(depotId, gid, placed, tmpDir, fileName) is string saved)
                {
                    LogService.Info("Manifest仓库", $"depot {depotId} 通过{label}（{host}）获取成功 ({gid})");
                    fetched = new FetchedDepot(depotId, gid, kind, picsGid, saved);
                    return true;
                }
            }
            LogService.Info("Manifest仓库", $"depot {depotId} {label}全部 {urls.Count} 个源均失败");
            return false;
        }

        // 路径 1：PICS 最新 gid 直链（命中则 0 API 开销），直连失败顺延公共镜像
        if (ulong.TryParse(picsGid, out var picsNum) && picsNum != 0)
        {
            var name = $"{depotId}_{picsGid}.manifest";
            if (await TryPlaceAsync(picsGid, RepoDepotKind.Latest, name,
                GitHubMirror.ManifestRawMirrors($"https://raw.githubusercontent.com/{Owner}/{Repo}/{appId}/{name}", preferredMirror), "直链"))
                return new DepotFetchOutcome(fetched, false);
        }

        // 路径 2：分支文件（仓库现有最新，多文件取最大 gid）。
        // PICS 无 gid 时无法比对，按仓库最新处理。
        // 分支列表本身失败时无法确认有无，记未知而非缺失。
        if (branchListOk)
        {
            if (branchFiles.TryGetValue(depotId, out var branchGid))
            {
                var name = $"{depotId}_{branchGid}.manifest";
                var kind = string.IsNullOrEmpty(picsGid) || branchGid == picsGid ? RepoDepotKind.Latest : RepoDepotKind.RepoStale;
                if (await TryPlaceAsync(branchGid, kind, name,
                    GitHubMirror.ManifestRawMirrors($"https://raw.githubusercontent.com/{Owner}/{Repo}/{appId}/{name}", preferredMirror), "分支文件"))
                    return new DepotFetchOutcome(fetched, false);
            }
            // 分支列表成功但无此文件 = 该分支确实没有
        }
        else unknown = true;

        // 路径 3：Tag 旧版（取最大 gid）
        var (tagGid, tagApiOk) = await GetMaxTagGidAsync(depotId, ct);
        var tagAbsent = tagApiOk && tagGid == null;
        if (!tagApiOk) unknown = true;
        else if (tagGid != null)
        {
            var tag = $"{depotId}_{tagGid}";
            var name = $"{tag}.manifest";
            if (await TryPlaceAsync(tagGid, RepoDepotKind.OldVersion, name,
                GitHubMirror.ManifestRawMirrors($"https://raw.githubusercontent.com/{Owner}/{Repo}/refs/tags/{tag}/{name}", preferredMirror), "旧版"))
                return new DepotFetchOutcome(fetched, false);
        }
        // Tag 查询成功但无 tag = 确实没有旧版

        // 双清单都确认没有 → 缺失（直链超时等单路未知不再翻盘）；
        // 否则有未知记未知，全 404 记缺失
        var branchAbsent = branchListOk && !branchFiles.ContainsKey(depotId);
        if (branchAbsent && tagAbsent)
            return new DepotFetchOutcome(null, false);
        return new DepotFetchOutcome(null, unknown);
    }

    private async Task<RepoDownload> TryDownloadBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            // 用 GetAsync 自行判状态码：404 是"确认不存在"，必须与网络失败区分开
            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                ClientName, DownloadTimeout,
                client => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct),
                HttpHeaderHelper.ConfigureBrowser);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new RepoDownload(RepoDownloadKind.NotFound, null);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes is { Length: > 0 }
                ? new RepoDownload(RepoDownloadKind.Found, bytes)
                : new RepoDownload(RepoDownloadKind.NotFound, null);
        }
        // 只有用户取消才重抛；超时等 OCE 记未知，交由上层归入未知桶而非缺失
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex)
        {
            LogService.Info("Manifest仓库", $"raw 下载中断（超时或连接被回收） {url}: {ex.Message}");
            return new RepoDownload(RepoDownloadKind.Unknown, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new RepoDownload(RepoDownloadKind.NotFound, null);
        }
        catch (Exception ex)
        {
            LogService.Info("Manifest仓库", $"raw 下载失败 {url}: {ex.Message}");
            return new RepoDownload(RepoDownloadKind.Unknown, null);
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

    /// <summary>分支根文件列表（depot→gid），会话内缓存；分支不存在返回 null 文件表。
    /// 非 404 失败（限流/断网）返回 ListOk=false，调用方记未知而非缺失；失败结果不缓存。</summary>
    private async Task<(Dictionary<int, string>? Files, bool ListOk)> GetBranchFilesAsync(int appId, CancellationToken ct)
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
            return (new Dictionary<int, string>(), false);
        }
        lock (_treeCacheLock) { _treeCache[appId] = (result, true); }
        return (result, true);
    }

    /// <summary>Tag 中该 depot 的最大 gid；ApiOk=false 表示查询本身失败（无法确认有无）。</summary>
    private async Task<(string? Gid, bool ApiOk)> GetMaxTagGidAsync(int depotId, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/git/matching-refs/tags/{depotId}_";
            var json = await _httpClientProvider.SendWithProxyRetryAsync(
                ClientName, ApiTimeout,
                client => client.GetStringAsync(url, ct),
                HttpHeaderHelper.ConfigureBrowser).WaitAsync(TagPhaseBudget, ct);
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
            return (best, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            NoteRateLimited(ex);
            LogService.Info("Manifest仓库", $"depot {depotId} 查询 Tag 失败: {ex.Message}");
            return (null, false);
        }
    }

    // gid 为十进制大整数：先比长度再比字典序
    private static int CompareGid(string a, string b) => FetchedDepot.CompareGid(a, b);
}
