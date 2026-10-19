using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SteamLuaManager.Services;

public sealed record CloudSaveStatus(
    bool CloudEnabled,
    string SyncPath);

public sealed record RedirectedApp(
    int AppId, string SaveDir, DateTime? LastSaveTime,
    string? AccountId = null, string? RemoteId = null, string? WebUrl = null);

public sealed record MigrateResult(int MovedFiles, long MovedBytes, List<string> FailedFiles);

// 删除目标种类：sync=重定向存档目录，cache=DLL 本地缓存，userdata=Steam 用户数据，cloud=云端远端目录
public sealed record DeleteTarget(
    string Kind, string AccountId, string Path, int FileCount, long TotalBytes, string? Provider = null);

public sealed record DeletePreview(int AppId, List<DeleteTarget> Targets, string BackupDir);

    // DisplayKey 非空表示显示名需要跟语言走，由消费侧按当前语言实时取值
    public sealed record CloudProviderOption(string Id, string DisplayName, string? DisplayKey = null);

public sealed record R2Credentials(
    string AccountId, string AccessKeyId, string SecretAccessKey,
    string Bucket, string KeyPrefix = "", string Endpoint = "");

public sealed record S3Credentials(
    string AccessKeyId, string SecretAccessKey, string Bucket,
    string Endpoint, string Region, string KeyPrefix = "",
    bool SignPayload = false, bool AllowInsecureHttp = false,
    bool AllowInsecureTls = false, string CaCertPath = "");

// WebDAV 通用配置：url 指向目标集合，auth 取 basic/auto/digest/ntlm/negotiate/bearer/none；
// extra_headers 与 DLL 一致，一行一个 "Name: value"（如 IIS 的 Translate: f）。
public sealed record WebDavCredentials(
    string Url, string Username, string Password, string Auth = "basic",
    string BearerToken = "", string KeyPrefix = "",
    bool AllowInsecureHttp = false, bool AllowInsecureTls = false,
    int TimeoutSec = 0, string ExtraHeaders = "");

public interface ICloudRedirectService
{
    Task<CloudSaveStatus> RefreshStatusAsync(CancellationToken ct = default);
    Task EnableAsync(string? syncPath, IProgress<string>? status, CancellationToken ct = default);
    Task DisableAsync();
    Task SetSyncPathAsync(string path);
    Task<List<RedirectedApp>> GetRedirectedAppsAsync(IProgress<string>? progress = null, CancellationToken ct = default);
    Task<MigrateResult> MigrateSavesAsync(string oldPath, string newPath, IProgress<string>? status, CancellationToken ct = default);
    string GetDefaultSyncPath();
    Task<DeletePreview> PreviewAppDeleteAsync(int appId, IProgress<string>? progress = null, CancellationToken ct = default);
    Task DeleteAppSavesAsync(DeletePreview preview, IProgress<string>? status, CancellationToken ct = default);
    // 云端源下打开路径的目标地址；本地源返回空，由调用方走目录打开
    string GetCloudConsoleUrl(int appId);
    IReadOnlyList<CloudProviderOption> ProviderOptions { get; }
    string GetCloudProvider();
    void SetCloudProvider(string provider);
    string GetTokenPath(string provider);
    (bool Ok, string Message) CheckOAuthToken(string provider);
    // R2/S3 凭证状态：区分缺文件、文件存在但解不开（别机复制）、内容不完整
    (bool Ok, string Message) CheckStoredCredentials(string provider);
    // 有效远端根目录展示（供两台机器核对前缀用）；无配置返回空
    string GetEffectiveRemoteRoot();
    // 最近一次云端名单读取中跳过的失败项（内存记录，不依赖日志开关）
    IReadOnlyList<string> GetLastListFailures();
    // 连接测试：只列举两级目录；本地源抛错
    Task<CloudProviderStore.CloudProbeResult> TestCloudConnectionAsync(CancellationToken ct = default);
    // 退出登录：删除 token/凭证文件；DLL 共用同一文件，退出后该源同步即失效
    void SignOut(string provider);
    string SaveR2Credentials(R2Credentials cred);
    R2Credentials? LoadR2Credentials();
    string SaveS3Credentials(S3Credentials cred);
    S3Credentials? LoadS3Credentials();
    string SaveWebDavCredentials(WebDavCredentials cred);
    WebDavCredentials? LoadWebDavCredentials();
}

public class CloudRedirectService : ICloudRedirectService
{
    private const string DllFileName = "cloud_redirect.dll";
    private const string EmbeddedResourceName = "SteamLuaManager.Resources.CloudRedirectDll.zip";

    private readonly ISteamPathService _steamPathService;
    private readonly CloudProviderStore _store;
    private readonly object _embedLock = new();
    private byte[]? _embeddedDll;
    // 最近一次云端名单：打开控制台与删除时定位远端目录；切源后刷新覆盖，不做跨源保留
    private readonly Dictionary<int, List<CloudAppEntry>> _cloudEntries = new();
    private readonly object _cloudLock = new();

    public CloudRedirectService(ISteamPathService steamPathService)
    {
        _steamPathService = steamPathService;
        _store = new CloudProviderStore(LoadR2Credentials, LoadS3Credentials, LoadWebDavCredentials, GetTokenPath);
    }

    private string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloudRedirect");

    private static string DefaultSyncPath(string steamPath) => Path.Combine(steamPath, "Cloud_Archiving");

    // Steam 根目录：自定义优先，否则自动探测；找不到返回 null
    private string? ResolveSteamPath() =>
        _steamPathService.GetCustomPath() ?? _steamPathService.DetectSteamPath();

    // 当前生效路径：已配置用配置值，否则回默认；Steam 未找到时返回空
    private string ResolveSyncPath()
    {
        var syncPath = GetConfiguredSyncPath();
        if (!string.IsNullOrEmpty(syncPath)) return syncPath;
        var steamPath = ResolveSteamPath();
        return string.IsNullOrEmpty(steamPath) ? string.Empty : DefaultSyncPath(steamPath);
    }

    private string GetConfiguredSyncPath()
    {
        try
        {
            var configPath = Path.Combine(ConfigDir, "config.json");
            if (!File.Exists(configPath)) return string.Empty;
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
            if (doc.RootElement.TryGetProperty("sync_path", out var s) && s.ValueKind == JsonValueKind.String)
                return s.GetString() ?? string.Empty;
            return string.Empty;
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogReadDirFailed", ex.Message));
            return string.Empty;
        }
    }

    public Task<CloudSaveStatus> RefreshStatusAsync(CancellationToken ct = default)
    {
        var enabled = _steamPathService.GetCloudEnabled();
        var syncPath = ResolveSyncPath();
        return Task.FromResult(new CloudSaveStatus(enabled, syncPath));
    }

    public Task EnableAsync(string? syncPath, IProgress<string>? status, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var steamPath = ResolveSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            throw new InvalidOperationException(L.Get("Cloud_EnableNoPath"));
        // 未显式传路径时优先沿用 config.json 里已有的，不存在才回默认；
        // 否则每次开关都会把用户自定义目录洗成默认目录
        if (string.IsNullOrWhiteSpace(syncPath))
            syncPath = GetConfiguredSyncPath();
        if (string.IsNullOrWhiteSpace(syncPath))
            syncPath = DefaultSyncPath(steamPath);

        status?.Report(L.Get("Cloud_PreparingDir"));
        try
        {
            Directory.CreateDirectory(syncPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(L.Get("Cloud_DirUnavailable", ex.Message), ex);
        }

        status?.Report(L.Get("Cloud_DeployingDll"));
        EnsureDllDeployed(steamPath);

        status?.Report(L.Get("Cloud_WritingKernelSwitch"));
        if (!_steamPathService.SetCloudEnabled(true))
            throw new InvalidOperationException(L.Get("Settings_TomlWriteFailed"));

        status?.Report(L.Get("Cloud_WritingRedirectCfg"));
        WriteRedirectConfig(syncPath);
        SyncPinConfigCloudEnabled(steamPath, true);

        LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogEnabledWithPath", syncPath));
        return Task.CompletedTask;
    }

    public Task DisableAsync()
    {
        var steamPath = ResolveSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            throw new InvalidOperationException(L.Get("Cloud_NoSteamPath"));
        // DLL 与配置文件保留，仅关闭开关，下次启用无需重新部署
        if (!_steamPathService.SetCloudEnabled(false))
            throw new InvalidOperationException(L.Get("Settings_TomlWriteFailed"));
        SyncPinConfigCloudEnabled(steamPath, false);
        LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogCloudDisabled"));
        return Task.CompletedTask;
    }

    // DLL 内部总闸（<Steam>\cloud_redirect\config.json 的 cloud_redirect 布尔值）：
    // 文件不存在绝不创建（缺省即开启）；存在才跟随界面开关同步，其余键原样保留；
    // 解析失败原样保留；全程不抛异常，不阻断主开关流程
    private void SyncPinConfigCloudEnabled(string steamPath, bool enabled)
    {
        try
        {
            var pinPath = Path.Combine(steamPath, "cloud_redirect", "config.json");
            if (!File.Exists(pinPath)) return;
            JsonObject root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(pinPath))?.AsObject() ?? new JsonObject();
            }
            catch
            {
                LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogPinParseFailed"));
                return;
            }
            if (root["cloud_redirect"]?.GetValue<bool>() == enabled) return;
            root["cloud_redirect"] = enabled;
            CloudCredentialStore.AtomicWriteAllText(pinPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_PinSynced", enabled ? L.Get("Cloud_PinOn") : L.Get("Cloud_PinOff")));
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogPinSyncFailed", ex.Message));
        }
    }

    // 目录切换时把旧目录存档搬到新目录：逐文件复制，失败跳过并记录；
    // 全部成功才删源目录，任何失败都保留源目录并如实报告
    public async Task<MigrateResult> MigrateSavesAsync(string oldPath, string newPath, IProgress<string>? status, CancellationToken ct = default)
    {
        var failed = new List<string>();
        long movedBytes = 0;
        int movedFiles = 0;

        List<string> allFiles;
        try
        {
            allFiles = Directory.GetFiles(oldPath, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(L.Get("Cloud_ReadOldDirFailed", ex.Message), ex);
        }
        if (allFiles.Count == 0)
            return new MigrateResult(0, 0, failed);

        // 新旧目录嵌套会自我复制，提前拦截
        var oldRoot = Path.GetFullPath(oldPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var newRoot = Path.GetFullPath(newPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (newRoot.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase)
            || oldRoot.StartsWith(newRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(L.Get("Cloud_NestedDirs"));

        Directory.CreateDirectory(newPath);
        var done = 0;
        foreach (var src in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var dest = Path.Combine(newPath, Path.GetRelativePath(oldRoot.TrimEnd(Path.DirectorySeparatorChar), src));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await using var inFs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
                await using var outFs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                await inFs.CopyToAsync(outFs, ct);
                movedBytes += inFs.Length;
                movedFiles++;
            }
            catch (Exception)
            {
                failed.Add(Path.GetFileName(src));
            }
            done++;
            if (done % 25 == 0 || done == allFiles.Count)
                status?.Report(L.Get("Cloud_Migrating", done, allFiles.Count));
        }

        if (failed.Count == 0)
        {
            try { Directory.Delete(oldPath, recursive: true); }
            catch (Exception ex)
            {
                // 删源失败不算迁移失败：文件已就位，残留由用户手动清理
                LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogCleanOldFailed", ex.Message));
            }
        }
        return new MigrateResult(movedFiles, movedBytes, failed);
    }

    public string GetDefaultSyncPath()
    {
        var steamPath = ResolveSteamPath();
        return string.IsNullOrEmpty(steamPath) ? string.Empty : DefaultSyncPath(steamPath);
    }

    public Task SetSyncPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(L.Get("Cloud_PickLocalDir"));
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(L.Get("Cloud_DirUnavailable", ex.Message), ex);
        }
        WriteRedirectConfig(path);
        LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogSyncDirSwitched", path));
        return Task.CompletedTask;
    }

    // 内嵌 DLL 字节：首次使用时释放一次并常驻，后续复用
    private byte[]? GetEmbeddedDllBytes()
    {
        lock (_embedLock)
        {
            if (_embeddedDll != null) return _embeddedDll;
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
                if (stream == null)
                {
                    LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_EmbeddedDllMissing"));
                    return null;
                }
                using var zip = new ZipArchive(stream);
                var entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(Path.GetFileName(e.FullName), DllFileName, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_EmbeddedEntryMissing"));
                    return null;
                }
                using var entryStream = entry.Open();
                using var ms = new MemoryStream();
                entryStream.CopyTo(ms);
                _embeddedDll = ms.ToArray();
                return _embeddedDll;
            }
            catch (Exception ex)
            {
                LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogReadEmbeddedFailed", ex.Message));
                return null;
            }
        }
    }

    // 部署目标：toml 配了 library 则尊重（绝对路径直接用，相对路径相对 Steam 根目录），否则用内核默认位置
    private string ResolveLibraryTarget(string steamPath)
    {
        var configured = _steamPathService.GetCloudLibraryPath();
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(steamPath, DllFileName);
        if (Path.IsPathRooted(configured)) return configured;
        return Path.Combine(steamPath, configured);
    }

    private void EnsureDllDeployed(string steamPath)
    {
        var embedded = GetEmbeddedDllBytes();
        if (embedded == null || embedded.Length == 0)
            throw new InvalidOperationException(L.Get("Cloud_EmbeddedReinstall"));

        var target = ResolveLibraryTarget(steamPath);
        if (File.Exists(target))
        {
            try
            {
                // 长度先行：不等必不同，省掉两次哈希；相等才流式哈希比对（不进内存整份拷贝）
                if (new FileInfo(target).Length == embedded.Length)
                {
                    using var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sha = SHA256.Create();
                    if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(embedded), sha.ComputeHash(fs)))
                        return;
                }
            }
            catch { }
            // 目标被 Steam 占用时覆盖必失败，先探后写
            try
            {
                using var probe = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException(L.Get("Cloud_DllLocked", DllFileName), ex);
            }
        }

        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, embedded);
            var destAttr = File.Exists(target) ? File.GetAttributes(target) : FileAttributes.Normal;
            if ((destAttr & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(target, FileAttributes.Normal);
            File.Move(tmp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new InvalidOperationException(L.Get("Cloud_DeployDllFailed", ex.Message), ex);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (File.Exists(target + ".new")) File.Delete(target + ".new"); } catch { }
        }
        LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogDllDeployed", target));
    }

    private void WriteRedirectConfig(string syncPath, string? provider = null)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var configPath = Path.Combine(ConfigDir, "config.json");
            JsonObject root;
            if (File.Exists(configPath))
            {
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject();
                }
                catch
                {
                    root = new JsonObject();
                }
            }
            else
            {
                root = new JsonObject();
            }

            // 仅写自有键，未知键原样保留；成就与时长跟随云端同步；DLL 自更新关闭以免覆盖已部署版本；
            // provider 传空=沿用现有值，免得重启用把已选云端打回本地
            var current = root["provider"]?.GetValue<string>();
            root["provider"] = string.IsNullOrEmpty(provider) ? (string.IsNullOrEmpty(current) ? "folder" : current) : provider;
            root["sync_path"] = syncPath;
            root["sync_achievements"] = true;
            root["sync_playtime"] = true;
            root["auto_update_dll"] = false;

            CloudCredentialStore.AtomicWriteAllText(configPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(L.Get("Cloud_WriteRedirectCfgFailed", ex.Message), ex);
        }
    }

    // ---- 云端提供商 ----

    public IReadOnlyList<CloudProviderOption> ProviderOptions => BuildProviderOptions();

    private static IReadOnlyList<CloudProviderOption> BuildProviderOptions() => new List<CloudProviderOption>
    {
        new("folder", L.Get("Cloud_ProviderLocal"), "Cloud_ProviderLocal"),
        new("gdrive", "Google Drive"),
        new("onedrive", "OneDrive"),
        new("r2", "Cloudflare R2"),
        new("s3", L.Get("Cloud_ProviderS3"), "Cloud_ProviderS3"),
        new("webdav", "WebDAV"),
    }.AsReadOnly();

    private static bool IsKnownProvider(string? id) =>
        id is "folder" or "gdrive" or "onedrive" or "r2" or "s3" or "webdav";

    // 当前提供商：读不到/读到未知值一律回本地目录，保证 DLL 侧永远有确定行为
    public string GetCloudProvider()
    {
        try
        {
            var configPath = Path.Combine(ConfigDir, "config.json");
            if (!File.Exists(configPath)) return "folder";
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "folder";
            if (doc.RootElement.TryGetProperty("provider", out var p) &&
                p.ValueKind == JsonValueKind.String && IsKnownProvider(p.GetString()))
                return p.GetString()!;
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogReadProviderFailed", ex.Message));
        }
        return "folder";
    }

    // 切换提供商：只写 provider + token_path（及各源路径注册表），不动其他键；
    // 切到云端后仍需登录/填凭证才算真正可用，状态由 CheckOAuthToken / 凭证校验体现
    public void SetCloudProvider(string provider)
    {
        if (!IsKnownProvider(provider))
            throw new InvalidOperationException(L.Get("Cloud_UnknownProvider", provider));
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var configPath = Path.Combine(ConfigDir, "config.json");
            JsonObject root;
            if (File.Exists(configPath))
            {
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject();
                }
                catch
                {
                    root = new JsonObject();
                }
            }
            else
            {
                root = new JsonObject();
            }

            root["provider"] = provider;
            var tokenPath = GetTokenPath(provider);
            if (!string.IsNullOrEmpty(tokenPath))
                root["token_path"] = tokenPath;

            var registry = new JsonObject();
            if (root["token_paths"] is JsonObject existing)
            {
                foreach (var kv in existing)
                {
                    if (kv.Value?.GetValueKind() == JsonValueKind.String)
                        registry[kv.Key] = kv.Value!.GetValue<string>();
                }
            }
            if (!string.IsNullOrEmpty(tokenPath))
                registry[provider] = tokenPath;
            root["token_paths"] = registry;

            var tmp = configPath + ".new";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, configPath, overwrite: true);
            LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogProviderSet", provider));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(L.Get("Cloud_SwitchProviderFailed", ex.Message), ex);
        }
    }

    // 各源凭证/ token 默认落点，与上游 companion 路径一致，DLL 与官方客户端互认
    public string GetTokenPath(string provider)
    {
        try
        {
            var configPath = Path.Combine(ConfigDir, "config.json");
            if (File.Exists(configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("token_paths", out var reg) &&
                    reg.ValueKind == JsonValueKind.Object &&
                    reg.TryGetProperty(provider, out var p) &&
                    p.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p.GetString()))
                    return p.GetString()!;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogTokenRegFailed", ex.Message));
        }
        return provider switch
        {
            "gdrive" => Path.Combine(ConfigDir, "google_tokens.json"),
            "onedrive" => Path.Combine(ConfigDir, "onedrive_tokens.json"),
            "r2" => Path.Combine(ConfigDir, "r2_credentials.json"),
            "s3" => Path.Combine(ConfigDir, "s3_credentials.json"),
            "webdav" => Path.Combine(ConfigDir, "webdav_credentials.json"),
            _ => string.Empty,
        };
    }

    // OAuth 类源看 refresh_token 是否存在；R2/S3 看必填字段是否齐
    public (bool Ok, string Message) CheckOAuthToken(string provider)
    {
        if (provider is not ("gdrive" or "onedrive"))
            return (false, L.Get("Cloud_NoOAuthNeeded"));
        return CloudOAuthService.CheckTokenStatus(GetTokenPath(provider)) switch
        {
            (true, var msg) => (true, msg),
            (false, var msg) => (false, msg),
        };
    }

    public (bool Ok, string Message) CheckStoredCredentials(string provider)
    {
        try
        {
            if (provider is "gdrive" or "onedrive")
                return CheckOAuthToken(provider);
            var (path, label) = provider switch
            {
                "r2" => (Path.Combine(ConfigDir, "r2_credentials.json"), "R2"),
                "s3" => (Path.Combine(ConfigDir, "s3_credentials.json"), "S3"),
                "webdav" => (Path.Combine(ConfigDir, "webdav_credentials.json"), "WebDAV"),
                _ => ("", ""),
            };
            if (string.IsNullOrEmpty(path)) return (false, "");
            if (!File.Exists(path))
                return (false, L.Get("Cloud_CredNotConfigured", label));
            if (string.IsNullOrEmpty(CloudCredentialStore.ReadJson(path)))
                return (false, L.Get("Cloud_CredUndecryptable"));
            var complete = provider switch
            {
                "r2" => LoadR2Credentials() != null,
                "s3" => LoadS3Credentials() != null,
                "webdav" => LoadWebDavCredentials() != null,
                _ => false,
            };
            if (!complete)
                return (false, L.Get("Cloud_CredIncomplete"));
            return (true, L.Get("Cloud_CredConfigured", label));
        }
        catch (Exception ex)
        {
            return (false, L.Get("Cloud_StatusReadFailed", ex.Message));
        }
    }

    // 有效远端根目录：两台机器核对 key_prefix 是否一致就看这里
    public string GetEffectiveRemoteRoot()
    {
        try
        {
            return GetCloudProvider() switch
            {
                "gdrive" => "Google Drive:/CloudRedirect/",
                "onedrive" => "OneDrive:/CloudRedirect/",
                "r2" => LoadR2Credentials() is { } c
                    ? $"r2://{c.Bucket}/{CloudProviderStore.NormalizePrefix(c.KeyPrefix)}" : "",
                "s3" => LoadS3Credentials() is { } c
                    ? $"{c.Endpoint.Trim().TrimEnd('/')}/{c.Bucket.Trim()}/{CloudProviderStore.NormalizePrefix(c.KeyPrefix)}" : "",
                "webdav" => LoadWebDavCredentials() is { } w
                    ? $"WebDAV: {CloudProviderStore.ApplyDefaultWebDavRoot(w.Url)}{CloudProviderStore.NormalizePrefix(w.KeyPrefix)}" : "",
                _ => "",
            };
        }
        catch
        {
            return "";
        }
    }

    public Task<CloudProviderStore.CloudProbeResult> TestCloudConnectionAsync(CancellationToken ct = default)
    {
        var provider = GetCloudProvider();
        if (provider == "folder")
            throw new InvalidOperationException(L.Get("Cloud_FolderModeNoTest"));
        return _store.ProbeAsync(provider, ct);
    }

    // 退出登录：OAuth 删 token 文件，R2/S3 删凭证文件；文件不存在视为未登录，直接报错
    public void SignOut(string provider)
    {
        var path = provider switch
        {
            "gdrive" or "onedrive" => GetTokenPath(provider),
            "r2" => Path.Combine(ConfigDir, "r2_credentials.json"),
            "s3" => Path.Combine(ConfigDir, "s3_credentials.json"),
            "webdav" => Path.Combine(ConfigDir, "webdav_credentials.json"),
            _ => throw new InvalidOperationException(L.Get("Cloud_FolderNoSignOut")),
        };
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException(L.Get("Cloud_NotSignedInSrc"));
        try
        {
            File.Delete(path);
            LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogSignedOutSrc", provider));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(L.Get("Cloud_SignOutFailed", ex.Message), ex);
        }
    }

    public string SaveR2Credentials(R2Credentials cred)
    {
        if (string.IsNullOrWhiteSpace(cred.AccountId) || string.IsNullOrWhiteSpace(cred.AccessKeyId) ||
            string.IsNullOrWhiteSpace(cred.SecretAccessKey) || string.IsNullOrWhiteSpace(cred.Bucket))
            throw new InvalidOperationException(L.Get("Cloud_R2Missing"));
        var obj = new JsonObject
        {
            ["account_id"] = cred.AccountId.Trim(),
            ["access_key_id"] = cred.AccessKeyId.Trim(),
            ["secret_access_key"] = cred.SecretAccessKey,
            ["bucket"] = cred.Bucket.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(cred.KeyPrefix)) obj["key_prefix"] = cred.KeyPrefix.Trim();
        if (!string.IsNullOrWhiteSpace(cred.Endpoint)) obj["endpoint"] = cred.Endpoint.Trim();
        var path = Path.Combine(ConfigDir, "r2_credentials.json");
        if (!CloudCredentialStore.WriteJson(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true })))
            throw new InvalidOperationException(L.Get("Cloud_R2WriteFailed"));
        SetCloudProvider("r2");
        LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_R2SavedSwitched"));
        return path;
    }

    public R2Credentials? LoadR2Credentials()
    {
        try
        {
            var json = CloudCredentialStore.ReadJson(Path.Combine(ConfigDir, "r2_credentials.json"));
            if (string.IsNullOrEmpty(json)) return null;
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string Get(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var account = Get("account_id");
            var key = Get("access_key_id");
            var bucket = Get("bucket");
            if (account.Length == 0 || key.Length == 0 || bucket.Length == 0) return null;
            return new R2Credentials(account, key, Get("secret_access_key"), bucket, Get("key_prefix"), Get("endpoint"));
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogReadR2Failed", ex.Message));
            return null;
        }
    }

    public string SaveS3Credentials(S3Credentials cred)
    {
        if (string.IsNullOrWhiteSpace(cred.AccessKeyId) || string.IsNullOrWhiteSpace(cred.SecretAccessKey) ||
            string.IsNullOrWhiteSpace(cred.Bucket) || string.IsNullOrWhiteSpace(cred.Endpoint) ||
            string.IsNullOrWhiteSpace(cred.Region))
            throw new InvalidOperationException(L.Get("Cloud_S3Missing"));
        var obj = new JsonObject
        {
            ["access_key_id"] = cred.AccessKeyId.Trim(),
            ["secret_access_key"] = cred.SecretAccessKey,
            ["bucket"] = cred.Bucket.Trim(),
            ["endpoint"] = cred.Endpoint.Trim(),
            ["region"] = cred.Region.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(cred.KeyPrefix)) obj["key_prefix"] = cred.KeyPrefix.Trim();
        if (cred.SignPayload) obj["sign_payload"] = true;
        if (cred.AllowInsecureHttp) obj["allow_insecure_http"] = true;
        if (cred.AllowInsecureTls) obj["allow_insecure_tls"] = true;
        if (!string.IsNullOrWhiteSpace(cred.CaCertPath)) obj["ca_cert_path"] = cred.CaCertPath.Trim();
        var path = Path.Combine(ConfigDir, "s3_credentials.json");
        if (!CloudCredentialStore.WriteJson(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true })))
            throw new InvalidOperationException(L.Get("Cloud_S3WriteFailed"));
        SetCloudProvider("s3");
        LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_S3SavedSwitched"));
        return path;
    }

    public S3Credentials? LoadS3Credentials()
    {
        try
        {
            var json = CloudCredentialStore.ReadJson(Path.Combine(ConfigDir, "s3_credentials.json"));
            if (string.IsNullOrEmpty(json)) return null;
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string Get(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool Flag(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
            var key = Get("access_key_id");
            var bucket = Get("bucket");
            var endpoint = Get("endpoint");
            var region = Get("region");
            if (key.Length == 0 || bucket.Length == 0 || endpoint.Length == 0 || region.Length == 0) return null;
            return new S3Credentials(key, Get("secret_access_key"), bucket, endpoint, region,
                Get("key_prefix"), Flag("sign_payload"), Flag("allow_insecure_http"),
                Flag("allow_insecure_tls"), Get("ca_cert_path"));
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogReadS3Failed", ex.Message));
            return null;
        }
    }

    // 额外请求头按行存储（一行一个 "Name: value"），落盘转数组，读取时还原成多行
    private static List<string> ParseWebDavHeaderLines(string raw)
    {
        var list = new List<string>();
        foreach (var line in (raw ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.IndexOf(':') <= 0)
                throw new InvalidOperationException(L.Get("Cloud_WebDavHeaderInvalid", trimmed));
            list.Add(trimmed);
        }
        return list;
    }

    public string SaveWebDavCredentials(WebDavCredentials cred)
    {
        if (string.IsNullOrWhiteSpace(cred.Url))
            throw new InvalidOperationException(L.Get("Cloud_WebDavUrlMissing"));
        if (!Uri.TryCreate(cred.Url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(L.Get("Cloud_WebDavUrlInvalid"));
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException(L.Get("Cloud_WebDavUrlInvalid"));
        // URL 里的 query/fragment 不参与 WebDAV 路径，落盘前去掉，避免 UI 显示/打开时错位
        var normalizedUrl = uri.GetLeftPart(UriPartial.Path);

        var auth = (cred.Auth ?? "").Trim().ToLowerInvariant();
        if (auth.Length == 0) auth = "basic";
        if (auth is not ("basic" or "auto" or "digest" or "ntlm" or "negotiate" or "bearer" or "none"))
            throw new InvalidOperationException(L.Get("Cloud_WebDavAuthInvalid", auth));
        if ((auth is "basic" or "auto" or "digest" or "ntlm" or "negotiate") &&
            string.IsNullOrWhiteSpace(cred.Username))
            throw new InvalidOperationException(L.Get("Cloud_WebDavUserMissing"));
        if (auth == "bearer" && string.IsNullOrWhiteSpace(cred.BearerToken))
            throw new InvalidOperationException(L.Get("Cloud_WebDavTokenMissing"));
        if (cred.TimeoutSec < 0 || cred.TimeoutSec > 300)
            throw new InvalidOperationException(L.Get("Cloud_WebDavTimeoutInvalid"));

        var obj = new JsonObject
        {
            ["url"] = normalizedUrl,
            ["username"] = cred.Username ?? "",
            ["password"] = cred.Password ?? "",
            ["auth"] = auth,
        };
        if (!string.IsNullOrWhiteSpace(cred.BearerToken)) obj["bearer_token"] = cred.BearerToken;
        if (!string.IsNullOrWhiteSpace(cred.KeyPrefix)) obj["key_prefix"] = cred.KeyPrefix.Trim();
        if (cred.AllowInsecureHttp) obj["allow_insecure_http"] = true;
        if (cred.AllowInsecureTls) obj["allow_insecure_tls"] = true;
        if (cred.TimeoutSec > 0) obj["timeout_sec"] = cred.TimeoutSec;
        var headers = ParseWebDavHeaderLines(cred.ExtraHeaders);
        if (headers.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var h in headers) arr.Add(h);
            obj["extra_headers"] = arr;
        }

        var path = Path.Combine(ConfigDir, "webdav_credentials.json");
        if (!CloudCredentialStore.WriteJson(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true })))
            throw new InvalidOperationException(L.Get("Cloud_WebDavWriteFailed"));
        SetCloudProvider("webdav");
        LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_WebDavSavedSwitched"));
        return path;
    }

    public WebDavCredentials? LoadWebDavCredentials()
    {
        try
        {
            var json = CloudCredentialStore.ReadJson(Path.Combine(ConfigDir, "webdav_credentials.json"));
            if (string.IsNullOrEmpty(json)) return null;
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string Get(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool Flag(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
            var url = Get("url");
            if (url.Length == 0) return null;
            var timeout = 0;
            if (r.TryGetProperty("timeout_sec", out var t))
            {
                if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var tn)) timeout = tn;
                else if (t.ValueKind == JsonValueKind.String && int.TryParse(t.GetString(), out var ts)) timeout = ts;
            }
            var headers = "";
            if (r.TryGetProperty("extra_headers", out var harr) && harr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var h in harr.EnumerateArray())
                    if (h.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(h.GetString()))
                        list.Add(h.GetString()!);
                headers = string.Join("\n", list);
            }
            var auth = Get("auth").Trim().ToLowerInvariant();
            if (auth.Length == 0) auth = "basic";
            if (auth is not ("basic" or "auto" or "digest" or "ntlm" or "negotiate" or "bearer" or "none"))
                auth = "basic";
            return new WebDavCredentials(url, Get("username"), Get("password"), auth,
                Get("bearer_token"), Get("key_prefix"), Flag("allow_insecure_http"),
                Flag("allow_insecure_tls"), timeout, headers);
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogReadWebDavFailed", ex.Message));
            return null;
        }
    }

    // 已重定向应用：本地源扫目录，云端源走远端列举；远端失败抛中文错，由调用方展示
    public async Task<List<RedirectedApp>> GetRedirectedAppsAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var provider = GetCloudProvider();
        if (provider != "folder")
            return await GetCloudRedirectedAppsAsync(provider, progress, ct);
        lock (_cloudLock) { _cloudEntries.Clear(); }
        var found = new Dictionary<int, RedirectedApp>();
        try
        {
            var syncPath = ResolveSyncPath();
            if (string.IsNullOrEmpty(syncPath) || !Directory.Exists(syncPath))
                return new List<RedirectedApp>();
            foreach (var accountDir in Directory.GetDirectories(syncPath))
            {
                if (!uint.TryParse(Path.GetFileName(accountDir), out _)) continue;
                foreach (var appDir in Directory.GetDirectories(accountDir))
                {
                    if (!int.TryParse(Path.GetFileName(appDir), out var appId) || appId == 0) continue;
                    found.TryAdd(appId, new RedirectedApp(appId, appDir, ReadCnTime(appDir)));
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogScanLocalFailed", ex.Message));
        }
        LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogLocalList", found.Count));
        return found.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    // 云端名单：远端目录即真相，本地无对应目录；条目缓存供打开控制台与删除定位
    private async Task<List<RedirectedApp>> GetCloudRedirectedAppsAsync(
        string provider, IProgress<string>? progress, CancellationToken ct)
    {
        List<CloudAppEntry> entries;
        try
        {
            entries = await _store.ListAppsAsync(provider, progress, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogReadCloudListFailed", ex.Message));
            throw new InvalidOperationException(L.Get("Cloud_ReadCloudListFailed", ex.Message), ex);
        }
        var found = new Dictionary<int, RedirectedApp>();
        lock (_cloudLock)
        {
            _cloudEntries.Clear();
            foreach (var e in entries)
            {
                if (!_cloudEntries.TryGetValue(e.AppId, out var list))
                    _cloudEntries[e.AppId] = list = new List<CloudAppEntry>();
                if (!list.Any(x => x.AccountId == e.AccountId))
                    list.Add(e);
                found.TryAdd(e.AppId, new RedirectedApp(
                    e.AppId, string.Empty, e.LastSaveTime, e.AccountId, e.RemoteId, e.WebUrl));
            }
        }
        LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogCloudList", provider, found.Count));
        return found.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    public IReadOnlyList<string> GetLastListFailures() => _store.GetLastListFailures();

    // 云端源下打开路径的目标地址：Drive 进目录页，OneDrive 用条目自带链接，R2 进面板，S3 拼桶浏览地址；
    // 多账号取最近有存档时间的那个，最相关
    public string GetCloudConsoleUrl(int appId)
    {
        List<CloudAppEntry>? list;
        lock (_cloudLock) { _cloudEntries.TryGetValue(appId, out list); list = list?.ToList(); }
        var entry = list?.OrderByDescending(e => e.LastSaveTime ?? DateTime.MinValue).FirstOrDefault();
        if (entry == null) return string.Empty;
        var provider = GetCloudProvider();
        return provider switch
        {
            "gdrive" => string.IsNullOrEmpty(entry.RemoteId)
                ? "https://drive.google.com/drive/search?q=CloudRedirect"
                : $"https://drive.google.com/drive/folders/{entry.RemoteId}",
            "onedrive" => entry.WebUrl ?? "https://onedrive.live.com/",
            "r2" => $"https://dash.cloudflare.com/{entry.AccountId}/r2/overview",
            "s3" => BuildS3BrowseUrl(entry),
            "webdav" => BuildWebDavBrowseUrl(entry),
            _ => string.Empty,
        };
    }

    // S3 无统一控制台：拼 endpoint + 桶 + 前缀的浏览器地址，能否打开取决于存储实现
    private string BuildS3BrowseUrl(CloudAppEntry entry)
    {
        try
        {
            var cred = LoadS3Credentials();
            if (cred == null || string.IsNullOrWhiteSpace(cred.Endpoint)) return string.Empty;
            var endpoint = cred.Endpoint.Trim().TrimEnd('/');
            if (!endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                endpoint = (cred.AllowInsecureHttp ? "http://" : "https://") + endpoint;
            var prefix = string.IsNullOrWhiteSpace(cred.KeyPrefix) ? "CloudRedirect/" : cred.KeyPrefix.Trim().Trim('/') + "/";
            return $"{endpoint}/{cred.Bucket.Trim()}/{prefix}{entry.AccountId}/{entry.AppId}/";
        }
        catch
        {
            return string.Empty;
        }
    }

    // WebDAV 没有统一控制台：直接拼出目标 app 目录的 URL，交给浏览器/资源管理器打开
    private string BuildWebDavBrowseUrl(CloudAppEntry entry)
    {
        try
        {
            var cred = LoadWebDavCredentials();
            if (cred == null || string.IsNullOrWhiteSpace(cred.Url)) return string.Empty;
            var root = CloudProviderStore.ApplyDefaultWebDavRoot(cred.Url);
            var prefix = CloudProviderStore.NormalizePrefix(cred.KeyPrefix);
            return $"{root}{prefix}{entry.AccountId}/{entry.AppId}/";
        }
        catch
        {
            return string.Empty;
        }
    }

    // 上次存档时间：appid 目录下 cn.cloudredirect 的修改时间；缺失返回空
    private static DateTime? ReadCnTime(string appDir)
    {
        try
        {
            var cn = Path.Combine(appDir, "cn.cloudredirect");
            return File.Exists(cn) ? File.GetLastWriteTime(cn) : null;
        }
        catch
        {
            return null;
        }
    }

    // 删除预览：收拢同一 appId 在所有账号下的三类本地目录并统计；云端源追加远端目标
    public async Task<DeletePreview> PreviewAppDeleteAsync(int appId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var steamPath = ResolveSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            throw new InvalidOperationException(L.Get("Cloud_NoSteamPath"));

        progress?.Report(L.Get("Cloud_ReadingLocalSaves"));
        var accountIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<DeleteTarget>();
        var syncPath = ResolveSyncPath();
        if (!string.IsNullOrEmpty(syncPath) && Directory.Exists(syncPath))
        {
            foreach (var accountDir in Directory.GetDirectories(syncPath))
            {
                var accountId = Path.GetFileName(accountDir);
                if (!uint.TryParse(accountId, out _)) continue;
                var appDir = Path.Combine(accountDir, appId.ToString());
                if (!Directory.Exists(appDir)) continue;
                accountIds.Add(accountId);
                targets.Add(CountTarget("sync", accountId, appDir));
            }
        }

        // DLL 缓存与旧版布局也要收拢，否则 heal 机制会把文件复活
        var cacheRoot = Path.Combine(steamPath, "cloud_redirect", "storage");
        var legacyBlobsRoot = Path.Combine(steamPath, "cloud_redirect", "blobs");
        foreach (var dir in new[] { cacheRoot, legacyBlobsRoot })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var accountDir in Directory.GetDirectories(dir))
            {
                var accountId = Path.GetFileName(accountDir);
                if (!uint.TryParse(accountId, out _)) continue;
                var appDir = Path.Combine(accountDir, appId.ToString());
                if (!Directory.Exists(appDir)) continue;
                accountIds.Add(accountId);
                targets.Add(CountTarget("cache", accountId, appDir));
            }
        }

        foreach (var accountId in accountIds)
        {
            var userdataDir = Path.Combine(steamPath, "userdata", accountId, appId.ToString());
            if (Directory.Exists(userdataDir))
                targets.Add(CountTarget("userdata", accountId, userdataDir));
        }

        // 云端源追加远端目标：本地缓存与 userdata 照常收拢，远端走在线统计；
        // 名单缓存缺失（未刷新直接删除）则现查远端，避免漏删
        var provider = GetCloudProvider();
        if (provider != "folder")
        {
            progress?.Report(L.Get("Cloud_ReadingCloudSaves"));
            List<CloudAppEntry>? cached;
            lock (_cloudLock) { _cloudEntries.TryGetValue(appId, out cached); cached = cached?.ToList(); }
            if (cached == null || cached.Count == 0)
            {
                List<CloudAppEntry> fresh;
                try
                {
                    fresh = await _store.ListAppsAsync(provider, null, ct);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(L.Get("Cloud_ReadCloudSavesFailed", ex.Message), ex);
                }
                lock (_cloudLock)
                {
                    if (!_cloudEntries.TryGetValue(appId, out cached))
                        _cloudEntries[appId] = cached = new List<CloudAppEntry>();
                    foreach (var e in fresh.Where(e => e.AppId == appId))
                    {
                        if (!cached.Any(x => x.AccountId == e.AccountId))
                            cached.Add(e);
                    }
                }
            }
            var statTasks = cached.Select(async e =>
            {
                CloudAppStats stats;
                try
                {
                    stats = await _store.GetAppStatsAsync(provider, e.AccountId, appId, ct);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(L.Get("Cloud_ReadCloudSavesFailed", ex.Message), ex);
                }
                return (Entry: e, Stats: stats);
            }).ToList();
            foreach (var (e, stats) in await Task.WhenAll(statTasks))
            {
                if (!accountIds.Contains(e.AccountId))
                {
                    accountIds.Add(e.AccountId);
                    var userdataDir = Path.Combine(steamPath, "userdata", e.AccountId, appId.ToString());
                    if (Directory.Exists(userdataDir))
                        targets.Add(CountTarget("userdata", e.AccountId, userdataDir));
                }
                targets.Add(new DeleteTarget("cloud", e.AccountId, stats.DisplayPath,
                    stats.FileCount, stats.TotalBytes, provider));
            }
        }

        // 兜底：只剩陈旧 userdata、同步目录与缓存都已不在的账号
        if (Directory.Exists(Path.Combine(steamPath, "userdata")))
        {
            foreach (var accountDir in Directory.GetDirectories(Path.Combine(steamPath, "userdata")))
            {
                var accountId = Path.GetFileName(accountDir);
                if (!uint.TryParse(accountId, out _)) continue;
                var userdataDir = Path.Combine(accountDir, appId.ToString());
                if (!Directory.Exists(userdataDir)) continue;
                if (targets.Any(t => t.Kind == "userdata" && t.Path.Equals(userdataDir, StringComparison.OrdinalIgnoreCase)))
                    continue;
                targets.Add(CountTarget("userdata", accountId, userdataDir));
            }
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var backupDir = Path.Combine(steamPath, "cloud_redirect", "app_tab_backup", $"{appId}_{stamp}");
        return new DeletePreview(appId, targets, backupDir);
    }

    private static DeleteTarget CountTarget(string kind, string accountId, string path)
    {
        int count = 0;
        long bytes = 0;
        try
        {
            // 流式枚举：大目录不物化全量数组
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    count++;
                }
                catch { }
            }
        }
        catch { }
        return new DeleteTarget(kind, accountId, path, count, bytes);
    }

    // 先完整备份并验数，通过后才删原件；中途失败直接抛错，原件不动；
    // 云端目标备份=下载到本地后验数，删除=调远端删除，备份恢复需手动上传回云端
    public Task DeleteAppSavesAsync(DeletePreview preview, IProgress<string>? status, CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            Directory.CreateDirectory(preview.BackupDir);
            var copied = new List<(DeleteTarget Target, string BackupPath)>();
            // 目标序号参与备份目录名：同账号 storage 与旧版 blobs 同为 cache，必须分目录，否则验数误报
            for (var i = 0; i < preview.Targets.Count; i++)
            {
                var t = preview.Targets[i];
                ct.ThrowIfCancellationRequested();
                status?.Report(L.Get("Cloud_BackingUp", KindLabel(t.Kind)));
                var dest = Path.Combine(preview.BackupDir, t.AccountId, $"{t.Kind}_{i}");
                if (t.Kind == "cloud")
                {
                    if (string.IsNullOrEmpty(t.Provider))
                        throw new InvalidOperationException(L.Get("Cloud_DeleteNoProvider"));
                    var fresh = await _store.GetAppStatsAsync(t.Provider, t.AccountId, preview.AppId, ct);
                    var downloaded = await _store.DownloadAppAsync(t.Provider, t.AccountId, preview.AppId, dest, status, ct);
                    if (downloaded != fresh.FileCount)
                        throw new InvalidOperationException(
                            L.Get("Cloud_BackupIncompleteRemote", KindLabel(t.Kind), fresh.FileCount, downloaded));
                    copied.Add((new DeleteTarget(t.Kind, t.AccountId, t.Path, downloaded, fresh.TotalBytes, t.Provider), dest));
                }
                else
                {
                    CopyDirectory(t.Path, dest, ct);
                    var backed = CountTarget(t.Kind, t.AccountId, dest);
                    if (backed.FileCount != t.FileCount)
                        throw new InvalidOperationException(L.Get("Cloud_BackupIncompleteCount", KindLabel(t.Kind), t.FileCount, backed.FileCount));
                    copied.Add((t, dest));
                }
            }

            var hasCloud = copied.Any(c => c.Target.Kind == "cloud");
            var info = new JsonObject
            {
                ["appId"] = preview.AppId,
                ["timestamp"] = DateTime.Now.ToString("o"),
                ["note"] = hasCloud
                    ? L.Get("Cloud_BackupNoteCloud")
                    : L.Get("Cloud_BackupNoteLocal"),
                ["targets"] = new JsonArray(copied.Select(c =>
                    new JsonObject
                    {
                        ["kind"] = c.Target.Kind,
                        ["accountId"] = c.Target.AccountId,
                        ["source"] = c.Target.Path,
                        ["backup"] = c.BackupPath,
                        ["files"] = c.Target.FileCount
                    }).ToArray())
            };
            File.WriteAllText(Path.Combine(preview.BackupDir, "backup_info.json"),
                info.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var errors = new List<string>();
            foreach (var (t, _) in copied)
            {
                ct.ThrowIfCancellationRequested();
                status?.Report(L.Get("Cloud_Deleting", KindLabel(t.Kind)));
                try
                {
                    if (t.Kind == "cloud")
                    {
                        var r = await _store.DeleteAppAsync(t.Provider!, t.AccountId, preview.AppId, status, ct);
                        if (!string.IsNullOrEmpty(r.Error))
                            errors.Add(L.Get("Cloud_LabelErrorGeneric", KindLabel(t.Kind), r.Error));
                    }
                    else if (Directory.Exists(t.Path))
                    {
                        Directory.Delete(t.Path, true);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(L.Get("Cloud_LabelErrorGeneric", KindLabel(t.Kind), ex.Message));
                }
            }
            if (errors.Count > 0)
                throw new InvalidOperationException(L.Get("Cloud_DeletePartialFailed", string.Join("\n", errors), preview.BackupDir));
        }, ct);
    }

    private static string KindLabel(string kind) => kind switch
    {
        "sync" => L.Get("Cloud_KindSync"),
        "cache" => L.Get("Cloud_KindCache"),
        "userdata" => L.Get("Cloud_KindUserdata"),
        "cloud" => L.Get("Cloud_KindCloud"),
        _ => kind
    };

    // 单遍遍历：目录项建目录（含空目录），文件项确保父目录后拷贝；
    // 与原来两遍结果一致（含空目录保留），中途失败同样上抛由调用方中止
    private static void CopyDirectory(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(dest, Path.GetRelativePath(source, entry));
            if (Directory.Exists(entry))
                Directory.CreateDirectory(target);
            else if (File.Exists(entry))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(entry, target, overwrite: true);
            }
        }
    }
}
