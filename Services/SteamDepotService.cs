using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class SteamDepotService : ISteamDepotService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;
    private readonly string _cacheFolder;
    private string _currentSource = "DepotKey";
    private readonly Dictionary<string, (string DepotKeysUrl, string TokenKeysUrl)> _resolvedUrls = new();

    private const string KeyIndexUrl = "https://pan.qzyun.net/f/d/MlArs0/key.txt";
    private const string Source2DepotKeysUrl = "https://api.993499094.xyz/depotkeys.json";
    private const string Source2TokenKeysUrl = "https://api.993499094.xyz/appaccesstokens.json";

    private static readonly string[] SourceNames = ["DepotKey", "DepotKey2"];

    public SteamDepotService(ISteamPathService steamPathService, IHttpClientProvider httpClientProvider)
    {
        _steamPathService = steamPathService;
        _httpClientProvider = httpClientProvider;

        _cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
        if (!Directory.Exists(_cacheFolder))
            Directory.CreateDirectory(_cacheFolder);
    }

    private static void ConfigureHeaders(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
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
                ConfigureHeaders);
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

            // 读取旧文件条目数
            if (File.Exists(depotPath))
            {
                try
                {
                    var oldContent = await File.ReadAllTextAsync(depotPath, ct);
                    var oldDict = JsonSerializer.Deserialize<Dictionary<string, string>>(oldContent);
                    result.DepotKeysOldCount = oldDict?.Count ?? 0;
                }
                catch (Exception ex) { LogService.Warn("入库", $"解析旧 depot 密钥文件失败: {ex.Message}"); result.DepotKeysOldCount = 0; }
            }
            if (File.Exists(tokenPath))
            {
                try
                {
                    var oldContent = await File.ReadAllTextAsync(tokenPath, ct);
                    var oldDict = JsonSerializer.Deserialize<Dictionary<string, string>>(oldContent);
                    result.TokenKeysOldCount = oldDict?.Count ?? 0;
                }
                catch (Exception ex) { LogService.Warn("入库", $"解析旧 token 密钥文件失败: {ex.Message}"); result.TokenKeysOldCount = 0; }
            }

            // 下载新文件
            var clientName = $"steam-depot-{_currentSource}";
            var depotTask = _httpClientProvider.SendWithProxyRetryAsync(
                clientName,
                TimeSpan.FromSeconds(30),
                client => client.GetByteArrayAsync(urls.DepotKeysUrl, ct),
                ConfigureHeaders);
            var tokenTask = _httpClientProvider.SendWithProxyRetryAsync(
                clientName,
                TimeSpan.FromSeconds(30),
                client => client.GetByteArrayAsync(urls.TokenKeysUrl, ct),
                ConfigureHeaders);

            await Task.WhenAll(depotTask, tokenTask);

            var depotData = await depotTask;
            await File.WriteAllBytesAsync(depotPath, depotData, ct);
            result.DepotKeysSizeBytes = depotData.Length;

            var tokenData = await tokenTask;
            await File.WriteAllBytesAsync(tokenPath, tokenData);
            result.TokenKeysSizeBytes = tokenData.Length;

            // 解析新文件条目数
            try
            {
                var newDict = JsonSerializer.Deserialize<Dictionary<string, string>>(depotData);
                result.DepotKeysNewCount = newDict?.Count ?? 0;
            }
            catch (Exception ex) { LogService.Warn("入库", $"解析新 depot 密钥文件失败: {ex.Message}"); result.DepotKeysNewCount = 0; }
            try
            {
                var newDict = JsonSerializer.Deserialize<Dictionary<string, string>>(tokenData);
                result.TokenKeysNewCount = newDict?.Count ?? 0;
            }
            catch (Exception ex) { LogService.Warn("入库", $"解析新 token 密钥文件失败: {ex.Message}"); result.TokenKeysNewCount = 0; }

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
                ConfigureHeaders);
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

            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"查询 AppID {appId} 失败：{ex.Message}", ex);
        }
    }

    public async Task<string?> GenerateLuaAsync(int appId, string? keyFolderPath = null, CancellationToken ct = default)
    {
        try
        {
            if (!await EnsureKeyFilesAsync(ct))
                return null;

            var depotKeysPath = GetDepotKeysPath();
            var tokenKeysPath = GetTokenKeysPath();

            Dictionary<string, string> depotKeys;
            Dictionary<string, string> appTokens;
            try
            {
                depotKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(depotKeysPath))
                    ?? new Dictionary<string, string>();
                appTokens = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(tokenKeysPath))
                    ?? new Dictionary<string, string>();
            }
            catch (Exception ex) { LogService.Warn("入库", $"读取密钥文件失败: {ex.Message}"); return null; }

            var queryResult = await QueryAppAsync(appId, ct);
            if (queryResult == null) return null;

            var sb = new StringBuilder();
            sb.AppendLine("-- lua by Fluent-Steam-Lua (https://github.com/huanyuejue/Fluent-Steam-Lua)");
            sb.AppendLine();
            var matchedItems = 0;

            if (depotKeys.TryGetValue(appId.ToString(), out var mainKey))
            {
                sb.AppendLine($"addappid({appId}, 1, \"{mainKey}\")");
                matchedItems++;
            }
            else
            {
                // 主 AppID 没有 key 时，仍允许依赖子 depot key 继续生成
                sb.AppendLine($"addappid({appId})");
            }

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

            if (appTokens.TryGetValue(appId.ToString(), out var token))
            {
                sb.AppendLine($"addtoken({appId}, \"{token}\")");
                queryResult.AppToken = token;
                matchedItems++;
            }

            if (matchedItems == 0)
                throw new InvalidOperationException($"本地缓存未找到 AppID {appId} 及其 depots 的任何密钥或 token，已停止生成入库文件");

            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder)) return null;

            if (!Directory.Exists(luaFolder))
                Directory.CreateDirectory(luaFolder);

            var luaPath = Path.Combine(luaFolder, $"{appId}.lua");
            await File.WriteAllTextAsync(luaPath, sb.ToString(), ct);

            return luaPath;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { LogService.Error("入库", $"生成入库文件失败 (AppID {appId}): {ex.Message}"); return null; }
    }

    public async Task<string?> GenerateLuaWithDlcAsync(int appId, string? keyFolderPath = null, CancellationToken ct = default)
    {
        try
        {
            if (!await EnsureKeyFilesAsync(ct))
                return null;

            var depotKeysPath = GetDepotKeysPath();
            var tokenKeysPath = GetTokenKeysPath();

            Dictionary<string, string> depotKeys;
            Dictionary<string, string> appTokens;
            try
            {
                depotKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(depotKeysPath))
                    ?? new Dictionary<string, string>();
                appTokens = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(tokenKeysPath))
                    ?? new Dictionary<string, string>();
            }
            catch (Exception ex) { LogService.Warn("入库", $"读取密钥文件失败 (DLC): {ex.Message}"); return null; }

            var queryResult = await QueryAppAsync(appId, ct);
            if (queryResult == null) return null;

            var sb = new StringBuilder();
            sb.AppendLine("-- lua by Fluent-Steam-Lua (https://github.com/huanyuejue/Fluent-Steam-Lua)");
            sb.AppendLine();
            var matchedItems = 0;

            if (depotKeys.TryGetValue(appId.ToString(), out var mainKeyDlc))
            {
                sb.AppendLine($"addappid({appId}, 1, \"{mainKeyDlc}\")");
                matchedItems++;
            }
            else
            {
                // 主 AppID 没有 key 时，DLC 模式也继续向下生成
                sb.AppendLine($"addappid({appId})");
            }

            foreach (var depot in queryResult.GameDepots)
            {
                if (depotKeys.TryGetValue(depot.DepotId.ToString(), out var key))
                {
                    sb.AppendLine($"addappid({depot.DepotId}, 1, \"{key}\")");
                    matchedItems++;
                }
            }

            if (appTokens.TryGetValue(appId.ToString(), out var token))
            {
                sb.AppendLine($"addtoken({appId}, \"{token}\")");
                queryResult.AppToken = token;
                matchedItems++;
            }

            var mainDepotIds = new HashSet<int>(queryResult.GameDepots.Select(d => d.DepotId));
            var matchedDlc = 0;
            foreach (var dlcAppId in queryResult.DlcAppIds)
            {
                // Skip DLC that is already a main game depot (already handled above)
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

                // Query DLC's own sub-depots for additional keys
                var dlcResult = await QueryAppAsync(dlcAppId, ct);
                if (dlcResult != null)
                {
                    foreach (var depot in dlcResult.GameDepots)
                    {
                        if (depotKeys.TryGetValue(depot.DepotId.ToString(), out var key))
                        {
                            sb.AppendLine($"addappid({depot.DepotId}, 1, \"{key}\")");
                            matchedItems++;
                        }
                    }
                }
                matchedDlc++;
            }

            if (matchedItems == 0)
                throw new InvalidOperationException($"本地缓存未找到 AppID {appId}、其 depots 或 DLC 的任何密钥或 token，已停止生成入库文件");

            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder)) return null;

            if (!Directory.Exists(luaFolder))
                Directory.CreateDirectory(luaFolder);

            var luaPath = Path.Combine(luaFolder, $"{appId}.lua");
            await File.WriteAllTextAsync(luaPath, sb.ToString(), ct);

            return luaPath;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { LogService.Error("入库", $"生成入库文件失败 (AppID {appId}, DLC): {ex.Message}"); return null; }
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

                Dictionary<string, string> depotKeys;
                try
                {
                    depotKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(GetDepotKeysPath()))
                        ?? new Dictionary<string, string>();
                }
                catch (Exception ex)
                {
                    LogService.Warn("获取DLC", $"读取密钥文件失败: {ex.Message}");
                    result.Message = "读取密钥仓库文件失败";
                    return result;
                }

                // 查找 DLC 自身主 AppID 的密钥
                if (depotKeys.TryGetValue(dlcAppId.ToString(), out var dlcMainKey))
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
