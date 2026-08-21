using System.IO;
using System.Net.Http;
using System.Runtime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class SteamDepotService : ISteamDepotService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;
    private readonly ISteamAppInfoService _appInfoService;
    private readonly string _cacheFolder;
    private string _currentSource = "DepotKey";
    private readonly Dictionary<string, (string DepotKeysUrl, string TokenKeysUrl)> _resolvedUrls = new();

    private const string KeyIndexUrl = "https://pan.qzyun.net/f/d/MlArs0/key.txt";
    private const string Source2DepotKeysUrl = "https://api.993499094.xyz/depotkeys.json";
    private const string Source2TokenKeysUrl = "https://api.993499094.xyz/appaccesstokens.json";

    private static readonly string[] SourceNames = ["DepotKey", "DepotKey2"];

    // 懒加载常驻字典：首次入库时建，后续复用，文件更新后失效
    private Dictionary<string, string>? _cachedDepotKeys;
    private Dictionary<string, string>? _cachedAppTokens;
    private DateTime _cachedDepotKeysWriteTime;
    private DateTime _cachedAppTokensWriteTime;
    private string? _cachedSource;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public SteamDepotService(ISteamPathService steamPathService, IHttpClientProvider httpClientProvider, ISteamAppInfoService appInfoService)
    {
        _steamPathService = steamPathService;
        _httpClientProvider = httpClientProvider;
        _appInfoService = appInfoService;

        _cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
        if (!Directory.Exists(_cacheFolder))
            Directory.CreateDirectory(_cacheFolder);
    }

    public void UseDataSource(string source)
    {
        _currentSource = source;
    }

    private string GetSourceCacheDir() =>
        Path.Combine(_cacheFolder, _currentSource == "DepotKey2" ? "v2" : "v1");

    private string GetDepotKeysPath() =>
        Path.Combine(GetSourceCacheDir(), "depotkeys.json");

    private string GetTokenKeysPath() =>
        Path.Combine(GetSourceCacheDir(), "appaccesstokens.json");

    private async Task<bool> ResolveKeyUrlsAsync(CancellationToken ct = default)
    {
        if (_resolvedUrls.TryGetValue(_currentSource, out var cached) &&
            !string.IsNullOrEmpty(cached.DepotKeysUrl) &&
            !string.IsNullOrEmpty(cached.TokenKeysUrl))
            return true;

        if (_currentSource == "DepotKey")
        {
            _resolvedUrls[_currentSource] = (Source2DepotKeysUrl, Source2TokenKeysUrl);
            return true;
        }

        try
        {
            var depotKeysUrl = string.Empty;
            var tokenKeysUrl = string.Empty;
            var content = await _httpClientProvider.SendWithProxyRetryAsync(
                $"steam-depot-{_currentSource}",
                TimeSpan.FromSeconds(30),
                client => client.GetStringAsync(KeyIndexUrl, ct),
                HttpHeaderHelper.ConfigureBrowser);
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                var url = line.Trim();
                if (url.EndsWith("depotkeys.json", StringComparison.OrdinalIgnoreCase))
                    depotKeysUrl = url;
                else if (url.EndsWith("appaccesstokens.json", StringComparison.OrdinalIgnoreCase))
                    tokenKeysUrl = url;
            }

            if (!string.IsNullOrEmpty(depotKeysUrl) && !string.IsNullOrEmpty(tokenKeysUrl))
            {
                _resolvedUrls[_currentSource] = (depotKeysUrl, tokenKeysUrl);
                return true;
            }
        }
        catch (Exception ex) { LogService.Warn("入库", $"解析密钥仓库地址失败 ({_currentSource}): {ex.Message}"); }

        return false;
    }

    public async Task<bool> EnsureKeyFilesAsync(CancellationToken ct = default)
    {
        var cacheDir = GetSourceCacheDir();
        var depotPath = GetDepotKeysPath();
        var tokenPath = GetTokenKeysPath();

        if (!Directory.Exists(cacheDir))
            Directory.CreateDirectory(cacheDir);

        if (File.Exists(depotPath) && File.Exists(tokenPath))
            return true;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var result = await UpdateKeyFilesAsync(ct);
                if (result.Success) return true;
            }
            catch (Exception ex) { LogService.Warn("入库", $"更新密钥文件失败 (第{attempt}次): {ex.Message}"); }

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }
        return false;
    }

    public async Task<KeyFileUpdateResult> UpdateKeyFilesAsync(CancellationToken ct = default)
    {
        var result = new KeyFileUpdateResult();

        try
        {
            var cacheDir = GetSourceCacheDir();
            if (!Directory.Exists(cacheDir))
                Directory.CreateDirectory(cacheDir);

            if (!await ResolveKeyUrlsAsync(ct))
            {
                result.Success = false;
                return result;
            }
            var urls = _resolvedUrls[_currentSource];

            var depotPath = GetDepotKeysPath();
            var tokenPath = GetTokenKeysPath();

            // 读取旧文件条目数（流式计数，避免全量字典）
            if (File.Exists(depotPath))
            {
                try
                {
                    await using var fs = File.OpenRead(depotPath);
                    using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
                    result.DepotKeysOldCount = doc.RootElement.ValueKind == JsonValueKind.Object
                        ? doc.RootElement.EnumerateObject().Count() : 0;
                }
                catch (Exception ex) { LogService.Warn("入库", $"解析旧 depot 密钥文件失败: {ex.Message}"); result.DepotKeysOldCount = 0; }
            }
            if (File.Exists(tokenPath))
            {
                try
                {
                    await using var fs = File.OpenRead(tokenPath);
                    using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
                    result.TokenKeysOldCount = doc.RootElement.ValueKind == JsonValueKind.Object
                        ? doc.RootElement.EnumerateObject().Count() : 0;
                }
                catch (Exception ex) { LogService.Warn("入库", $"解析旧 token 密钥文件失败: {ex.Message}"); result.TokenKeysOldCount = 0; }
            }

            // 下载新文件
            var clientName = $"steam-depot-{_currentSource}";
            var depotTask = _httpClientProvider.SendWithProxyRetryAsync(
                clientName,
                TimeSpan.FromSeconds(30),
                client => client.GetByteArrayAsync(urls.DepotKeysUrl, ct),
                HttpHeaderHelper.ConfigureBrowser);
            var tokenTask = _httpClientProvider.SendWithProxyRetryAsync(
                clientName,
                TimeSpan.FromSeconds(30),
                client => client.GetByteArrayAsync(urls.TokenKeysUrl, ct),
                HttpHeaderHelper.ConfigureBrowser);

            await Task.WhenAll(depotTask, tokenTask);

            var depotData = await depotTask;
            await File.WriteAllBytesAsync(depotPath, depotData, ct);
            result.DepotKeysSizeBytes = depotData.Length;

            var tokenData = await tokenTask;
            await File.WriteAllBytesAsync(tokenPath, tokenData, ct);
            result.TokenKeysSizeBytes = tokenData.Length;

            // 解析新文件条目数（轻量计数，避免再建大字典）
            try
            {
                using var doc = JsonDocument.Parse(depotData);
                result.DepotKeysNewCount = doc.RootElement.ValueKind == JsonValueKind.Object
                    ? doc.RootElement.EnumerateObject().Count() : 0;
            }
            catch (Exception ex) { LogService.Warn("入库", $"解析新 depot 密钥文件失败: {ex.Message}"); result.DepotKeysNewCount = 0; }
            try
            {
                using var doc = JsonDocument.Parse(tokenData);
                result.TokenKeysNewCount = doc.RootElement.ValueKind == JsonValueKind.Object
                    ? doc.RootElement.EnumerateObject().Count() : 0;
            }
            catch (Exception ex) { LogService.Warn("入库", $"解析新 token 密钥文件失败: {ex.Message}"); result.TokenKeysNewCount = 0; }

            // 释放大对象引用，使旧缓存与下载缓冲尽快可回收
            depotData = null!;
            tokenData = null!;
            InvalidateKeyCache();
            // 刷新为低频操作，强制回收并压缩 LOH，避免 230→300 新旧字典叠加
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            LogService.Error("入库", $"更新密钥文件失败: {ex.Message}");
            result.Success = false;
            return result;
        }
    }

    public async Task EnsureAllSourcesAsync(CancellationToken ct = default)
    {
        foreach (var source in SourceNames)
        {
            UseDataSource(source);
            try { await EnsureKeyFilesAsync(ct); }
            catch (Exception ex) { LogService.Warn("入库", $"后台更新密钥文件失败 ({source}): {ex.Message}"); }
        }
    }

    public async Task<DepotQueryResult?> QueryAppAsync(int appId, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            var response = await _httpClientProvider.SendWithProxyRetryAsync(
                $"steam-depot-{_currentSource}",
                TimeSpan.FromSeconds(30),
                client => client.GetStringAsync(url, ct),
                HttpHeaderHelper.ConfigureBrowser);
            using var doc = JsonDocument.Parse(response);

            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty(appId.ToString(), out var app)) return null;

            var result = new DepotQueryResult { AppId = appId };

            if (app.TryGetProperty("common", out var common) && common.TryGetProperty("name", out var name))
                result.AppName = name.GetString() ?? $"App {appId}";

            if (app.TryGetProperty("depots", out var depots))
            {
                foreach (var depotProp in depots.EnumerateObject())
                {
                    var depotIdStr = depotProp.Name;
                    if (!int.TryParse(depotIdStr, out var depotId)) continue;

                    var depot = depotProp.Value;
                    if (depot.TryGetProperty("depotfromapp", out _)) continue;

                    var dki = new DepotKeyInfo { DepotId = depotId };

                    if (depot.TryGetProperty("manifests", out var manifests) &&
                        manifests.TryGetProperty("public", out var pub) &&
                        pub.TryGetProperty("gid", out var gid))
                    {
                        dki.ManifestId = gid.GetString() ?? "";
                    }

                    result.GameDepots.Add(dki);
                }
            }

            if (app.TryGetProperty("extended", out var ext) && ext.TryGetProperty("listofdlc", out var dlcList))
            {
                if (dlcList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dlcIdStr in dlcList.EnumerateArray())
                    {
                        if (int.TryParse(dlcIdStr.GetString(), out var dlcId))
                            result.DlcAppIds.Add(dlcId);
                    }
                }
                else if (dlcList.ValueKind == JsonValueKind.String)
                {
                    foreach (var idStr in dlcList.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (int.TryParse(idStr.Trim(), out var dlcId))
                            result.DlcAppIds.Add(dlcId);
                    }
                }
            }

            // 兜底：api.steamcmd.net 返回的 depots 为空（新游戏/受 token 保护）时，
            // 用本地 appaccesstokens.json 里的 token 向 Steam 服务器查询完整 depots 与 DLC。
            if (result.GameDepots.Count == 0)
            {
                LogService.Info("入库", $"AppID {appId} 主仓库为空，尝试 SteamKit2 兜底...");
                var token = await LoadLocalAppTokenAsync(appId, ct);
                if (token != 0)
                {
                    var full = await _appInfoService.QueryFullAppInfoAsync(appId, token, ct);
                    if (full != null)
                    {
                        result.AppName = string.IsNullOrEmpty(result.AppName) ? full.AppName : result.AppName;
                        foreach (var depotId in full.DepotIds)
                            result.GameDepots.Add(new DepotKeyInfo { DepotId = depotId });
                        foreach (var dlcId in full.DlcAppIds)
                            if (!result.DlcAppIds.Contains(dlcId))
                                result.DlcAppIds.Add(dlcId);
                        LogService.Info("入库", $"AppID {appId} SteamKit2 兜底: depots={full.DepotIds.Count}, dlc={full.DlcAppIds.Count}");
                    }
                    else
                    {
                        LogService.Warn("入库", $"AppID {appId} SteamKit2 兜底查询返回 null");
                    }
                }
                else
                {
                    LogService.Warn("入库", $"AppID {appId} 本地无 token，跳过兜底");
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"查询 AppID {appId} 失败：{ex.Message}", ex);
        }
    }

    /// <summary>按需从 appaccesstokens.json 读取某 app 的 token（流式，避免全表字典）。</summary>
    private async Task<ulong> LoadLocalAppTokenAsync(int appId, CancellationToken ct = default)
    {
        try
        {
            var tokenPath = GetTokenKeysPath();
            if (!File.Exists(tokenPath)) return 0;
            await using var fs = File.OpenRead(tokenPath);
            using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty(appId.ToString(), out var val))
            {
                var tokenStr = val.GetString();
                if (!string.IsNullOrEmpty(tokenStr) && ulong.TryParse(tokenStr, out var token))
                    return token;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("入库", $"读取本地 token 文件失败: {ex.Message}");
        }
        return 0;
    }

    public Task<string?> GenerateLuaAsync(int appId, string? keyFolderPath = null, CancellationToken ct = default)
        => BuildLuaCoreAsync(appId, withDlc: false, ct);

    public Task<string?> GenerateLuaWithDlcAsync(int appId, string? keyFolderPath = null, CancellationToken ct = default)
        => BuildLuaCoreAsync(appId, withDlc: true, ct);

    private async Task<string?> BuildLuaCoreAsync(int appId, bool withDlc, CancellationToken ct)
    {
        try
        {
            if (!await EnsureKeyFilesAsync(ct))
                return null;

            var (depotKeys, appTokens) = await LoadKeyDictionariesAsync(ct);
            if (depotKeys == null || appTokens == null) return null;

            var queryResult = await QueryAppAsync(appId, ct);
            if (queryResult == null) return null;

            var sb = new StringBuilder();
            sb.AppendLine("-- lua by Fluent-Steam-Lua (https://github.com/huanyuejue/Fluent-Steam-Lua)");
            sb.AppendLine();
            var matchedItems = 0;

            // 主 AppID
            if (depotKeys.TryGetValue(appId.ToString(), out var mainKey))
            {
                sb.AppendLine($"addappid({appId}, 1, \"{mainKey}\")");
                matchedItems++;
            }
            else
            {
                sb.AppendLine($"addappid({appId})");
            }

            // 主游戏 depots
            foreach (var depot in queryResult.GameDepots)
            {
                if (depotKeys.TryGetValue(depot.DepotId.ToString(), out var key))
                {
                    sb.AppendLine($"addappid({depot.DepotId}, 1, \"{key}\")");
                    depot.Key = key;
                    depot.IsMatched = true;
                    matchedItems++;
                }
            }

            // App token
            if (appTokens.TryGetValue(appId.ToString(), out var token))
            {
                sb.AppendLine($"addtoken({appId}, \"{token}\")");
                queryResult.AppToken = token;
                matchedItems++;
            }

            // DLC 段
            if (withDlc && queryResult.DlcAppIds.Count > 0)
            {
                var mainDepotIds = new HashSet<int>(queryResult.GameDepots.Select(d => d.DepotId));
                foreach (var dlcAppId in queryResult.DlcAppIds)
                {
                    if (!mainDepotIds.Contains(dlcAppId))
                    {
                        sb.AppendLine($"addappid({dlcAppId})");

                        if (depotKeys.TryGetValue(dlcAppId.ToString(), out var dlcMainKey))
                        {
                            sb.AppendLine($"addappid({dlcAppId}, 1, \"{dlcMainKey}\")");
                            matchedItems++;
                        }
                        if (appTokens.TryGetValue(dlcAppId.ToString(), out var dlcToken))
                        {
                            sb.AppendLine($"addtoken({dlcAppId}, \"{dlcToken}\")");
                            matchedItems++;
                        }
                    }

                    var dlcResult = await QueryAppAsync(dlcAppId, ct);
                    if (dlcResult != null)
                    {
                        foreach (var depot in dlcResult.GameDepots)
                        {
                            if (depotKeys.TryGetValue(depot.DepotId.ToString(), out var depKey))
                            {
                                sb.AppendLine($"addappid({depot.DepotId}, 1, \"{depKey}\")");
                                matchedItems++;
                            }
                        }
                    }
                }
            }

            if (matchedItems == 0)
                throw new InvalidOperationException(
                    withDlc
                        ? $"本地缓存未找到 AppID {appId}、其 depots 或 DLC 的任何密钥或 token，已停止生成入库文件"
                        : $"本地缓存未找到 AppID {appId} 及其 depots 的任何密钥或 token，已停止生成入库文件");

            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder)) return null;

            if (!Directory.Exists(luaFolder))
                Directory.CreateDirectory(luaFolder);

            var luaPath = Path.Combine(luaFolder, $"{appId}.lua");
            await File.WriteAllTextAsync(luaPath, sb.ToString(), ct);

            return luaPath;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            LogService.Error("入库", $"生成入库文件失败 (AppID {appId}{(withDlc ? ", DLC" : "")}): {ex.Message}");
            return null;
        }
    }

    private async Task<(Dictionary<string, string>? DepotKeys, Dictionary<string, string>? AppTokens)> LoadKeyDictionariesAsync(CancellationToken ct)
    {
        var depotPath = GetDepotKeysPath();
        var tokenPath = GetTokenKeysPath();

        // 缓存命中：同源且文件未变更则直接复用
        var depotWrite = File.Exists(depotPath) ? File.GetLastWriteTimeUtc(depotPath) : DateTime.MinValue;
        var tokenWrite = File.Exists(tokenPath) ? File.GetLastWriteTimeUtc(tokenPath) : DateTime.MinValue;
        if (_cachedDepotKeys != null && _cachedAppTokens != null
            && _cachedSource == _currentSource
            && _cachedDepotKeysWriteTime == depotWrite
            && _cachedAppTokensWriteTime == tokenWrite)
        {
            return (_cachedDepotKeys, _cachedAppTokens);
        }

        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双检
            depotWrite = File.Exists(depotPath) ? File.GetLastWriteTimeUtc(depotPath) : DateTime.MinValue;
            tokenWrite = File.Exists(tokenPath) ? File.GetLastWriteTimeUtc(tokenPath) : DateTime.MinValue;
            if (_cachedDepotKeys != null && _cachedAppTokens != null
                && _cachedSource == _currentSource
                && _cachedDepotKeysWriteTime == depotWrite
                && _cachedAppTokensWriteTime == tokenWrite)
            {
                return (_cachedDepotKeys, _cachedAppTokens);
            }

            await using var depotStream = File.OpenRead(depotPath);
            await using var tokenStream = File.OpenRead(tokenPath);
            var depotKeys = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(depotStream, cancellationToken: ct)
                ?? new Dictionary<string, string>();
            var appTokens = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(tokenStream, cancellationToken: ct)
                ?? new Dictionary<string, string>();

            _cachedDepotKeys = depotKeys;
            _cachedAppTokens = appTokens;
            _cachedDepotKeysWriteTime = depotWrite;
            _cachedAppTokensWriteTime = tokenWrite;
            _cachedSource = _currentSource;
            return (depotKeys, appTokens);
        }
        catch (Exception ex)
        {
            LogService.Warn("入库", $"读取密钥文件失败: {ex.Message}");
            return (null, null);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private void InvalidateKeyCache()
    {
        _cachedDepotKeys = null;
        _cachedAppTokens = null;
        _cachedSource = null;
    }

    public async Task<DlcFetchResult> FetchDlcAsync(string luaPath, int dlcAppId, bool hasOwnDepot, CancellationToken ct = default)
    {
        var result = new DlcFetchResult();
        try
        {
            // 1. 无独立 depot → 无需密钥，直接写入
            if (!hasOwnDepot)
            {
                result.NeedKey = false;
                await AppendLinesToLuaAsync(luaPath, new List<string> { $"addappid({dlcAppId})" }, ct);
                result.Success = true;
                result.Message = $"DLC {dlcAppId} 无独立 depot，无需密钥，已写入";
                return result;
            }

            // 2. 有独立 depot → 需要密钥，从本地密钥仓库 v1 搜索
            result.NeedKey = true;

            var prevSource = _currentSource;
            UseDataSource("DepotKey");
            try
            {
                if (!await EnsureKeyFilesAsync(ct))
                {
                    result.Message = "无法获取密钥仓库文件";
                    return result;
                }

                string? dlcMainKey = null;
                try
                {
                    await using var fs = File.OpenRead(GetDepotKeysPath());
                    using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty(dlcAppId.ToString(), out var val))
                        dlcMainKey = val.GetString();
                }
                catch (Exception ex)
                {
                    LogService.Warn("获取DLC", $"读取密钥文件失败: {ex.Message}");
                    result.Message = "读取密钥仓库文件失败";
                    return result;
                }

                // 查找 DLC 自身主 AppID 的密钥
                if (!string.IsNullOrEmpty(dlcMainKey))
                {
                    var lines = new List<string> { $"addappid({dlcAppId}, 1, \"{dlcMainKey}\")" };
                    await AppendLinesToLuaAsync(luaPath, lines, ct);
                    result.Success = true;
                    result.Message = $"DLC {dlcAppId} 密钥获取成功，已写入";
                }
                else
                {
                    result.Message = $"无法获取 DLC {dlcAppId} 的密钥信息（本地密钥仓库 v1 中未找到），获取失败";
                }
            }
            finally
            {
                UseDataSource(prevSource);
            }

            return result;
        }
        catch (Exception ex)
        {
            LogService.Error("获取DLC", $"获取 DLC {dlcAppId} 失败: {ex.Message}");
            result.Message = $"获取失败：{ex.Message}";
            return result;
        }
    }

    private static async Task AppendLinesToLuaAsync(string luaPath, List<string> lines, CancellationToken ct = default)
    {
        if (lines == null || lines.Count == 0) return;

        var dir = Path.GetDirectoryName(luaPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var existing = File.Exists(luaPath) ? await File.ReadAllTextAsync(luaPath, ct) : string.Empty;
        var newLines = new List<string>();

        foreach (var line in lines)
        {
            var match = Regex.Match(line, @"addappid\((\d+)");
            if (match.Success && Regex.IsMatch(existing, $@"\baddappid\(\s*{match.Groups[1].Value}\s*[,\)]"))
                continue; // 已存在则跳过
            newLines.Add(line);
        }

        if (newLines.Count == 0)
        {
            LogService.Info("获取DLC", "所有行已存在于 Lua 文件中，跳过写入");
            return;
        }

        var sb = new StringBuilder(existing.TrimEnd());
        if (sb.Length > 0)
            sb.AppendLine();
        sb.AppendLine(string.Join(Environment.NewLine, newLines));

        await File.WriteAllTextAsync(luaPath, sb.ToString(), ct);
        LogService.Info("获取DLC", $"已写入 {newLines.Count} 行到 {luaPath}");
    }
}
