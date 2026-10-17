using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SteamLuaManager.Services;

// 云端存档远端访问：名单 / 时间 / 统计 / 下载 / 删除。
// 上游前端走内嵌 CLI，本仓库未集成该二进制，改为 C# 原生 REST 实现；
// 鉴权复用已落盘材料（OAuth refresh_token、R2/S3 密钥），DLL 侧同步行为不受影响。
public sealed record CloudAppEntry(string AccountId, int AppId, DateTime? LastSaveTime, string? RemoteId, string? WebUrl);

public sealed record CloudAppStats(int FileCount, long TotalBytes, string DisplayPath);

public sealed record CloudDeleteResult(int Deleted, int Failed, List<string> FailedNames, string? Error);

public sealed class CloudProviderStore
{
    private readonly Func<R2Credentials?> _loadR2;
    private readonly Func<S3Credentials?> _loadS3;
    private readonly Func<string, string> _tokenPath;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, HttpClient> _s3Clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _s3ClientOrder = new();
    private const int MaxS3Clients = 8;
    private readonly object _s3ClientLock = new();
    private const string RootFolderName = "CloudRedirect";
    private const string StatsFileName = "stats.json";

    public CloudProviderStore(Func<R2Credentials?> loadR2, Func<S3Credentials?> loadS3, Func<string, string> tokenPath)
    {
        _loadR2 = loadR2;
        _loadS3 = loadS3;
        _tokenPath = tokenPath;
    }

    // key 前缀归一化：为空即桶根目录（DLL 默认无前缀）；非空去首尾斜杠后补 trailing /
    public static string NormalizePrefix(string? keyPrefix) =>
        string.IsNullOrWhiteSpace(keyPrefix) ? "" : keyPrefix.Trim().Trim('/') + "/";

    // 远端路径展示：删除确认对话框与日志使用，不对应本地文件系统；
    // root 传有效前缀（S3/R2 可能为空即桶根），OAuth 源沿用 CloudRedirect
    public static string DescribeRemote(string provider, string accountId, int appId,
        string? bucket = null, string? root = null) => provider switch
    {
        "gdrive" => $"Google Drive:/{RootFolderName}/{accountId}/{appId}",
        "onedrive" => $"OneDrive:/{RootFolderName}/{accountId}/{appId}",
        "r2" => $"r2://{bucket ?? "(bucket)"}/{root ?? RootFolderName + "/"}{accountId}/{appId}",
        "s3" => $"s3://{bucket ?? "(bucket)"}/{root ?? RootFolderName + "/"}{accountId}/{appId}",
        _ => $"{provider}:/{accountId}/{appId}",
    };

    public Task<List<CloudAppEntry>> ListAppsAsync(string provider, IProgress<string>? progress, CancellationToken ct) =>
        provider switch
        {
            "gdrive" => ListDriveAppsAsync(progress, ct),
            "onedrive" => ListOneDriveAppsAsync(progress, ct),
            "r2" or "s3" => ListS3AppsAsync(provider, progress, ct),
            _ => throw new InvalidOperationException(L.Get("Cloud_UnknownProvider", provider)),
        };

    // 并发限流（4 并发防云端风控）+ 单项异常隔离 + 计数进度；失败项记入内存名单，
    // 末尾串行单个重试一次，仍失败则跳过记日志；全程不依赖 app.log（日志开关关闭时流程不受影响）
    private const int ListDegree = 4;

    private readonly List<string> _listFailures = new();

    public IReadOnlyList<string> GetLastListFailures()
    {
        lock (_listFailures) return _listFailures.ToList();
    }

    private async Task<List<CloudAppEntry>> RunParallelListAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string> describe,
        Func<T, CancellationToken, Task<CloudAppEntry>> fetch,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var results = new CloudAppEntry?[items.Count];
        var failed = new List<(int index, T item, string reason)>();
        var completed = 0;
        using var throttle = new SemaphoreSlim(ListDegree);
        await Task.WhenAll(items.Select(async (item, index) =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                try
                {
                    results[index] = await fetch(item, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (failed) failed.Add((index, item, ex.Message));
                }
            }
            finally
            {
                throttle.Release();
                progress?.Report(L.Get("Cloud_ReadingList", Interlocked.Increment(ref completed)));
            }
        }));
        ct.ThrowIfCancellationRequested();
        // 失败项串行单个重试一次；瞬时抖动靠这次重试消化，持续失败则跳过
        if (failed.Count > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var retryTotal = failed.Count;
            var retryDone = 0;
            foreach (var (index, item, reason) in failed.ToList())
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(L.Get("Cloud_RetryingGames", ++retryDone, retryTotal));
                try
                {
                    results[index] = await fetch(item, ct);
                    lock (failed) failed.RemoveAll(f => f.index == index);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogGameSkipped", describe(item), ex.Message, reason));
                }
            }
        }
        lock (_listFailures)
        {
            _listFailures.Clear();
            foreach (var (_, item, reason) in failed)
                _listFailures.Add($"{describe(item)}（{reason}）");
        }
        return results.Where(e => e != null).Select(e => e!).OrderBy(e => e.AppId).ToList();
    }

    // 下载并发：与名单相反语义，任一文件失败整单中止（备份不完整绝不删远端）；
    // 首个真失败原样抛出，其余取消；成功返回文件数，与旧串行返回值一致
    private const int DownloadDegree = 4;

    private static async Task<int> RunParallelDownloadAsync<T>(
        IReadOnlyList<T> items,
        Func<T, CancellationToken, Task> downloadOne,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var done = 0;
        var errorLock = new object();
        Exception? firstError = null;
        using var joint = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var throttle = new SemaphoreSlim(DownloadDegree);
        var tasks = items.Select(async item =>
        {
            await throttle.WaitAsync(joint.Token);
            try
            {
                try
                {
                    await downloadOne(item, joint.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (errorLock) firstError ??= ex;
                    joint.Cancel();
                    throw;
                }
            }
            finally
            {
                throttle.Release();
                progress?.Report(L.Get("Cloud_DownloadingBackup", Interlocked.Increment(ref done), items.Count));
            }
        });
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            // 联合取消且有真错误：落下统一抛首错
        }
        catch (Exception)
        {
            // 真失败：首错已记录，落下统一抛首错
        }
        if (firstError != null)
            ExceptionDispatchInfo.Capture(firstError).Throw();
        ct.ThrowIfCancellationRequested();
        return items.Count;
    }

    public Task<CloudAppStats> GetAppStatsAsync(string provider, string accountId, int appId, CancellationToken ct) =>
        provider switch
        {
            "gdrive" => GetDriveAppStatsAsync(accountId, appId, ct),
            "onedrive" => GetOneDriveAppStatsAsync(accountId, appId, ct),
            "r2" or "s3" => GetS3AppStatsAsync(provider, accountId, appId, ct),
            _ => throw new InvalidOperationException(L.Get("Cloud_UnknownProvider", provider)),
        };

    public Task<int> DownloadAppAsync(string provider, string accountId, int appId, string destDir,
        IProgress<string>? progress, CancellationToken ct) => provider switch
        {
            "gdrive" => DownloadDriveAppAsync(accountId, appId, destDir, progress, ct),
            "onedrive" => DownloadOneDriveAppAsync(accountId, appId, destDir, progress, ct),
            "r2" or "s3" => DownloadS3AppAsync(provider, accountId, appId, destDir, progress, ct),
            _ => throw new InvalidOperationException(L.Get("Cloud_UnknownProvider", provider)),
        };

    public Task<CloudDeleteResult> DeleteAppAsync(string provider, string accountId, int appId,
        IProgress<string>? progress, CancellationToken ct) => provider switch
        {
            "gdrive" => DeleteDriveAppAsync(accountId, appId, progress, ct),
            "onedrive" => DeleteOneDriveAppAsync(accountId, appId, progress, ct),
            "r2" or "s3" => DeleteS3AppAsync(provider, accountId, appId, progress, ct),
            _ => throw new InvalidOperationException(L.Get("Cloud_UnknownProvider", provider)),
        };

    // ---- OAuth token：未过期复用 access_token，否则 refresh_token 刷新并回盘 ----

    private async Task<string> GetGoogleAccessTokenAsync(CancellationToken ct)
    {
        var path = _tokenPath("gdrive");
        var (access, refresh, expiresAt) = ReadTokenFile(path);
        if (!string.IsNullOrEmpty(access) && expiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds() > 60)
            return access;
        if (string.IsNullOrEmpty(refresh))
            throw new InvalidOperationException(L.Get("Cloud_GoogleTokenMissing"));
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = CloudOAuthService.GDriveClientId,
            ["client_secret"] = CloudOAuthService.GDriveClientSecret,
            ["refresh_token"] = refresh,
        };
        using var resp = await _http.PostAsync(CloudOAuthService.GDriveTokenUrl, new FormUrlEncodedContent(fields), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(L.Get("Cloud_GoogleTokenRefreshFailed", (int)resp.StatusCode));
        return PersistRefreshedToken(path, body, refresh);
    }

    private async Task<string> GetOneDriveAccessTokenAsync(CancellationToken ct)
    {
        var path = _tokenPath("onedrive");
        var (access, refresh, expiresAt) = ReadTokenFile(path);
        if (!string.IsNullOrEmpty(access) && expiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds() > 60)
            return access;
        if (string.IsNullOrEmpty(refresh))
            throw new InvalidOperationException(L.Get("Cloud_OneDriveTokenMissing"));
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = CloudOAuthService.OneDriveClientId,
            ["client_secret"] = CloudOAuthService.OneDriveClientSecret,
            ["refresh_token"] = refresh,
            ["scope"] = CloudOAuthService.OneDriveScope,
        };
        using var resp = await _http.PostAsync(CloudOAuthService.OneDriveTokenUrl, new FormUrlEncodedContent(fields), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(L.Get("Cloud_OneDriveTokenRefreshFailed", (int)resp.StatusCode));
        return PersistRefreshedToken(path, body, refresh);
    }

    private static (string Access, string Refresh, long ExpiresAt) ReadTokenFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return ("", "", 0);
            var json = CloudCredentialStore.ReadJson(path);
            if (string.IsNullOrEmpty(json)) return ("", "", 0);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string Get(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var expires = r.TryGetProperty("expires_at", out var e) && e.TryGetInt64(out var s) ? s : 0;
            return (Get("access_token"), Get("refresh_token"), expires);
        }
        catch
        {
            return ("", "", 0);
        }
    }

    // 刷新回盘：response 未必带新 refresh_token，沿用旧值；DLL 使用同一文件，字段保持三元组不变
    private static string PersistRefreshedToken(string path, string body, string oldRefresh)
    {
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        var access = r.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
        var refresh = r.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrEmpty(rt.GetString())
            ? rt.GetString()! : oldRefresh;
        var expiresIn = r.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out var s) ? s : 3600;
        if (string.IsNullOrEmpty(access))
            throw new InvalidOperationException(L.Get("Cloud_TokenNoAccess"));
        var payload = JsonSerializer.Serialize(new
        {
            access_token = access,
            refresh_token = refresh,
            expires_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn,
        }, new JsonSerializerOptions { WriteIndented = true });
        if (!CloudCredentialStore.WriteJson(path, payload))
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_TokenPersistFailed"));
        return access;
    }

    // ---- stats.json 时间解析：schema 未公开，按常见字段与格式宽容解析 ----

    private static DateTime? TryParseCloudTime(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var text = content.Trim();
        if (text.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "last_sync", "lastSync", "last_synced", "timestamp", "updated_at", "updatedAt", "modified", "mtime", "time" })
                    {
                        if (!doc.RootElement.TryGetProperty(key, out var v)) continue;
                        var t = v.ValueKind switch
                        {
                            JsonValueKind.Number when v.TryGetInt64(out var n) => FromEpoch(n),
                            JsonValueKind.String => TryParseCloudTime(v.GetString()),
                            _ => null,
                        };
                        if (t.HasValue) return t;
                    }
                }
            }
            catch { }
            return null;
        }
        if (long.TryParse(text, out var epoch)) return FromEpoch(epoch);
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var epochD))
            return FromEpoch((long)epochD);
        if (DateTimeOffset.TryParse(text, out var dto)) return dto.LocalDateTime;
        if (DateTime.TryParse(text, out var dt)) return dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
        return null;
    }

    // 纪元秒 / 毫秒按量级区分；超出合理范围视为无效
    private static DateTime? FromEpoch(long n)
    {
        try
        {
            if (n > 1_000_000_000_000L) return DateTimeOffset.FromUnixTimeMilliseconds(n).LocalDateTime;
            if (n > 1_000_000_000L) return DateTimeOffset.FromUnixTimeSeconds(n).LocalDateTime;
            return null;
        }
        catch
        {
            return null;
        }
    }

    // ---- Google Drive ----

    private const string DriveApi = "https://www.googleapis.com/drive/v3/files";

    private async Task<List<CloudAppEntry>> ListDriveAppsAsync(IProgress<string>? progress, CancellationToken ct)
    {
        lock (_listFailures) _listFailures.Clear();
        progress?.Report(L.Get("Cloud_ConnectingSource", "Google Drive"));
        var token = await GetGoogleAccessTokenAsync(ct);
        progress?.Report(L.Get("Cloud_ReadingCloudDir"));
        var rootId = await FindDriveChildFolderAsync("root", RootFolderName, token, ct);
        if (rootId == null) return new List<CloudAppEntry>();
        var accounts = await ListDriveFoldersAsync(rootId, token, ct);
        var apps = new List<(string Account, int AppId, string FolderId)>();
        foreach (var (accountName, _) in accounts)
        {
            // 账号目录只认数字；找不到下级目录直接跳过，不回退到上级，避免串号
            if (!uint.TryParse(accountName, out _)) continue;
            var accountFolderId = await GetDriveIdAsync(rootId, accountName, token, ct);
            if (accountFolderId == null) continue;
            foreach (var (appName, appFolderId) in await ListDriveFoldersAsync(accountFolderId, token, ct))
            {
                if (!int.TryParse(appName, out var appId) || appId == 0) continue;
                apps.Add((accountName, appId, appFolderId));
            }
        }
        return await RunParallelListAsync(apps,
            a => $"AppID {a.AppId}",
            async (a, ct) =>
            {
                var time = await GetDriveAppTimeAsync(a.FolderId, token, ct);
                return new CloudAppEntry(a.Account, a.AppId, time, a.FolderId, null);
            }, progress, ct);
    }

    private async Task<string?> GetDriveIdAsync(string parentId, string name, string token, CancellationToken ct)
    {
        foreach (var (childName, childId) in await ListDriveFoldersAsync(parentId, token, ct))
        {
            if (childName == name) return childId;
        }
        return null;
    }

    private async Task<string?> FindDriveChildFolderAsync(string parentId, string name, string token, CancellationToken ct)
    {
        var q = $"'{parentId}' in parents and name = '{name}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{DriveApi}?q={Uri.EscapeDataString(q)}&fields=files(id)&pageSize=10");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(L.Get("Cloud_ReadSourceFailed", "Google Drive", (int)resp.StatusCode));
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
            return files[0].TryGetProperty("id", out var id) ? id.GetString() : null;
        return null;
    }

    private async Task<List<(string Name, string Id)>> ListDriveFoldersAsync(string parentId, string token, CancellationToken ct)
    {
        var result = new List<(string Name, string Id)>();
        string? pageToken = null;
        do
        {
            var q = $"'{parentId}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
            var url = $"{DriveApi}?q={Uri.EscapeDataString(q)}&fields=files(id,name),nextPageToken&pageSize=1000"
                + (pageToken == null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(L.Get("Cloud_ReadSourceDirFailed", "Google Drive", (int)resp.StatusCode));
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("files", out var files))
            {
                foreach (var f in files.EnumerateArray())
                {
                    var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var id = f.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    if (name.Length > 0 && id.Length > 0) result.Add((name, id));
                }
            }
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var nt) ? nt.GetString() : null;
        } while (!string.IsNullOrEmpty(pageToken));
        return result;
    }

    // app 时间：stats.json 内容 > stats.json 文件时间 > cn.cloudredirect 文件时间；
    // 上游 DLL 并非必写 stats.json（如截图只有 cn/state），目录 mtime 不可靠，不采用
    private async Task<DateTime?> GetDriveAppTimeAsync(string appFolderId, string token, CancellationToken ct)
    {
        var stats = await FindDriveFileAsync(appFolderId, StatsFileName, token, ct);
        if (stats != null)
        {
            var parsed = TryParseCloudTime(await DownloadDriveFileAsync(stats.Value.Id, token, ct));
            if (parsed.HasValue) return parsed;
            if (stats.Value.Modified.HasValue) return stats.Value.Modified;
        }
        var cn = await FindDriveFileAsync(appFolderId, "cn.cloudredirect", token, ct);
        return cn?.Modified;
    }

    private async Task<(string Id, DateTime? Modified)?> FindDriveFileAsync(
        string parentId, string name, string token, CancellationToken ct)
    {
        var q = $"'{parentId}' in parents and name = '{name}' and trashed = false";
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{DriveApi}?q={Uri.EscapeDataString(q)}&fields=files(id,modifiedTime)&pageSize=10");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.GetArrayLength() == 0)
            return null;
        var first = files[0];
        var id = first.TryGetProperty("id", out var i) ? i.GetString() : null;
        if (string.IsNullOrEmpty(id)) return null;
        DateTime? modified = null;
        if (first.TryGetProperty("modifiedTime", out var mt) && mt.GetString() is { } mts &&
            DateTimeOffset.TryParse(mts, out var dto))
            modified = dto.LocalDateTime;
        return (id, modified);
    }

    private async Task<string?> DownloadDriveFileAsync(string fileId, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{DriveApi}/{fileId}?alt=media");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private sealed record DriveNode(string Id, string RelPath, bool IsFolder, long Size);

    // 递归收集 app 目录下全部节点；relPath 相对 app 目录，用于本地备份还原路径
    private async Task<List<DriveNode>> CollectDriveNodesAsync(string folderId, string rel, string token, CancellationToken ct)
    {
        var nodes = new List<DriveNode>();
        string? pageToken = null;
        var children = new List<(string Id, string Name, bool IsFolder, long Size)>();
        do
        {
            var q = $"'{folderId}' in parents and trashed = false";
            var url = $"{DriveApi}?q={Uri.EscapeDataString(q)}&fields=files(id,name,mimeType,size),nextPageToken&pageSize=1000"
                + (pageToken == null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(L.Get("Cloud_ReadSourceFileFailed", "Google Drive", (int)resp.StatusCode));
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("files", out var files))
            {
                foreach (var f in files.EnumerateArray())
                {
                    var id = f.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (id.Length == 0 || name.Length == 0) continue;
                    var mime = f.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
                    var isFolder = mime == "application/vnd.google-apps.folder";
                    long size = 0;
                    if (!isFolder && f.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.String)
                        long.TryParse(s.GetString(), out size);
                    children.Add((id, name, isFolder, size));
                }
            }
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var nt) ? nt.GetString() : null;
        } while (!string.IsNullOrEmpty(pageToken));
        foreach (var (id, name, isFolder, size) in children)
        {
            var relPath = string.IsNullOrEmpty(rel) ? name : $"{rel}/{name}";
            nodes.Add(new DriveNode(id, relPath, isFolder, size));
            if (isFolder)
                nodes.AddRange(await CollectDriveNodesAsync(id, relPath, token, ct));
        }
        return nodes;
    }

    private async Task<CloudAppStats> GetDriveAppStatsAsync(string accountId, int appId, CancellationToken ct)
    {
        var display = DescribeRemote("gdrive", accountId, appId);
        var token = await GetGoogleAccessTokenAsync(ct);
        var rootId = await FindDriveChildFolderAsync("root", RootFolderName, token, ct)
            ?? throw new InvalidOperationException(L.Get("Cloud_NoRootNoSync"));
        var accountId2 = await GetDriveIdAsync(rootId, accountId, token, ct)
            ?? throw new InvalidOperationException(L.Get("Cloud_NoAccountDir", accountId));
        var appId2 = await GetDriveIdAsync(accountId2, appId.ToString(), token, ct)
            ?? throw new InvalidOperationException(L.Get("Cloud_NoGameDir", appId));
        var nodes = await CollectDriveNodesAsync(appId2, "", token, ct);
        return new CloudAppStats(nodes.Count(n => !n.IsFolder), nodes.Where(n => !n.IsFolder).Sum(n => n.Size), display);
    }

    private async Task<int> DownloadDriveAppAsync(string accountId, int appId, string destDir,
        IProgress<string>? progress, CancellationToken ct)
    {
        var token = await GetGoogleAccessTokenAsync(ct);
        var rootId = await FindDriveChildFolderAsync("root", RootFolderName, token, ct)
            ?? throw new InvalidOperationException(L.Get("Cloud_NoRoot"));
        var accountId2 = await GetDriveIdAsync(rootId, accountId, token, ct)
            ?? throw new InvalidOperationException(L.Get("Cloud_NoAccountDir", accountId));
        var appId2 = await GetDriveIdAsync(accountId2, appId.ToString(), token, ct)
            ?? throw new InvalidOperationException(L.Get("Cloud_NoGameDir", appId));
        var nodes = (await CollectDriveNodesAsync(appId2, "", token, ct)).Where(n => !n.IsFolder).ToList();
        return await RunParallelDownloadAsync(nodes,
            async (node, ct) =>
            {
                var dest = Path.Combine(destDir, node.RelPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{DriveApi}/{node.Id}?alt=media");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst, ct);
            }, progress, ct);
    }

    // Drive 删目录自带递归：列举仅用于计数，一次删 app 目录本身即可（语义同逐个删，均进回收站）
    private async Task<CloudDeleteResult> DeleteDriveAppAsync(string accountId, int appId,
        IProgress<string>? progress, CancellationToken ct)
    {
        var token = await GetGoogleAccessTokenAsync(ct);
        var rootId = await FindDriveChildFolderAsync("root", RootFolderName, token, ct);
        if (rootId == null) return new CloudDeleteResult(0, 0, new List<string>(), L.Get("Cloud_NoRoot"));
        var accountId2 = await GetDriveIdAsync(rootId, accountId, token, ct);
        if (accountId2 == null) return new CloudDeleteResult(0, 0, new List<string>(), L.Get("Cloud_NoAccountDir", accountId));
        var appId2 = await GetDriveIdAsync(accountId2, appId.ToString(), token, ct);
        if (appId2 == null) return new CloudDeleteResult(0, 0, new List<string>(), L.Get("Cloud_NoGameDir", appId));
        var fileCount = (await CollectDriveNodesAsync(appId2, "", token, ct)).Count(n => !n.IsFolder);
        progress?.Report(L.Get("Cloud_DeletingCloud"));
        if (!await DeleteDriveFileAsync(appId2, token, ct))
            return new CloudDeleteResult(0, fileCount, new List<string> { appId.ToString() }, L.Get("Cloud_DeleteDirFailed"));
        return new CloudDeleteResult(fileCount, 0, new List<string>(), null);
    }

    private async Task<bool> DeleteDriveFileAsync(string fileId, string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{DriveApi}/{fileId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ---- OneDrive (Graph) ----

    private const string GraphBase = "https://graph.microsoft.com/v1.0/me/drive";

    private async Task<List<CloudAppEntry>> ListOneDriveAppsAsync(IProgress<string>? progress, CancellationToken ct)
    {
        lock (_listFailures) _listFailures.Clear();
        progress?.Report(L.Get("Cloud_ConnectingSource", "OneDrive"));
        var token = await GetOneDriveAccessTokenAsync(ct);
        progress?.Report(L.Get("Cloud_ReadingCloudDir"));
        var rootChildren = await GetGraphChildrenByPathAsync(RootFolderName, token, ct);
        var apps = new List<(string Account, int AppId, string FolderId, DateTime? Modified, string? WebUrl)>();
        foreach (var acct in rootChildren.Where(c => c.IsFolder))
        {
            if (!uint.TryParse(acct.Name, out _)) continue;
            foreach (var app in (await GetGraphChildrenByIdAsync(acct.Id, token, ct)).Where(c => c.IsFolder))
            {
                if (!int.TryParse(app.Name, out var appId) || appId == 0) continue;
                apps.Add((acct.Name, appId, app.Id, app.LastModified, app.WebUrl));
            }
        }
        return await RunParallelListAsync(apps,
            a => $"AppID {a.AppId}",
            async (a, ct) =>
            {
                var time = await GetOneDriveAppTimeAsync(a.FolderId, token, ct) ?? a.Modified;
                return new CloudAppEntry(a.Account, a.AppId, time, a.FolderId, a.WebUrl);
            }, progress, ct);
    }

    private sealed record GraphNode(string Id, string Name, bool IsFolder, long Size, DateTime? LastModified, string? WebUrl);

    private async Task<List<GraphNode>> GetGraphChildrenByPathAsync(string path, string token, CancellationToken ct)
    {
        var encoded = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        var url = $"{GraphBase}/root:/{encoded}:/children?$select=id,name,folder,file,size,lastModifiedDateTime,webUrl&$top=200";
        // 根目录不存在（尚未同步过）视为空列表，由调用方处理
        return await GetGraphChildrenAsync(url, token, ct, notFoundAsNull: true) ?? new List<GraphNode>();
    }

    private async Task<List<GraphNode>> GetGraphChildrenByIdAsync(string id, string token, CancellationToken ct) =>
        await GetGraphChildrenAsync($"{GraphBase}/items/{id}/children?$select=id,name,folder,file,size,lastModifiedDateTime,webUrl&$top=200",
            token, ct, notFoundAsNull: false) ?? new List<GraphNode>();

    // notFoundAsNull=true 时 404 转空（目录尚未同步过，不是错误）
    private async Task<List<GraphNode>?> GetGraphChildrenAsync(string url, string token, CancellationToken ct, bool notFoundAsNull)
    {
        var result = new List<GraphNode>();
        string? next = url;
        while (next != null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound && notFoundAsNull) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(L.Get("Cloud_ReadSourceFailed", "OneDrive", (int)resp.StatusCode) + "：" + GraphError(body));
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("value", out var value))
            {
                foreach (var item in value.EnumerateArray())
                    result.Add(ParseGraphNode(item));
            }
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }
        return result;
    }

    private static GraphNode ParseGraphNode(JsonElement item)
    {
        var id = item.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
        var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var isFolder = item.TryGetProperty("folder", out _);
        long size = 0;
        if (item.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var sz))
            size = sz;
        DateTime? modified = null;
        if (item.TryGetProperty("lastModifiedDateTime", out var m) && m.GetString() is { } ms &&
            DateTimeOffset.TryParse(ms, out var dto))
            modified = dto.LocalDateTime;
        var webUrl = item.TryGetProperty("webUrl", out var w) ? w.GetString() : null;
        return new GraphNode(id, name, isFolder, size, modified, webUrl);
    }

    private static string GraphError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg))
                return msg.GetString() ?? "";
        }
        catch { }
        return body.Length > 200 ? body[..200] : body;
    }

    private async Task<DateTime?> GetOneDriveAppTimeAsync(string appFolderId, string token, CancellationToken ct)
    {
        var children = await GetGraphChildrenAsync(
            $"{GraphBase}/items/{appFolderId}/children?$select=id,name,lastModifiedDateTime&$top=200",
            token, ct, notFoundAsNull: true);
        var stats = children?.FirstOrDefault(c => c.Name == StatsFileName);
        if (stats != null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/items/{stats.Id}/content");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return stats.LastModified;
            var content = await resp.Content.ReadAsStringAsync(ct);
            return TryParseCloudTime(content) ?? stats.LastModified;
        }
        // DLL 未写 stats.json 时退到 cn.cloudredirect 文件时间
        return children?.FirstOrDefault(c => c.Name == "cn.cloudredirect")?.LastModified;
    }

    private sealed record GraphFile(string Id, string RelPath, long Size);

    private async Task<List<GraphFile>> CollectGraphFilesAsync(string folderId, string rel, string token, CancellationToken ct)
    {
        var files = new List<GraphFile>();
        var children = await GetGraphChildrenAsync(
            $"{GraphBase}/items/{folderId}/children?$select=id,name,folder,size&$top=200",
            token, ct, notFoundAsNull: false) ?? new List<GraphNode>();
        foreach (var child in children)
        {
            var relPath = string.IsNullOrEmpty(rel) ? child.Name : $"{rel}/{child.Name}";
            if (child.IsFolder)
                files.AddRange(await CollectGraphFilesAsync(child.Id, relPath, token, ct));
            else
                files.Add(new GraphFile(child.Id, relPath, child.Size));
        }
        return files;
    }

    private async Task<string> ResolveOneDriveAppIdAsync(string accountId, int appId, string token, CancellationToken ct)
    {
        var rootChildren = await GetGraphChildrenByPathAsync(RootFolderName, token, ct)
            ?? throw new InvalidOperationException(L.Get("Cloud_NoRootNoSync"));
        var acct = rootChildren.FirstOrDefault(c => c.IsFolder && c.Name == accountId)
            ?? throw new InvalidOperationException(L.Get("Cloud_NoAccountDir", accountId));
        var apps = await GetGraphChildrenByIdAsync(acct.Id, token, ct);
        var app = apps.FirstOrDefault(c => c.IsFolder && c.Name == appId.ToString())
            ?? throw new InvalidOperationException(L.Get("Cloud_NoGameDir", appId));
        return app.Id;
    }

    private async Task<CloudAppStats> GetOneDriveAppStatsAsync(string accountId, int appId, CancellationToken ct)
    {
        var token = await GetOneDriveAccessTokenAsync(ct);
        var appFolderId = await ResolveOneDriveAppIdAsync(accountId, appId, token, ct);
        var files = await CollectGraphFilesAsync(appFolderId, "", token, ct);
        return new CloudAppStats(files.Count, files.Sum(f => f.Size), DescribeRemote("onedrive", accountId, appId));
    }

    private async Task<int> DownloadOneDriveAppAsync(string accountId, int appId, string destDir,
        IProgress<string>? progress, CancellationToken ct)
    {
        var token = await GetOneDriveAccessTokenAsync(ct);
        var appFolderId = await ResolveOneDriveAppIdAsync(accountId, appId, token, ct);
        var files = await CollectGraphFilesAsync(appFolderId, "", token, ct);
        return await RunParallelDownloadAsync(files,
            async (file, ct) =>
            {
                var dest = Path.Combine(destDir, file.RelPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/items/{file.Id}/content");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst, ct);
            }, progress, ct);
    }

    // Graph 删除目录默认递归，单次调用即可
    private async Task<CloudDeleteResult> DeleteOneDriveAppAsync(string accountId, int appId,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(L.Get("Cloud_DeletingCloud"));
        try
        {
            var token = await GetOneDriveAccessTokenAsync(ct);
            var appFolderId = await ResolveOneDriveAppIdAsync(accountId, appId, token, ct);
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{GraphBase}/items/{appFolderId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new CloudDeleteResult(0, 1, new List<string> { appId.ToString() },
                    L.Get("Cloud_OneDriveDeleteFailed", (int)resp.StatusCode, GraphError(body)));
            return new CloudDeleteResult(1, 0, new List<string>(), null);
        }
        catch (Exception ex)
        {
            return new CloudDeleteResult(0, 1, new List<string> { appId.ToString() }, ex.Message);
        }
    }

    // ---- S3 兼容存储（含 R2）：SigV4 自签名，无第三方 SDK ----

    private sealed record S3Endpoint(string Scheme, string Host, string Bucket, string Region,
        string AccessKey, string SecretKey, string RootPrefix, bool SignPayload = false);

    private S3Endpoint ResolveS3Endpoint(string provider)
    {
        if (provider == "r2")
        {
            var cred = _loadR2() ?? throw new InvalidOperationException(L.Get("Cloud_R2CredMissing"));
            var endpoint = string.IsNullOrWhiteSpace(cred.Endpoint)
                ? $"https://{cred.AccountId}.r2.cloudflarestorage.com"
                : cred.Endpoint.Trim().TrimEnd('/');
            return BuildS3Endpoint(endpoint, cred.Bucket, "auto",
                cred.AccessKeyId, cred.SecretAccessKey, cred.KeyPrefix, allowHttp: false);
        }
        else
        {
            var cred = _loadS3() ?? throw new InvalidOperationException(L.Get("Cloud_S3CredMissing"));
            if (string.IsNullOrWhiteSpace(cred.Endpoint))
                throw new InvalidOperationException(L.Get("Cloud_S3EndpointMissing"));
            return BuildS3Endpoint(cred.Endpoint.Trim().TrimEnd('/'), cred.Bucket.Trim(), cred.Region.Trim(),
                cred.AccessKeyId, cred.SecretAccessKey, cred.KeyPrefix,
                cred.AllowInsecureHttp, cred.AllowInsecureTls, cred.CaCertPath, cred.SignPayload);
        }
    }

    private static S3Endpoint BuildS3Endpoint(string endpoint, string bucket, string region,
        string accessKey, string secretKey, string keyPrefix,
        bool allowHttp = false, bool allowInsecureTls = false, string caCertPath = "",
        bool signPayload = false)
    {
        var scheme = "https";
        var host = endpoint;
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            scheme = "http";
            host = endpoint[7..];
        }
        else if (endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            host = endpoint[8..];
        }
        else if (allowHttp)
        {
            scheme = "http";
        }
        if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
            throw new InvalidOperationException(L.Get("Cloud_ObjectKeyMissing"));
        // DLL 默认无 key 前缀（TopObjectKey = prefix + relPath）；之前默认 CloudRedirect/ 是错的，
        // 会导致桶里有数据也列举为空
        var root = NormalizePrefix(keyPrefix);
        return new S3Endpoint(scheme, host, bucket, string.IsNullOrEmpty(region) ? "auto" : region,
            accessKey, secretKey, root, signPayload);
    }

    // S3 客户端按 endpoint 缓存；preview（线程池）与刷新（UI 线程）可并发，加锁防字典竞态
    // S3 客户端 TLS 选项：R2 走默认，S3 读用户配置；收拢一处，避免四处传散
    private HttpClient ResolveS3Http(string provider, S3Endpoint ep)
    {
        var insecure = false;
        var caPath = "";
        if (provider == "s3")
        {
            var cred = _loadS3();
            insecure = cred?.AllowInsecureTls == true;
            caPath = cred?.CaCertPath ?? "";
        }
        return GetS3Client(ep, insecure, caPath);
    }

    // 连接测试：只列举账号/应用两级目录（不下 stats.json），验证鉴权、endpoint、region、前缀；
    // 前缀下无数据不算错（返回 0，由调用方提示核对前缀或等待同步）
    public sealed record CloudProbeResult(int AccountCount, int AppCount, string? SamplePath, string? Error);

    public async Task<CloudProbeResult> ProbeAsync(string provider, CancellationToken ct)
    {
        try
        {
            return provider switch
            {
                "gdrive" => await ProbeDriveAsync(ct),
                "onedrive" => await ProbeOneDriveAsync(ct),
                "r2" or "s3" => await ProbeS3Async(provider, ct),
                _ => throw new InvalidOperationException(L.Get("Cloud_UnknownProvider", provider)),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CloudProbeResult(0, 0, null, ex.Message);
        }
    }

    private static bool IsAppDir(string name) =>
        int.TryParse(name, out var id) && id != 0;

    private async Task<CloudProbeResult> ProbeDriveAsync(CancellationToken ct)
    {
        var token = await GetGoogleAccessTokenAsync(ct);
        var rootId = await FindDriveChildFolderAsync("root", RootFolderName, token, ct);
        if (rootId == null) return new CloudProbeResult(0, 0, null, null);
        var accounts = (await ListDriveFoldersAsync(rootId, token, ct))
            .Where(a => uint.TryParse(a.Name, out _)).ToList();
        if (accounts.Count == 0) return new CloudProbeResult(0, 0, null, null);
        var apps = (await ListDriveFoldersAsync(accounts[0].Id, token, ct))
            .Where(a => IsAppDir(a.Name)).ToList();
        var sample = apps.Count > 0 ? $"{accounts[0].Name}/{apps[0].Name}/…" : null;
        return new CloudProbeResult(accounts.Count, apps.Count, sample, null);
    }

    private async Task<CloudProbeResult> ProbeOneDriveAsync(CancellationToken ct)
    {
        var token = await GetOneDriveAccessTokenAsync(ct);
        var rootChildren = await GetGraphChildrenByPathAsync(RootFolderName, token, ct);
        var accounts = rootChildren
            .Where(c => c.IsFolder && uint.TryParse(c.Name, out _)).ToList();
        if (accounts.Count == 0) return new CloudProbeResult(0, 0, null, null);
        var apps = (await GetGraphChildrenByIdAsync(accounts[0].Id, token, ct))
            .Where(c => c.IsFolder && IsAppDir(c.Name)).ToList();
        var sample = apps.Count > 0 ? $"{accounts[0].Name}/{apps[0].Name}/…" : null;
        return new CloudProbeResult(accounts.Count, apps.Count, sample, null);
    }

    private async Task<CloudProbeResult> ProbeS3Async(string provider, CancellationToken ct)
    {
        var ep = ResolveS3Endpoint(provider);
        var http = ResolveS3Http(provider, ep);
        var accounts = (await ListS3PrefixesAsync(http, ep, ep.RootPrefix, ct))
            .Where(a => uint.TryParse(a, out _)).ToList();
        if (accounts.Count == 0) return new CloudProbeResult(0, 0, null, null);
        var apps = (await ListS3PrefixesAsync(http, ep, $"{ep.RootPrefix}{accounts[0]}/", ct))
            .Where(IsAppDir).ToList();
        var sample = apps.Count > 0 ? $"{ep.RootPrefix}{accounts[0]}/{apps[0]}/…" : null;
        return new CloudProbeResult(accounts.Count, apps.Count, sample, null);
    }

    private HttpClient GetS3Client(S3Endpoint ep, bool allowInsecureTls, string caCertPath)
    {
        var key = $"{ep.Scheme}://{ep.Host}|{allowInsecureTls}|{caCertPath}";
        lock (_s3ClientLock)
        {
            // 命中移到队尾；超限摘除最早并释放。淘汰撞上在飞请求会 abort 当次，
            // 但只发生在换 endpoint/证书这种本来就中断的场景，可接受
            if (_s3Clients.TryGetValue(key, out var cached))
            {
                _s3ClientOrder.Remove(key);
                _s3ClientOrder.AddLast(key);
                return cached;
            }
            var client = BuildS3Client(ep, allowInsecureTls, caCertPath);
            _s3Clients[key] = client;
            _s3ClientOrder.AddLast(key);
            while (_s3Clients.Count > MaxS3Clients && _s3ClientOrder.First != null)
            {
                var oldest = _s3ClientOrder.First.Value;
                _s3ClientOrder.RemoveFirst();
                if (_s3Clients.Remove(oldest, out var evicted))
                    try { evicted.Dispose(); } catch { }
            }
            return client;
        }
    }

    private static HttpClient BuildS3Client(S3Endpoint ep, bool allowInsecureTls, string caCertPath)
    {
        var handler = new HttpClientHandler();
        if (ep.Scheme == "http" || allowInsecureTls)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }
        else if (!string.IsNullOrEmpty(caCertPath) && File.Exists(caCertPath))
        {
            // 自建存储的私有 CA：链验证时追加该证书，不过则拒绝
            X509Certificate2? ca = null;
            try { ca = new X509Certificate2(caCertPath); } catch { }
            handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                if (ca == null || cert == null || chain == null) return false;
                chain.ChainPolicy.ExtraStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                return chain.Build((X509Certificate2)cert);
            };
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    private static string S3Escape(string value)
    {
        // SigV4 要求 RFC3986 编码；EscapeDataString 遗留 !'()* 需补编码
        return Uri.EscapeDataString(value)
            .Replace("!", "%21").Replace("'", "%27")
            .Replace("(", "%28").Replace(")", "%29").Replace("*", "%2A");
    }

    private static string S3EncodePath(string path) =>
        string.Join("/", path.Split('/').Select(S3Escape));

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static byte[] Hmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private async Task<HttpResponseMessage> SendS3Async(HttpClient http, string method, S3Endpoint ep,
        string key, SortedDictionary<string, string> query, CancellationToken ct, string body = "",
        IDictionary<string, string>? extraHeaders = null)
    {
        var encodedKey = string.IsNullOrEmpty(key) ? "" : "/" + S3EncodePath(key);
        var canonicalQuery = string.Join("&",
            query.Select(kv => $"{S3Escape(kv.Key)}={S3Escape(kv.Value)}"));
        var path = $"/{ep.Bucket}{encodedKey}{(canonicalQuery.Length > 0 ? "?" + canonicalQuery : "")}";
        var uri = new Uri($"{ep.Scheme}://{ep.Host}{path}");
        var amzDate = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = amzDate[..8];
        // 载荷哈希：勾选“携带签名载荷”时签 body（本类当前只发空 body），否则 UNSIGNED；
        // 与 DLL 的 sign_payload 语义一致，R2 恒为 unsigned
        var payloadHash = ep.SignPayload ? Sha256Hex(body) : "UNSIGNED-PAYLOAD";
        var hostHeader = uri.Host + (uri.IsDefaultPort ? "" : $":{uri.Port}");
        // 附加头一并签名；无附加头时拼出的串与原来逐字节一致
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = hostHeader,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        if (extraHeaders != null)
        {
            foreach (var kv in extraHeaders)
                signed[kv.Key.ToLowerInvariant()] = kv.Value.Trim();
        }
        var canonicalHeaders = string.Join("", signed.Select(kv => $"{kv.Key}:{kv.Value}\n"));
        var signedNames = string.Join(";", signed.Keys);
        var canonical = $"{method}\n/{ep.Bucket}{encodedKey}\n{canonicalQuery}\n{canonicalHeaders}\n{signedNames}\n{payloadHash}";
        var scope = $"{dateStamp}/{ep.Region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256Hex(canonical)}";
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + ep.SecretKey);
        var kDate = Hmac(kSecret, dateStamp);
        var kRegion = Hmac(kDate, ep.Region);
        var kService = Hmac(kRegion, "s3");
        var kSigning = Hmac(kService, "aws4_request");
        var signature = Convert.ToHexString(Hmac(kSigning, stringToSign)).ToLowerInvariant();
        using var req = new HttpRequestMessage(new HttpMethod(method), uri);
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        if (body.Length > 0)
            req.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        if (extraHeaders != null)
        {
            // Content-MD5 等属于 content 头，放请求头会被静默丢弃，必须挂到 Content 上
            foreach (var kv in extraHeaders)
            {
                if (!req.Headers.TryAddWithoutValidation(kv.Key, kv.Value))
                    req.Content?.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }
        req.Headers.Authorization = new AuthenticationHeaderValue("AWS4-HMAC-SHA256",
            $"Credential={ep.AccessKey}/{scope}, SignedHeaders={signedNames}, Signature={signature}");
        return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static string S3Error(HttpStatusCode status, string body)
    {
        string code = "", message = "";
        try
        {
            var doc = XDocument.Parse(body);
            XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
            code = NsValue(doc.Root, ns, "Code");
            message = NsValue(doc.Root, ns, "Message");
        }
        catch { }
        var hint = code switch
        {
            "NoSuchBucket" => L.Get("Cloud_NoSuchBucket"),
            "InvalidAccessKeyId" => L.Get("Cloud_InvalidKey"),
            "SignatureDoesNotMatch" => L.Get("Cloud_SigMismatch"),
            "AccessDenied" => L.Get("Cloud_AccessDenied"),
            "NoSuchKey" => L.Get("Cloud_NoSuchKey"),
            _ => string.IsNullOrEmpty(message) ? $"HTTP {(int)status}" : message,
        };
        return string.IsNullOrEmpty(code) ? hint : $"{hint}（{code}）";
    }

    private sealed record S3Object(string Key, long Size, DateTime? LastModified);

    // 部分自建 S3 实现返回的 XML 不带 xmlns：命名空间优先，取不到回退无命名空间，避免静默空列表
    private static IEnumerable<XElement> NsElements(XElement root, XNamespace ns, string name)
    {
        var list = root.Elements(ns + name).ToList();
        return list.Count > 0 ? list : root.Elements(name);
    }

    private static string NsValue(XElement? parent, XNamespace ns, string name) =>
        parent?.Element(ns + name)?.Value ?? parent?.Element(name)?.Value ?? "";

    // delimiter 分级列举：prefix + "/" 切出一级子目录；IsTruncated 翻页
    private async Task<List<string>> ListS3PrefixesAsync(HttpClient http, S3Endpoint ep, string prefix, CancellationToken ct)
    {
        var result = new List<string>();
        string? token = null;
        XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
        do
        {
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["list-type"] = "2",
                ["prefix"] = prefix,
                ["delimiter"] = "/",
                ["max-keys"] = "1000",
            };
            if (token != null) query["continuation-token"] = token;
            using var resp = await SendS3Async(http, "GET", ep, "", query, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(L.Get("Cloud_S3OpFailed", S3Error(resp.StatusCode, body)));
            var doc = XDocument.Parse(body);
            var root = doc.Root!;
            foreach (var cp in NsElements(root, ns, "CommonPrefixes"))
            {
                var p = NsValue(cp, ns, "Prefix");
                var name = p.StartsWith(prefix, StringComparison.Ordinal) ? p[prefix.Length..].TrimEnd('/') : "";
                if (name.Length > 0) result.Add(name);
            }
            var truncated = NsValue(root, ns, "IsTruncated") == "true";
            token = truncated ? NsValue(root, ns, "NextContinuationToken") : null;
        } while (!string.IsNullOrEmpty(token));
        return result;
    }

    // 无 delimiter 全量列举：统计与下载删除共用
    private async Task<List<S3Object>> ListS3ObjectsAsync(HttpClient http, S3Endpoint ep, string prefix, CancellationToken ct)
    {
        var result = new List<S3Object>();
        string? token = null;
        XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
        do
        {
            var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["list-type"] = "2",
                ["prefix"] = prefix,
                ["max-keys"] = "1000",
            };
            if (token != null) query["continuation-token"] = token;
            using var resp = await SendS3Async(http, "GET", ep, "", query, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(L.Get("Cloud_S3OpFailed", S3Error(resp.StatusCode, body)));
            var doc = XDocument.Parse(body);
            var root = doc.Root!;
            foreach (var c in NsElements(root, ns, "Contents"))
            {
                var key = NsValue(c, ns, "Key");
                if (key.Length == 0) continue;
                long.TryParse(NsValue(c, ns, "Size"), out var size);
                DateTime? modified = null;
                if (DateTimeOffset.TryParse(NsValue(c, ns, "LastModified"), out var dto))
                    modified = dto.LocalDateTime;
                result.Add(new S3Object(key, size, modified));
            }
            var truncated = NsValue(root, ns, "IsTruncated") == "true";
            token = truncated ? NsValue(root, ns, "NextContinuationToken") : null;
        } while (!string.IsNullOrEmpty(token));
        return result;
    }

    private async Task<List<CloudAppEntry>> ListS3AppsAsync(string provider, IProgress<string>? progress, CancellationToken ct)
    {
        lock (_listFailures) _listFailures.Clear();
        progress?.Report(L.Get("Cloud_ConnectingSource", provider == "r2" ? "R2" : "S3"));
        var ep = ResolveS3Endpoint(provider);
        var http = ResolveS3Http(provider, ep);
        progress?.Report(L.Get("Cloud_ReadingCloudDir"));
        // 全量递归列举一次，本地按 <账号>/<游戏>/ 分组；原来每游戏一次前缀列举直接省掉，
        // 共享桶里的无关键按同样规则过滤
        var objects = await ListS3ObjectsAsync(http, ep, ep.RootPrefix, ct);
        var groups = new Dictionary<(string Account, int AppId), List<S3Object>>();
        foreach (var o in objects)
        {
            if (!o.Key.StartsWith(ep.RootPrefix, StringComparison.Ordinal)) continue;
            var rel = o.Key.Substring(ep.RootPrefix.Length);
            var slash = rel.IndexOf('/');
            if (slash <= 0) continue;
            var account = rel.Substring(0, slash);
            var rest = rel.Substring(slash + 1);
            var slash2 = rest.IndexOf('/');
            var appPart = slash2 < 0 ? rest : rest.Substring(0, slash2);
            if (!uint.TryParse(account, out _) || !int.TryParse(appPart, out var appId) || appId == 0) continue;
            if (!groups.TryGetValue((account, appId), out var list)) groups[(account, appId)] = list = new List<S3Object>();
            list.Add(o);
        }
        var apps = groups.Keys.ToList();
        var emptyQuery = new SortedDictionary<string, string>(StringComparer.Ordinal);
        return await RunParallelListAsync(apps,
            a => $"AppID {a.AppId}",
            async (a, ct) =>
            {
                DateTime? time = null;
                var statsKey = $"{ep.RootPrefix}{a.Account}/{a.AppId}/{StatsFileName}";
                using (var statsResp = await SendS3Async(http, "GET", ep, statsKey, emptyQuery, ct))
                {
                    if (statsResp.StatusCode == HttpStatusCode.OK)
                        time = TryParseCloudTime(await statsResp.Content.ReadAsStringAsync(ct));
                }
                time ??= groups[a].Where(o => o.LastModified.HasValue).Select(o => o.LastModified!.Value)
                    .DefaultIfEmpty().Max();
                if (time == default) time = null;
                return new CloudAppEntry(a.Account, a.AppId, time, null, null);
            }, progress, ct);
    }

    private async Task<CloudAppStats> GetS3AppStatsAsync(string provider, string accountId, int appId, CancellationToken ct)
    {
        var ep = ResolveS3Endpoint(provider);
        var http = ResolveS3Http(provider, ep);
        var appPrefix = $"{ep.RootPrefix}{accountId}/{appId}/";
        var objects = await ListS3ObjectsAsync(http, ep, appPrefix, ct);
        if (objects.Count == 0)
            throw new InvalidOperationException(L.Get("Cloud_NoGameDir", appId));
        return new CloudAppStats(objects.Count, objects.Sum(o => o.Size),
            DescribeRemote(provider, accountId, appId, ep.Bucket, ep.RootPrefix));
    }

    private async Task<int> DownloadS3AppAsync(string provider, string accountId, int appId, string destDir,
        IProgress<string>? progress, CancellationToken ct)
    {
        var ep = ResolveS3Endpoint(provider);
        var http = ResolveS3Http(provider, ep);
        var appPrefix = $"{ep.RootPrefix}{accountId}/{appId}/";
        var objects = await ListS3ObjectsAsync(http, ep, appPrefix, ct);
        if (objects.Count == 0)
            throw new InvalidOperationException(L.Get("Cloud_NoGameDir", appId));
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal);
        return await RunParallelDownloadAsync(objects,
            async (obj, ct) =>
            {
                var rel = obj.Key.StartsWith(appPrefix, StringComparison.Ordinal) ? obj.Key[appPrefix.Length..] : obj.Key;
                var dest = Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var resp = await SendS3Async(http, "GET", ep, obj.Key, query, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    throw new InvalidOperationException(L.Get("Cloud_DownloadRemoteFailed", rel, S3Error(resp.StatusCode, body)));
                }
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst, ct);
            }, progress, ct);
    }

    private static string RelS3Key(string key, string appPrefix) =>
        key.StartsWith(appPrefix, StringComparison.Ordinal) ? key[appPrefix.Length..] : key;

    // S3 Multi-Object Delete：一次删一批；返回失败的 key（相对桶的全路径）；
    // 传输级失败（非 2xx、XML 解析失败）抛异常，由调用方回退逐个删
    private async Task<List<string>> MultiDeleteS3ObjectsAsync(HttpClient http, S3Endpoint ep,
        S3Object[] chunk, CancellationToken ct)
    {
        var doc = new XDocument(new XElement("Delete",
            new XElement("Quiet", "false"),
            chunk.Select(o => new XElement("Object", new XElement("Key", o.Key)))));
        var xml = doc.ToString(SaveOptions.DisableFormatting);
        var md5 = Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(xml)));
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["delete"] = "" };
        using var resp = await SendS3Async(http, "POST", ep, "", query, ct, xml,
            new Dictionary<string, string> { ["Content-MD5"] = md5 });
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(L.Get("Cloud_BatchDeleteFailed", S3Error(resp.StatusCode, body)));
        XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
        XDocument result;
        try
        {
            result = XDocument.Parse(body);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(L.Get("Cloud_BatchDeleteRespFailed", ex.Message), ex);
        }
        var failed = new List<string>();
        foreach (var e in NsElements(result.Root!, ns, "Error"))
        {
            var key = NsValue(e, ns, "Key");
            if (key.Length > 0) failed.Add(key);
        }
        return failed;
    }

    private async Task<CloudDeleteResult> DeleteS3AppAsync(string provider, string accountId, int appId,
        IProgress<string>? progress, CancellationToken ct)
    {
        var ep = ResolveS3Endpoint(provider);
        var http = ResolveS3Http(provider, ep);
        var appPrefix = $"{ep.RootPrefix}{accountId}/{appId}/";
        var objects = await ListS3ObjectsAsync(http, ep, appPrefix, ct);
        if (objects.Count == 0)
            return new CloudDeleteResult(0, 0, new List<string>(), L.Get("Cloud_NoGameDir", appId));
        // 批量删除（单次最多 1000 个，响应自带逐 key 明细）；整批传输失败才回退逐个删
        var deleted = 0;
        var failed = new List<string>();
        var done = 0;
        foreach (var chunk in objects.Chunk(1000))
        {
            ct.ThrowIfCancellationRequested();
            List<string>? chunkFailed = null;
            try
            {
                chunkFailed = await MultiDeleteS3ObjectsAsync(http, ep, chunk, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogDeleteErrors", ex.Message));
            }
            if (chunkFailed == null)
            {
                foreach (var obj in chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    done++;
                    progress?.Report(L.Get("Cloud_DeletingBatch", done, objects.Count));
                    try
                    {
                        using var resp = await SendS3Async(http, "DELETE", ep, obj.Key,
                            new SortedDictionary<string, string>(StringComparer.Ordinal), ct);
                        if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
                            deleted++;
                        else
                            failed.Add(RelS3Key(obj.Key, appPrefix));
                    }
                    catch
                    {
                        failed.Add(RelS3Key(obj.Key, appPrefix));
                    }
                }
            }
            else
            {
                done += chunk.Length;
                progress?.Report(L.Get("Cloud_DeletingBatch", done, objects.Count));
                deleted += chunk.Length - chunkFailed.Count;
                failed.AddRange(chunkFailed.Select(k => RelS3Key(k, appPrefix)));
            }
        }
        return new CloudDeleteResult(deleted, failed.Count, failed.Take(10).ToList(),
            failed.Count > 0 ? L.Get("Cloud_RemoteFilesDeleteFailed", failed.Count) : null);
    }
}
