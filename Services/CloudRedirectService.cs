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

public sealed record RedirectedApp(int AppId, string SaveDir, DateTime? LastSaveTime);

public sealed record MigrateResult(int MovedFiles, long MovedBytes, List<string> FailedFiles);

// 删除目标种类：sync=重定向存档目录，cache=DLL 本地缓存，userdata=Steam 用户数据
public sealed record DeleteTarget(string Kind, string AccountId, string Path, int FileCount, long TotalBytes);

public sealed record DeletePreview(int AppId, List<DeleteTarget> Targets, string BackupDir);

public interface ICloudRedirectService
{
    Task<CloudSaveStatus> RefreshStatusAsync(CancellationToken ct = default);
    Task EnableAsync(string? syncPath, IProgress<string>? status, CancellationToken ct = default);
    Task DisableAsync();
    Task SetSyncPathAsync(string path);
    List<RedirectedApp> GetRedirectedApps();
    Task<MigrateResult> MigrateSavesAsync(string oldPath, string newPath, IProgress<string>? status, CancellationToken ct = default);
    string GetDefaultSyncPath();
    DeletePreview PreviewAppDelete(int appId);
    Task DeleteAppSavesAsync(DeletePreview preview, IProgress<string>? status, CancellationToken ct = default);
}

public class CloudRedirectService : ICloudRedirectService
{
    private const string DllFileName = "cloud_redirect.dll";
    private const string EmbeddedResourceName = "SteamLuaManager.Resources.CloudRedirectDll.zip";

    private readonly ISteamPathService _steamPathService;
    private readonly object _embedLock = new();
    private byte[]? _embeddedDll;

    public CloudRedirectService(ISteamPathService steamPathService)
    {
        _steamPathService = steamPathService;
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
            LogService.Warn("云存档", $"读取重定向目录失败: {ex.Message}");
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
            throw new InvalidOperationException("未检测到 Steam 路径，无法启用云存档");
        if (string.IsNullOrWhiteSpace(syncPath))
            syncPath = DefaultSyncPath(steamPath);

        status?.Report("正在准备本地目录...");
        try
        {
            Directory.CreateDirectory(syncPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"本地目录不可用：{ex.Message}", ex);
        }

        status?.Report("正在部署云存档 DLL...");
        EnsureDllDeployed(steamPath);

        status?.Report("正在写入内核开关...");
        if (!_steamPathService.SetCloudEnabled(true))
            throw new InvalidOperationException("写入 opensteamtool.toml 失败，请检查文件权限");

        status?.Report("正在写入重定向配置...");
        WriteRedirectConfig(syncPath);

        LogService.Info("云存档", $"云存档已启用（{syncPath}）");
        return Task.CompletedTask;
    }

    public Task DisableAsync()
    {
        var steamPath = ResolveSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            throw new InvalidOperationException("未检测到 Steam 路径");
        // DLL 与配置文件保留，仅关闭开关，下次启用无需重新部署
        if (!_steamPathService.SetCloudEnabled(false))
            throw new InvalidOperationException("写入 opensteamtool.toml 失败，请检查文件权限");
        LogService.Info("云存档", "云存档已关闭");
        return Task.CompletedTask;
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
            throw new InvalidOperationException($"读取旧目录失败：{ex.Message}", ex);
        }
        if (allFiles.Count == 0)
            return new MigrateResult(0, 0, failed);

        // 新旧目录嵌套会自我复制，提前拦截
        var oldRoot = Path.GetFullPath(oldPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var newRoot = Path.GetFullPath(newPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (newRoot.StartsWith(oldRoot, StringComparison.OrdinalIgnoreCase)
            || oldRoot.StartsWith(newRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("新旧目录存在嵌套关系，无法迁移");

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
                status?.Report($"正在迁移旧存档... ({done}/{allFiles.Count})");
        }

        if (failed.Count == 0)
        {
            try { Directory.Delete(oldPath, recursive: true); }
            catch (Exception ex)
            {
                // 删源失败不算迁移失败：文件已就位，残留由用户手动清理
                LogService.Warn("云存档", $"旧目录清理失败，已保留：{ex.Message}");
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
            throw new InvalidOperationException("请选择本地重定向目录");
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"本地目录不可用：{ex.Message}", ex);
        }
        WriteRedirectConfig(path);
        LogService.Info("云存档", $"本地重定向目录已切换为 {path}");
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
                    LogService.Warn("云存档", "内嵌云存档 DLL 缺失");
                    return null;
                }
                using var zip = new ZipArchive(stream);
                var entry = zip.Entries.FirstOrDefault(e =>
                    string.Equals(Path.GetFileName(e.FullName), DllFileName, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    LogService.Warn("云存档", "内嵌包中未找到云存档 DLL");
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
                LogService.Warn("云存档", $"读取内嵌云存档 DLL 失败: {ex.Message}");
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
            throw new InvalidOperationException("内嵌云存档 DLL 缺失，请重新安装本软件");

        var target = ResolveLibraryTarget(steamPath);
        if (File.Exists(target))
        {
            try
            {
                using var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(embedded), SHA256.HashData(ms.ToArray())))
                    return;
            }
            catch { }
            // 目标被 Steam 占用时覆盖必失败，先探后写
            try
            {
                using var probe = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"无法写入 {DllFileName}，文件正被占用，请关闭 Steam 后重试", ex);
            }
        }

        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = target + ".new";
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
            throw new InvalidOperationException($"部署云存档 DLL 失败：{ex.Message}，请关闭 Steam 后重试", ex);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        LogService.Info("云存档", $"云存档 DLL 已部署到 {target}");
    }

    private void WriteRedirectConfig(string syncPath)
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

            // 仅写自有键，未知键原样保留；成就与时长跟随云端同步；DLL 自更新关闭以免覆盖已部署版本
            root["provider"] = "folder";
            root["sync_path"] = syncPath;
            root["sync_achievements"] = true;
            root["sync_playtime"] = true;
            root["auto_update_dll"] = false;

            var tmp = configPath + ".new";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, configPath, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"写入重定向配置失败：{ex.Message}", ex);
        }
    }

    // 已重定向应用：文件夹模式落盘布局为 <sync_path>\<accountId>\<appid>；
    // 账号目录只认数字，appid 为 0 的是账号级元数据目录，跳过；同 app 取首个命中的目录
    public List<RedirectedApp> GetRedirectedApps()
    {
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
            LogService.Warn("云存档", $"扫描已重定向游戏失败: {ex.Message}");
        }
        return found.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
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

    // 删除预览：收拢同一 appId 在所有账号下的三类目录并统计；只收录存在的目录
    public DeletePreview PreviewAppDelete(int appId)
    {
        var steamPath = ResolveSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            throw new InvalidOperationException("未检测到 Steam 路径");

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
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
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

    // 先完整备份并验数，通过后才删原件；中途失败直接抛错，原件不动
    public Task DeleteAppSavesAsync(DeletePreview preview, IProgress<string>? status, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            Directory.CreateDirectory(preview.BackupDir);
            var copied = new List<(DeleteTarget Target, string BackupPath)>();
            // 目标序号参与备份目录名：同账号 storage 与旧版 blobs 同为 cache，必须分目录，否则验数误报
            for (var i = 0; i < preview.Targets.Count; i++)
            {
                var t = preview.Targets[i];
                ct.ThrowIfCancellationRequested();
                status?.Report($"正在备份 {KindLabel(t.Kind)}…");
                var dest = Path.Combine(preview.BackupDir, t.AccountId, $"{t.Kind}_{i}");
                CopyDirectory(t.Path, dest, ct);
                var backed = CountTarget(t.Kind, t.AccountId, dest);
                if (backed.FileCount != t.FileCount)
                    throw new InvalidOperationException($"备份不完整（{KindLabel(t.Kind)}：应备 {t.FileCount} 个，实备 {backed.FileCount} 个），已中止删除，原件未动");
                copied.Add((t, dest));
            }

            var info = new JsonObject
            {
                ["appId"] = preview.AppId,
                ["timestamp"] = DateTime.Now.ToString("o"),
                ["note"] = "删除存档前自动备份；恢复需手动拷回对应目录，暂无一键恢复",
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
                status?.Report($"正在删除 {KindLabel(t.Kind)}…");
                try
                {
                    if (Directory.Exists(t.Path))
                        Directory.Delete(t.Path, true);
                }
                catch (Exception ex)
                {
                    errors.Add($"{KindLabel(t.Kind)}：{ex.Message}");
                }
            }
            if (errors.Count > 0)
                throw new InvalidOperationException($"部分删除失败：\n{string.Join("\n", errors)}\n备份位于 {preview.BackupDir}");
        }, ct);
    }

    private static string KindLabel(string kind) => kind switch
    {
        "sync" => "重定向存档",
        "cache" => "DLL 本地缓存",
        "userdata" => "Steam 用户数据",
        _ => kind
    };

    private static void CopyDirectory(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}
