using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;

namespace SteamLuaManager.Services;

public sealed record StagedAppUpdate(string StagingDir, string TargetDir, string ExeName, string Version, bool IsLoose);

public interface IAppUpdateService
{
    Task<StagedAppUpdate> DownloadAndStageAsync(UpdateCheckResult check, IProgress<string>? status, IProgress<int>? progress, CancellationToken ct = default);
    bool TryStartUpdater(StagedAppUpdate staged);
    void CleanupLeftoverUpdateFiles();
}

public class AppUpdateService : IAppUpdateService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISettingsService _settingsService;

    // 单下载源总预算：多源串行，慢源不能无限拖；用户取消走 ct 立刻中断
    private static readonly TimeSpan PerSourceBudget = TimeSpan.FromSeconds(300);

    public AppUpdateService(IHttpClientProvider httpClientProvider, ISettingsService settingsService)
    {
        _httpClientProvider = httpClientProvider;
        _settingsService = settingsService;
    }

    public static string InstallDir => AppDomain.CurrentDomain.BaseDirectory;

    public static string ExeName => Path.GetFileName(Environment.ProcessPath ?? "SteamLuaManager.exe");

    // 散文件版安装目录旁必有主 dll；单文件版（新旧命名）都没有
    public static bool IsLooseInstall() =>
        File.Exists(Path.Combine(InstallDir, "SteamLuaManager.dll"));

    public async Task<StagedAppUpdate> DownloadAndStageAsync(
        UpdateCheckResult check, IProgress<string>? status, IProgress<int>? progress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var isLoose = IsLooseInstall();
        var exeName = ExeName;
        var url = isLoose ? check.LooseAssetUrl : check.SingleAssetUrl;
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("新版本未提供当前版本的安装包，请前往 GitHub 手动下载更新");

        // 安装目录不可写时直接报错，免得下完包才发现装不上
        var probePath = Path.Combine(InstallDir, ".write-test");
        try
        {
            using var probe = File.Create(probePath);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new InvalidOperationException("安装目录不可写，请以管理员身份运行或前往 GitHub 手动下载更新", ex);
        }
        finally
        {
            try { File.Delete(probePath); } catch { }
        }

        // 下载源：用户在设置里选了镜像则该镜像首位、直连垫底
        var sources = GitHubMirror.WithAssetMirrors(url, _settingsService.Load().ManifestMirror);
        Exception? lastError = null;
        string? downloadedZip = null;
        for (var i = 0; i < sources.Count; i++)
        {
            var src = sources[i];
            status?.Report(i == 0
                ? $"正在通过 {GitHubMirror.SourceDisplayName(src)} 下载新版本..."
                : $"经 {GitHubMirror.SourceDisplayName(sources[i - 1])} 下载失败，正在尝试 {GitHubMirror.SourceDisplayName(src)}…");

            var tempZip = Path.Combine(Path.GetTempPath(), $"FluentSteamLuaUpdate_{Guid.NewGuid():N}.zip");
            try
            {
                using (var response = await _httpClientProvider.SendWithProxyRetryAsync(
                           "app-update",
                           TimeSpan.FromSeconds(120),
                           client => client.GetAsync(src, HttpCompletionOption.ResponseHeadersRead),
                           HttpHeaderHelper.ConfigureApp).WaitAsync(PerSourceBudget, ct))
                {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? -1;

                    await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = File.Create(tempZip);

                    var buffer = new byte[81920];
                    long readBytes = 0;
                    int bytesRead;
                    while ((bytesRead = await httpStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                        readBytes += bytesRead;
                        if (totalBytes > 0 && progress != null)
                        {
                            var percent = (int)(readBytes * 100 / totalBytes);
                            progress.Report(Math.Clamp(percent, 0, 100));
                        }
                    }
                }

                downloadedZip = tempZip;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                try { File.Delete(tempZip); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                LogService.Warn("更新", $"下载源失败 ({src}): {ex.Message}");
                try { File.Delete(tempZip); } catch { }
            }
        }

        if (downloadedZip == null)
            throw new InvalidOperationException($"新版本下载失败，已尝试 {sources.Count} 个下载源：{lastError?.Message}", lastError);

        try
        {
            ct.ThrowIfCancellationRequested();
            status?.Report("正在解压更新包...");

            using (var archive = ZipFile.OpenRead(downloadedZip))
            {
                var hasExe = archive.Entries.Any(e =>
                    string.Equals(Path.GetFileName(e.FullName), exeName, StringComparison.OrdinalIgnoreCase));
                if (!hasExe)
                    throw new InvalidOperationException("更新包中未找到主程序文件，请前往 GitHub 手动下载更新");
            }

            var stagingDir = Path.Combine(InstallDir, "update-staging");
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);
            using (var archive = ZipFile.OpenRead(downloadedZip))
            {
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var dest = Path.GetFullPath(Path.Combine(stagingDir, entry.FullName));
                    if (!dest.StartsWith(Path.GetFullPath(stagingDir) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("更新包内路径非法，已终止更新");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            // 兼容带顶层目录的老包体布局：根下仅有一个子目录时下沉一层
            var rootEntries = Directory.GetFileSystemEntries(stagingDir);
            if (rootEntries.Length == 1 && Directory.Exists(rootEntries[0]))
                stagingDir = rootEntries[0];

            status?.Report("更新包就绪，正在启动更新程序...");
            return new StagedAppUpdate(stagingDir, InstallDir, exeName, check.LatestVersion.ToString(), isLoose);
        }
        finally
        {
            try { File.Delete(downloadedZip); } catch { }
        }
    }

    public bool TryStartUpdater(StagedAppUpdate staged)
    {
        try
        {
            // 每次释放全新的 Updater，与主程序版本同步
            var updaterDir = Path.Combine(Path.GetTempPath(), "FluentSteamLuaUpdater");
            if (Directory.Exists(updaterDir))
                Directory.Delete(updaterDir, recursive: true);
            Directory.CreateDirectory(updaterDir);

            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("SteamLuaManager.Resources.Updater.zip");
            if (stream == null)
            {
                LogService.Warn("更新", "内嵌更新程序缺失，无法自更新");
                return false;
            }
            using var zip = new ZipArchive(stream);
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var dest = Path.GetFullPath(Path.Combine(updaterDir, entry.FullName));
                if (!dest.StartsWith(Path.GetFullPath(updaterDir) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }

            var updaterExe = Path.Combine(updaterDir, "Updater.exe");
            if (!File.Exists(updaterExe))
            {
                LogService.Warn("更新", "更新程序释放失败，无法自更新");
                return false;
            }

            // 逐个传参，由运行时负责转义；手拼命令行会在路径末尾反斜杠处损坏引号配对
            var psi = new ProcessStartInfo
            {
                FileName = updaterExe,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("--staging");
            psi.ArgumentList.Add(staged.StagingDir);
            psi.ArgumentList.Add("--target");
            psi.ArgumentList.Add(staged.TargetDir);
            psi.ArgumentList.Add("--exe");
            psi.ArgumentList.Add(staged.ExeName);
            psi.ArgumentList.Add("--version");
            psi.ArgumentList.Add(staged.Version);
            Process.Start(psi);
            LogService.Info("更新", $"已启动更新程序，目标版本 {staged.Version}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn("更新", $"启动更新程序失败: {ex.Message}");
            return false;
        }
    }

    public void CleanupLeftoverUpdateFiles()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(InstallDir, "backup-*"))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
            foreach (var dir in Directory.GetDirectories(InstallDir, "update-staging*"))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), "FluentSteamLuaUpdate_*.zip"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}
