using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public interface IOpenSteamToolService
{
    bool IsInstalled { get; }
    bool HasDisabledKernel { get; }
    string? GetSteamPath();
    Task<string?> GetLocalVersionAsync();
    Task<(string version, string downloadUrl, string releaseUrl)> GetRemoteInfoAsync();
    Task InstallAsync(string downloadUrl, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default);
    Task UninstallAsync();
    Task<int> DisableKernelAsync();
    Task<int> EnableKernelAsync();
    Task<IReadOnlyList<ForeignKernelFile>> ScanForeignKernelsAsync();
    Task<(int removed, List<string> failed)> PurifyForeignKernelsAsync(IEnumerable<string> relativePaths);
    Task<SteamDllTomlInfo?> DetectSteamDllInfoAsync();
    Task<IReadOnlyList<TomlFetchResult>> FetchTomlFilesAsync(SteamDllTomlInfo info, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default);
}

// 当前安装 Steam 的两个 DLL 指纹：OST 解析签名文件的唯一依据（与内核 RemoteToml 同规则）
public sealed record SteamDllTomlInfo(string ClientSha, string UiSha);

// 单个 toml 的下载结果：文件名 + 落盘路径 + 成败 + 是否上游 404（404=版本不受支持，
// 文案让用户更新正式版；其他失败=网络问题，文案让查网络，两类 advice 不能混）
public sealed record TomlFetchResult(string FileName, string TargetPath, bool Success, string? Error, bool NotFound = false);

// 扫描到的第三方内核文件：相对路径 + 所属内核标注（确认框逐字展示用）
public sealed record ForeignKernelFile(string RelativePath, string Owner);

public class OpenSteamToolService : IOpenSteamToolService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;
    private readonly ISettingsService _settingsService;
    // 我自己针对本软件优化适配的 fork 仓库，不再跟随官方主仓库（功能欠缺）
    private const string GitHubOwner = "huanyuejue";
    private const string GitHubRepo = "OpenSteamTool";
    private static string GitHubLatestUrl => $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    private static readonly string[] RequiredDlls = ["version.dll", "xinput1_4.dll", "OpenSteamTool.dll"];
    // 退役劫持 DLL（不再安装）：只在卸载/安装/禁用时顺手清理，有则删、无则过；启用时故意不恢复
    private static readonly string[] LegacyDlls = ["dwmapi.dll"];
    private static readonly Dictionary<string, string> EmbeddedVersionMap = new()
    {
        ["115ec256c7c5b066926015a24120cf6e7d9e5a7a5b87441817c2de11cc3f9fec"] = "v1.2.0",
        ["494bc762351b4dc80ca2f36cc005fc89b976f24e6e77c12945229e3e05502e93"] = "v1.3.0",
        ["8d4cb44bc57565e8183b9dab72eda873305c4257e080e29d57bbfda4cc755585"] = "v1.3.1",
        ["6daeef8b0a085c22ca43a6efeceee1f8547c3044573c394ec7cd4945fba13430"] = "v1.3.2",
        ["a1c4ffc819d96d9c397d132cb718aa7d7d44651375845e4bb9258499e643857d"] = "v1.4.0",
        ["550f9edfede4a4403f7aefdd5c4a40fdd92be22135443857fd997b415d7ced1e"] = "v1.4.1",
        ["962f5c7700a0ddde46cd419763ed15f95baf5a4a93525559f7bb6453aa1b1aac"] = "v1.4.2",
        ["d578da0170d18cd8f7cdee36a617a80147bddc8945701e3d5d1f11315d7e36fd"] = "v1.4.3",
        ["9113dce46b7a807e30abc018ee8469f188c51e2d277279c2f15427efc2f52226"] = "v1.4.4",
        ["5ec8351d5949c10c97210759efb5d618741d8414d67ff650e328d036e352c10c"] = "v1.4.5",
        ["cd7266e06d7416d3b02335386c54b909df68e2e0605941f70bb21ed392ee639f"] = "v1.4.6",
        ["9a2c459ad5124eeb48e4a1c7ac9808e5fad6eda54f7df8ad4b2e4465818f50f7"] = "v1.4.6-fix",
        ["09d26118c7cf796cf37562c4cb965d1b213f5866c02ca04fe0fafc4c2f22b0bc"] = "v1.4.7",
    };
    private static readonly byte[] VersionMarker = [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00];

    public OpenSteamToolService(ISteamPathService steamPathService, IHttpClientProvider httpClientProvider, ISettingsService settingsService)
    {
        _steamPathService = steamPathService;
        _httpClientProvider = httpClientProvider;
        _settingsService = settingsService;
    }



    public bool IsInstalled => _steamPathService.DetectSteamToolType() == SteamToolType.OpenSteamTool;

    // 任一内核 DLL 存在 .disabled 后缀即视为有被临时禁用的内核
    public bool HasDisabledKernel
    {
        get
        {
            try
            {
                var steamPath = GetSteamPath();
                if (string.IsNullOrEmpty(steamPath)) return false;
                return RequiredDlls.Any(dll => File.Exists(Path.Combine(steamPath, dll + ".disabled")));
            }
            catch
            {
                return false;
            }
        }
    }

    public string? GetSteamPath()
    {
        var path = _steamPathService.DetectSteamPath();
        return !string.IsNullOrEmpty(path) ? path : null;
    }

    public Task<string?> GetLocalVersionAsync()
    {
        var steamPath = GetSteamPath();
        if (steamPath == null) return Task.FromResult<string?>(null);
        var dllPath = Path.Combine(steamPath, "OpenSteamTool.dll");
        if (!File.Exists(dllPath)) return Task.FromResult<string?>(null);

        // 1. 嵌入 SHA256 字典（覆盖 pre-1.4.8 所有官方构建）
        var localHash = ComputeSha256(dllPath);
        if (EmbeddedVersionMap.TryGetValue(localHash, out var embeddedVer))
            return Task.FromResult<string?>(embeddedVer);

        // 2. 二进制标记位解析（覆盖 1.4.8+ 版本）
        try
        {
            var bytes = File.ReadAllBytes(dllPath);
            for (int i = 0; i <= bytes.Length - 12; i++)
            {
                var found = true;
                for (int j = 0; j < VersionMarker.Length; j++)
                {
                    if (bytes[i + j] != VersionMarker[j]) { found = false; break; }
                }
                if (!found) continue;

                var start = i + VersionMarker.Length;
                var end = start;
                while (end < bytes.Length && bytes[end] != 0) end++;
                if (end > start)
                {
                    var ver = Encoding.ASCII.GetString(bytes, start, end - start);
                    if (Regex.IsMatch(ver, @"^v?\d+\.\d+\.\d+"))
                        return Task.FromResult<string?>(ver);
                }
            }
        }
        catch (Exception ex) { LogService.Warn("内核", $"读取本地版本失败: {ex.Message}"); }

        return Task.FromResult<string?>(null);
    }

    public async Task<(string version, string downloadUrl, string releaseUrl)> GetRemoteInfoAsync()
    {
        var json = await _httpClientProvider.SendWithProxyRetryAsync(
            "open-steam-tool",
            TimeSpan.FromSeconds(120),
            client => client.GetStringAsync(GitHubLatestUrl),
            HttpHeaderHelper.ConfigureApp);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "0.0.0";
        var releaseUrl = doc.RootElement.TryGetProperty("html_url", out var htmlUrl)
            ? htmlUrl.GetString() ?? ""
            : $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/tag/{tag}";
        var downloadUrl = "";
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("-Release") && name.EndsWith(".zip"))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    break;
                }
            }
        }
        return (tag, downloadUrl, releaseUrl);
    }

    // 单下载源总预算：多源串行，黑洞源不能无限拖；用户取消走 ct 立刻中断
    private static readonly TimeSpan PerSourceBudget = TimeSpan.FromSeconds(300);

    public async Task InstallAsync(string downloadUrl, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");

        // 目标文件被 Steam 占用时覆盖必失败，先探后下，免得白下几个包才报错
        foreach (var dll in RequiredDlls)
        {
            var targetPath = Path.Combine(steamPath, dll);
            if (!File.Exists(targetPath)) continue;
            try
            {
                using var probe = new FileStream(targetPath, FileMode.Open, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"无法写入 {dll}，文件正被占用，请确保 Steam 已关闭后再试", ex);
            }
        }

        // 下载源：默认直连优先，用户在设置里选了镜像则该镜像首位、直连垫底
        // 只有下载阶段允许换源重试；下载成功后的解压安装只做一次，失败直接报原因
        var sources = GitHubMirror.WithAssetMirrors(downloadUrl, _settingsService.Load().ManifestMirror);
        Exception? lastError = null;
        string? downloadedZip = null;
        for (var i = 0; i < sources.Count; i++)
        {
            var src = sources[i];
            status?.Report(i == 0
                ? $"正在通过 {GitHubMirror.SourceDisplayName(src)} 下载 OpenSteamTool..."
                : $"经 {GitHubMirror.SourceDisplayName(sources[i - 1])} 下载失败，正在尝试 {GitHubMirror.SourceDisplayName(src)}…");

            var tempZip = Path.Combine(Path.GetTempPath(), $"OpenSteamTool_{Guid.NewGuid():N}.zip");
            try
            {
                using (var response = await _httpClientProvider.SendWithProxyRetryAsync(
                           "open-steam-tool",
                           TimeSpan.FromSeconds(120),
                           client => client.GetAsync(src, HttpCompletionOption.ResponseHeadersRead),
                           HttpHeaderHelper.ConfigureApp).WaitAsync(PerSourceBudget, ct))
                {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? -1;

                    await using var httpStream = await response.Content.ReadAsStreamAsync();
                    await using var fileStream = File.Create(tempZip);

                    var buffer = new byte[81920];
                    long readBytes = 0;
                    int bytesRead;
                    while ((bytesRead = await httpStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                        readBytes += bytesRead;
                        if (totalBytes > 0 && downloadProgress != null)
                        {
                            var percent = (int)(readBytes * 100 / totalBytes);
                            downloadProgress.Report(Math.Clamp(percent, 0, 100));
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
                LogService.Warn("内核", $"下载源失败 ({src}): {ex.Message}");
                try { File.Delete(tempZip); } catch { }
            }
        }

        if (downloadedZip == null)
            throw new InvalidOperationException($"OpenSteamTool 下载失败，已尝试 {sources.Count} 个下载源：{lastError?.Message}", lastError);

        try
        {
            ct.ThrowIfCancellationRequested();
            status?.Report("正在解压并安装 DLL...");
            using var archive = ZipFile.OpenRead(downloadedZip);
            var extracted = 0;
            var extractedVersion = false;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(entry.Name);
                if (string.IsNullOrEmpty(fileName)) continue;
                if (!RequiredDlls.Contains(fileName, StringComparer.OrdinalIgnoreCase)) continue;
                if (fileName.Equals("version.dll", StringComparison.OrdinalIgnoreCase)) extractedVersion = true;

                var targetPath = Path.Combine(steamPath, fileName);
                try
                {
                    entry.ExtractToFile(targetPath, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"无法写入 {fileName}，文件正被占用，请确保 Steam 已关闭后再试", ex);
                }
                extracted++;
            }

            if (extracted == 0)
                throw new InvalidOperationException("压缩包中未找到 OpenSteamTool DLL 文件");
            if (!extractedVersion)
                LogService.Warn("内核", "压缩包中缺少 version.dll，早注隔离不可用，请更新到新版发行包");

            // 退役残留：旧部署的根目录 dwmapi.dll 会让新版 Steam 启动失败，安装成功即顺手清除（有则删，无则过）
            foreach (var dll in LegacyDlls)
            {
                var legacyPath = Path.Combine(steamPath, dll);
                if (!File.Exists(legacyPath)) continue;
                try
                {
                    File.Delete(legacyPath);
                    status?.Report($"已清除退役残留 {dll}");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"无法清除退役残留 {dll}，文件正被占用，请确保 Steam 已关闭后再试", ex);
                }
            }

            status?.Report("安装完成");
        }
        finally
        {
            try { File.Delete(downloadedZip); } catch { }
        }
    }

    public Task UninstallAsync()
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        var removed = 0;
        foreach (var dll in RequiredDlls)
        {
            var path = Path.Combine(steamPath, dll);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    throw new InvalidOperationException($"无法删除 {dll}，文件正被占用，请确保 Steam 已关闭后再试", ex);
                }
            }
        }
        // 退役残留：根目录与 bin 目录的 dwmapi.dll，有则删、无则过（bin 里的劫持同样会炸新版 Steam）
        foreach (var dll in LegacyDlls)
        {
            foreach (var rel in new[] { dll, Path.Combine("bin", dll) })
            {
                var path = Path.Combine(steamPath, rel);
                if (!File.Exists(path)) continue;
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    throw new InvalidOperationException($"无法删除 {rel}，文件正被占用，请确保 Steam 已关闭后再试", ex);
                }
            }
        }
        if (removed == 0)
            throw new InvalidOperationException("未检测到已安装的 OpenSteamTool 文件");
        return Task.CompletedTask;
    }

    // 临时禁用：逐个给已存在的内核 DLL 加 .disabled 后缀，返回实际处理数；
    // 已是禁用态或目标后缀已存在的跳过，调用方负责 Steam 关闭门禁
    public Task<int> DisableKernelAsync()
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        var done = 0;
        var skipped = new List<string>();
        // 退役残留一并禁用：否则“禁用内核”后它仍会被加载，新版 Steam 照样起不来；
        // 它是退役文件，Enable 时故意不恢复（见 EnableKernelAsync）
        foreach (var dll in RequiredDlls.Concat(LegacyDlls))
        {
            var path = Path.Combine(steamPath, dll);
            var disabledPath = path + ".disabled";
            if (!File.Exists(path)) continue;
            if (File.Exists(disabledPath))
            {
                skipped.Add(dll);
                continue;
            }
            try
            {
                File.Move(path, disabledPath);
                done++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new InvalidOperationException($"无法禁用 {dll}，文件正被占用，请确保 Steam 已关闭后再试", ex);
            }
        }
        if (done == 0 && skipped.Count == 0)
            throw new InvalidOperationException("未检测到已安装的 OpenSteamTool 文件");
        if (skipped.Count > 0)
            LogService.Warn("内核", $"以下文件已处于禁用态，已跳过：{string.Join(", ", skipped)}");
        LogService.Info("内核", $"已临时禁用 {done} 个内核文件");
        return Task.FromResult(done);
    }

    // 启用：去掉所有已存在的 .disabled 后缀，返回实际处理数；目标原名已存在的跳过，避免覆盖
    public Task<int> EnableKernelAsync()
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        var done = 0;
        var skipped = new List<string>();
        // 退役文件故意不在此恢复：dwmapi.dll.disabled 留在原地即可（加载器不理 .disabled 后缀）
        foreach (var dll in RequiredDlls)
        {
            var path = Path.Combine(steamPath, dll);
            var disabledPath = path + ".disabled";
            if (!File.Exists(disabledPath)) continue;
            if (File.Exists(path))
            {
                skipped.Add(dll);
                continue;
            }
            try
            {
                File.Move(disabledPath, path);
                done++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new InvalidOperationException($"无法启用 {dll}：{ex.Message}", ex);
            }
        }
        if (done == 0 && skipped.Count == 0)
            throw new InvalidOperationException("未检测到被临时禁用的内核文件");
        if (skipped.Count > 0)
            LogService.Warn("内核", $"以下文件原名已存在，保留禁用副本未覆盖：{string.Join(", ", skipped)}");
        LogService.Info("内核", $"已启用 {done} 个内核文件");
        return Task.FromResult(done);
    }

    // 第三方内核指纹（相对 Steam 根目录）：只收文件，不收目录；
    // stplug-in 只是 ST 存 lua 的目录、不影响注入，不在其中
    private static readonly (string File, string Owner)[] ForeignKernelFingerprints =
    [
        ("hid.dll", "SteamTools"),
        ("steam.cfg", "SteamTools"),
        ("zlib1.dll", "SteamTools"),
        ("User32.dll", "GreenLuma"),
    ];

    // 扫描第三方内核残留：精确名直查 + greenluma*.dll 通配（版号嵌文件名）；
    // 只看 Steam 根目录顶层，劫持 DLL 必须跟 steam.exe 同级才生效
    public Task<IReadOnlyList<ForeignKernelFile>> ScanForeignKernelsAsync()
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        var found = new List<ForeignKernelFile>();
        foreach (var (file, owner) in ForeignKernelFingerprints)
        {
            try
            {
                if (File.Exists(Path.Combine(steamPath, file)))
                    found.Add(new ForeignKernelFile(file, owner));
            }
            catch (Exception ex)
            {
                LogService.Warn("内核", $"扫描 {file} 失败: {ex.Message}");
            }
        }
        try
        {
            foreach (var path in Directory.EnumerateFiles(steamPath, "greenluma*.dll"))
            {
                var name = Path.GetFileName(path);
                if (!found.Any(f => f.RelativePath.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new ForeignKernelFile(name, "GreenLuma"));
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("内核", $"扫描 GreenLuma 失败: {ex.Message}");
        }
        LogService.Info("内核", found.Count == 0
            ? "未发现第三方内核残留"
            : $"发现第三方内核残留: {string.Join(", ", found.Select(f => $"{f.RelativePath}({f.Owner})"))}");
        return Task.FromResult<IReadOnlyList<ForeignKernelFile>>(found);
    }

    // 净化：逐个删除扫描命中的文件，无备份（调用方确认框已列清）；
    // 路径越界、有目录嫌疑的一律跳过；只读属性先清再删；失败收集不中断
    public Task<(int removed, List<string> failed)> PurifyForeignKernelsAsync(IEnumerable<string> relativePaths)
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        var steamFull = Path.GetFullPath(steamPath);
        var removed = 0;
        var failed = new List<string>();
        foreach (var rel in relativePaths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(steamPath, rel));
            }
            catch
            {
                failed.Add($"{rel}：路径非法，已跳过");
                continue;
            }
            if (!full.StartsWith(steamFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(full))
            {
                failed.Add($"{rel}：不在 Steam 根目录下或不是文件，已跳过");
                continue;
            }
            if (!File.Exists(full)) continue;
            try
            {
                try { File.SetAttributes(full, FileAttributes.Normal); } catch { }
                File.Delete(full);
                removed++;
                LogService.Info("内核", $"已移除第三方内核文件: {rel}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                failed.Add($"{rel}：{ex.Message}（文件被占用？请确保 Steam 已关闭）");
            }
            catch (Exception ex)
            {
                failed.Add($"{rel}：{ex.Message}");
            }
        }
        return Task.FromResult((removed, failed));
    }

    // ========== 辅助方法 ==========

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ========== 一键获取 toml ==========

    // OST 的 toml 文件名/内容都以 DLL 的 SHA256 为准（与内核 RemoteToml 同规则）：
    // pattern/steamclient、pattern/steamui、ipc/steamclient 共 3 个文件
    private static readonly (string Channel, string Component)[] TomlTargets =
    [
        ("pattern", "steamclient"),
        ("pattern", "steamui"),
        ("ipc", "steamclient"),
    ];

    // 与内核同源：hazre/steam-monitor（自动采集 stable+beta，文件更全），
    // 布局与 OpenSteam001 版差异：channel 是 main 下目录而非分支，分支固定 main
    // jsdelivr 对这个大仓库响应不稳定，只走镜像链 + raw 直连
    private static string GitHubTomlUrl(string channel, string component, string sha)
        => $"https://raw.githubusercontent.com/hazre/steam-monitor/main/{channel}/{component}/{sha}.toml";

    public Task<SteamDllTomlInfo?> DetectSteamDllInfoAsync()
    {
        var steamPath = GetSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return Task.FromResult<SteamDllTomlInfo?>(null);
        var clientPath = Path.Combine(steamPath, "steamclient64.dll");
        var uiPath = Path.Combine(steamPath, "steamui.dll");
        if (!File.Exists(clientPath) || !File.Exists(uiPath)) return Task.FromResult<SteamDllTomlInfo?>(null);

        try
        {
            var clientSha = ComputeSha256(clientPath);
            var uiSha = ComputeSha256(uiPath);
            return Task.FromResult<SteamDllTomlInfo?>(new SteamDllTomlInfo(clientSha, uiSha));
        }
        catch (Exception ex)
        {
            LogService.Warn("内核", $"识别 Steam DLL 指纹失败：{ex.Message}");
            return Task.FromResult<SteamDllTomlInfo?>(null);
        }
    }

    public async Task<IReadOnlyList<TomlFetchResult>> FetchTomlFilesAsync(SteamDllTomlInfo info, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default)
    {
        var steamPath = GetSteamPath();
        if (string.IsNullOrEmpty(steamPath)) throw new InvalidOperationException("未找到 Steam 安装路径");
        var results = new List<TomlFetchResult>();

        for (var i = 0; i < TomlTargets.Length; i++)
        {
            var (channel, component) = TomlTargets[i];
            ct.ThrowIfCancellationRequested();
            var sha = component == "steamui" ? info.UiSha : info.ClientSha;
            var fileName = $"{sha}.toml";
            var dir = Path.Combine(steamPath, "opensteamtool", channel, component);
            var targetPath = Path.Combine(dir, fileName);
            Directory.CreateDirectory(dir);
            downloadProgress?.Report(i * 100 / TomlTargets.Length);

            // 下载源：跟随接口设置的镜像偏好展开 raw 镜像链（直连垫底）；
            // raw 直连亲口 404 = 上游确实没这个文件，后面不用再试
            string? mirror;
            try { mirror = _settingsService.Load().ManifestMirror; }
            catch { mirror = null; }
            var directRaw = GitHubTomlUrl(channel, component, sha);
            var urls = GitHubMirror.ManifestRawMirrors(directRaw, mirror);
            Exception? lastError = null;
            // 404 单独记：两个源全 404=上游没这个版本（让用户更新正式版）；
            // 混进任何一个非 404 失败=网络问题（让用户查网络），advice 不能混
            var notFoundOnly = true;
            var direct404 = false;
            var done = false;
            foreach (var url in urls)
            {
                try
                {
                    status?.Report($"正在通过 {GitHubMirror.SourceDisplayName(url)} 下载 {fileName}...");
                    var bytes = await _httpClientProvider.SendWithProxyRetryAsync(
                        "ost-toml",
                        TimeSpan.FromSeconds(30),
                        client => client.GetByteArrayAsync(url, ct),
                        null,
                        2);
                    if (bytes == null || bytes.Length == 0) throw new InvalidOperationException("远端返回空内容");
                    // 原子替换：先落临时文件再搬家，崩溃也不留截断文件祸害已存在的源文件
                    var tmpPath = targetPath + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(tmpPath, bytes, ct);
                        File.Move(tmpPath, targetPath, overwrite: true);
                    }
                    catch
                    {
                        try { File.Delete(tmpPath); } catch { }
                        throw;
                    }
                    LogService.Info("内核", $"toml 已获取：{channel}/{component}/{fileName}（{bytes.Length} 字节）");
                    results.Add(new TomlFetchResult(fileName, targetPath, true, null));
                    done = true;
                    break;
                }
                catch (Exception ex)
                {
                    // 用户主动取消直接向上传；超时是 TaskCanceledException 但 token 没取消，
                    // 按单源失败处理继续试下一个源，否则一次超时就 abort 整个流程
                    if (ex is OperationCanceledException && ct.IsCancellationRequested)
                        throw;
                    lastError = ex;
                    var is404 = ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound };
                    if (!is404)
                        notFoundOnly = false;
                    // raw 直连的 404 是权威的，见到它直接定性缺文件
                    if (is404 && GitHubMirror.IsDirectUrl(url))
                    {
                        direct404 = true;
                        break;
                    }
                    LogService.Warn("内核", $"toml 下载源失败 ({url}): {ex.Message}");
                }
            }

            if (!done)
                results.Add(new TomlFetchResult(fileName, targetPath, false, FriendlyTomlError(lastError), notFoundOnly || direct404));
        }

        downloadProgress?.Report(100);
        return results;
    }

    // 弹窗里别甩英文原文：SSL/RST 报网络，超时报超时，其他保留原文方便定位
    private static string FriendlyTomlError(Exception? ex)
    {
        if (ex == null) return "下载失败";
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return "连接失败（TLS/SSL 被中断，常见于网络波动或代理不稳），请检查网络或代理后重试";
            if (e is SocketException)
                return "连接失败（网络不可达），请检查网络或代理后重试";
        }
        if (ex is TaskCanceledException)
            return "下载超时，请重试";
        var msg = ex.Message;
        if (!string.IsNullOrEmpty(msg) && msg.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            return "连接失败（TLS/SSL 被中断，常见于网络波动或代理不稳），请检查网络或代理后重试";
        return string.IsNullOrEmpty(msg) ? "下载失败" : msg;
    }

}
