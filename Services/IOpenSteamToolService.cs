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

// 注入入口（二选一）：Dwmapi 反作弊友好（默认），Version 用于首载隔离
public enum InjectionMode
{
    None,
    Dwmapi,
    Version
}

// 磁盘上两个入口 DLL 的激活组合（只看激活文件是否存在）：
// 内核启用态下用来区分「两个都激活」与「一个都没激活」两种异常配置
public enum InjectionEntryState
{
    None,       // 无激活入口（未安装 / 内核禁用 / 两个入口都在 .disabled）
    Dwmapi,     // 仅 dwmapi 激活
    Version,    // 仅 version 激活
    BothActive  // 两个都激活：version 先注入、实际生效的是它
}

public interface IOpenSteamToolService
{
    bool IsInstalled { get; }
    bool HasDisabledKernel { get; }
    string? GetSteamPath();
    Task<string?> GetLocalVersionAsync();
    Task<(string version, string downloadUrl, string releaseUrl)> GetRemoteInfoAsync();
    Task InstallAsync(string downloadUrl, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default);
    Task UninstallAsync();
    // 卸载确认框用的实际存在文件清单（相对 Steam 根目录，含停用副本与 bin 残留）
    IReadOnlyList<string> GetInstalledKernelFiles();
    Task<int> DisableKernelAsync();
    Task<int> EnableKernelAsync();
    // 当前生效的注入入口；内核被禁用或未安装时返回 None
    InjectionMode GetInjectionMode();
    // 磁盘上两个入口 DLL 的激活组合，供界面区分双入口冲突/缺入口两种异常
    InjectionEntryState GetEntryState();
    // 切换注入入口；要求内核处于启用态（入口文件可改名），Steam 需已关闭
    Task SwitchInjectionModeAsync(InjectionMode mode);
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
    // 内核本体（核心 + 副加载器）：禁用/启用内核时整体施加/移除停用后缀
    private static readonly string[] KernelBodyDlls = ["OpenSteamTool.dll", "xinput1_4.dll"];
    // 注入入口（二选一）：dwmapi 反作弊友好（默认），version 用于首载隔离
    private static readonly string[] InjectionEntryDlls = ["dwmapi.dll", "version.dll"];
    private const string DefaultInjectionEntry = "dwmapi.dll";
    // 禁用内核用独立后缀：与注入入口停用用的 .disabled 分开，
    // 否则出厂即停用的 version.dll.disabled 会被误判成「内核被禁用」
    private const string KernelDisabledSuffix = ".off";
    // 注入入口停用后缀：沿用 OST 出厂约定（出厂的 version.dll.disabled 即由此而来）
    private const string EntryDisabledSuffix = ".disabled";
    // 安装包内允许落盘的文件：含出厂即停用的入口副本（version.dll.disabled）
    private static readonly HashSet<string> InstallWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwmapi.dll", "version.dll", "dwmapi.dll.disabled", "version.dll.disabled", "xinput1_4.dll", "OpenSteamTool.dll"
    };
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

    // 内核本体（核心/副加载器）存在 .off 停用副本即视为内核被禁用；
    // 只看本体不看入口，避免出厂 version.dll.disabled 干扰判断
    public bool HasDisabledKernel
    {
        get
        {
            try
            {
                var steamPath = GetSteamPath();
                if (string.IsNullOrEmpty(steamPath)) return false;
                return KernelBodyDlls.Any(dll => File.Exists(Path.Combine(steamPath, dll + KernelDisabledSuffix)));
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
        catch (Exception ex) { LogService.Warn(L.Get("LogCat_Kernel"), L.Get("Kernel_LogLocalVersionFailed", ex.Message)); }

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
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException(L.Get("Svc_CantDetectSteamPath"));
        // 覆盖前先记下用户选定的注入入口，装完照旧恢复，别把 version 模式用户悄悄拉回 dwmapi
        var priorMode = GetInjectionMode();

        // 目标文件被 Steam 占用时覆盖必失败，先探后下，免得白下几个包才报错
        foreach (var dll in KernelBodyDlls.Concat(InjectionEntryDlls))
        {
            var targetPath = Path.Combine(steamPath, dll);
            if (!File.Exists(targetPath)) continue;
            try
            {
                using var probe = new FileStream(targetPath, FileMode.Open, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException(L.Get("Kernel_FileLocked", dll), ex);
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
                ? L.Get("Kernel_DownloadingFrom", GitHubMirror.SourceDisplayName(src))
                : L.Get("Kernel_RetryingSource", GitHubMirror.SourceDisplayName(sources[i - 1]), GitHubMirror.SourceDisplayName(src)));

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
                LogService.Warn(L.Get("LogCat_Kernel"), L.Get("Kernel_LogSourceFailed", src, ex.Message));
                try { File.Delete(tempZip); } catch { }
            }
        }

        if (downloadedZip == null)
            throw new InvalidOperationException(L.Get("Kernel_DownloadFailed", sources.Count, lastError?.Message), lastError);

        try
        {
            ct.ThrowIfCancellationRequested();
            status?.Report(L.Get("Kernel_Extracting"));
            using var archive = ZipFile.OpenRead(downloadedZip);
            var extracted = 0;
            var extractedEntry = false;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(entry.Name);
                if (string.IsNullOrEmpty(fileName)) continue;
                if (!InstallWhitelist.Contains(fileName)) continue;
                if (fileName is "dwmapi.dll" or "version.dll") extractedEntry = true;

                var targetPath = Path.Combine(steamPath, fileName);
                try
                {
                    entry.ExtractToFile(targetPath, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(L.Get("Kernel_FileLocked", fileName), ex);
                }
                extracted++;
            }

            if (extracted == 0)
                throw new InvalidOperationException(L.Get("Kernel_ZipNoDll"));
            if (!extractedEntry)
                LogService.Warn(L.Get("LogCat_Kernel"), L.Get("Kernel_ZipNoEntryDll"));

            // 装完归一注入入口：默认 dwmapi，用户原选 version 则保持 version，
            // 避免升级后旧入口与新入口同时激活
            var targetMode = priorMode == InjectionMode.Version ? InjectionMode.Version : InjectionMode.Dwmapi;
            try
            {
                SetInjectionMode(steamPath, targetMode);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException(L.Get("Kernel_SwitchEntryFailed", ex.Message), ex);
            }

            status?.Report(L.Get("Kernel_Installed"));
        }
        finally
        {
            try { File.Delete(downloadedZip); } catch { }
        }
    }

    public Task UninstallAsync()
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException(L.Get("Svc_CantDetectSteamPath"));
        var removed = 0;
        // 内核本体与两个注入入口的所有形态（激活 / 停用 / 禁用内核）一并清掉
        foreach (var name in KernelBodyDlls.Concat(InjectionEntryDlls))
        {
            foreach (var suffix in new[] { "", EntryDisabledSuffix, KernelDisabledSuffix })
            {
                var file = name + suffix;
                var path = Path.Combine(steamPath, file);
                if (!File.Exists(path)) continue;
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                {
                    throw new InvalidOperationException(L.Get("Kernel_CantDelete", file), ex);
                }
            }
        }
        // 旧版 OST 曾把 dwmapi.dll 劫持进 bin 子目录，新版主入口只落 Steam 根目录，
        // 卸载时顺手清掉 bin 里的孤儿残留——bin 里的劫持会炸新版 Steam
        foreach (var rel in new[]
                 {
                     Path.Combine("bin", DefaultInjectionEntry),
                     Path.Combine("bin", DefaultInjectionEntry + EntryDisabledSuffix),
                     Path.Combine("bin", DefaultInjectionEntry + KernelDisabledSuffix)
                 })
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
                throw new InvalidOperationException(L.Get("Kernel_CantDelete", rel), ex);
            }
        }
        if (removed == 0)
            throw new InvalidOperationException(L.Get("Kernel_NotInstalledFiles"));
        return Task.CompletedTask;
    }

    // 卸载预览：枚举当前真实存在的 OST 文件（本体/入口的激活、停用、禁用三种形态 + bin 孤儿残留），
    // 确认框按这份磁盘实测清单展示，避免文案写死三件套、与部署形态脱节
    public IReadOnlyList<string> GetInstalledKernelFiles()
    {
        var steamPath = GetSteamPath();
        var list = new List<string>();
        if (string.IsNullOrEmpty(steamPath)) return list;
        foreach (var name in KernelBodyDlls.Concat(InjectionEntryDlls))
        {
            foreach (var suffix in new[] { "", EntryDisabledSuffix, KernelDisabledSuffix })
            {
                var rel = name + suffix;
                if (File.Exists(Path.Combine(steamPath, rel))) list.Add(rel);
            }
        }
        foreach (var suffix in new[] { "", EntryDisabledSuffix, KernelDisabledSuffix })
        {
            var rel = Path.Combine("bin", DefaultInjectionEntry + suffix);
            if (File.Exists(Path.Combine(steamPath, rel))) list.Add(rel);
        }
        return list;
    }

    // 临时禁用：给内核本体与当前激活入口加 .off 后缀，返回实际处理数；
    // 已是禁用态或目标后缀已存在的跳过，调用方负责 Steam 关闭门禁
    public Task<int> DisableKernelAsync()
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException(L.Get("Svc_CantDetectSteamPath"));
        var done = 0;
        var skipped = new List<string>();
        // 内核本体必禁；入口只禁当前激活的那个——未激活的入口本就是 .disabled，不动它
        var targets = new List<string>(KernelBodyDlls);
        var activeEntry = ActiveEntryFile(steamPath);
        if (activeEntry != null) targets.Add(activeEntry);
        foreach (var dll in targets)
        {
            var path = Path.Combine(steamPath, dll);
            var disabledPath = path + KernelDisabledSuffix;
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
                throw new InvalidOperationException(L.Get("Kernel_CantDisable", dll), ex);
            }
        }
        if (done == 0 && skipped.Count == 0)
            throw new InvalidOperationException(L.Get("Kernel_NotInstalledFiles"));
        if (skipped.Count > 0)
            LogService.Warn(L.Get("LogCat_Kernel"), L.Get("Kernel_LogSkippedDisabled", string.Join(", ", skipped)));
        LogService.Info(L.Get("LogCat_Kernel"), L.Get("Kernel_LogDisabled", done));
        return Task.FromResult(done);
    }

    // 启用：把内核本体与注入入口的 .off 副本还原回原名，返回实际处理数；原名已存在的跳过，避免覆盖
    public Task<int> EnableKernelAsync()
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException(L.Get("Svc_CantDetectSteamPath"));
        var done = 0;
        var skipped = new List<string>();
        foreach (var dll in KernelBodyDlls.Concat(InjectionEntryDlls))
        {
            var path = Path.Combine(steamPath, dll);
            var disabledPath = path + KernelDisabledSuffix;
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
                throw new InvalidOperationException(L.Get("Kernel_CantEnable", dll, ex.Message), ex);
            }
        }
        if (done == 0 && skipped.Count == 0)
            throw new InvalidOperationException(L.Get("Kernel_NoDisabledFound"));
        if (skipped.Count > 0)
            LogService.Warn(L.Get("LogCat_Kernel"), L.Get("Kernel_LogSkippedExists", string.Join(", ", skipped)));
        LogService.Info(L.Get("LogCat_Kernel"), L.Get("Kernel_LogEnabled", done));
        return Task.FromResult(done);
    }

    // ========== 注入入口切换 ==========

    // 当前激活的注入入口文件（dwmapi 优先）；未安装或被禁用时为 null
    private static string? ActiveEntryFile(string steamPath)
    {
        foreach (var dll in InjectionEntryDlls)
        {
            if (File.Exists(Path.Combine(steamPath, dll))) return dll;
        }
        return null;
    }

    // 磁盘上两个入口 DLL 的激活组合；只反映文件现状，未安装/禁用的语义由调用方结合状态判断
    public InjectionEntryState GetEntryState()
    {
        var steamPath = GetSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return InjectionEntryState.None;
        var dwmapi = File.Exists(Path.Combine(steamPath, "dwmapi.dll"));
        var version = File.Exists(Path.Combine(steamPath, "version.dll"));
        if (dwmapi && version) return InjectionEntryState.BothActive;
        if (dwmapi) return InjectionEntryState.Dwmapi;
        if (version) return InjectionEntryState.Version;
        return InjectionEntryState.None;
    }

    // 当前生效的注入方式。两个入口同时激活时以 version 为准：它作为 steam.exe 的
    // 静态导入先注入，dwmapi 之后即便被加载，再去加载本体也已是空操作、不产生效果。
    public InjectionMode GetInjectionMode()
    {
        return GetEntryState() switch
        {
            InjectionEntryState.Dwmapi => InjectionMode.Dwmapi,
            InjectionEntryState.Version => InjectionMode.Version,
            InjectionEntryState.BothActive => InjectionMode.Version,
            _ => InjectionMode.None
        };
    }

    // 切换注入入口：只有内核处于启用态（入口文件可改名）时才允许，
    // 否则 .off 下的入口无法安全归一
    public Task SwitchInjectionModeAsync(InjectionMode mode)
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException(L.Get("Svc_CantDetectSteamPath"));
        if (mode is not (InjectionMode.Dwmapi or InjectionMode.Version))
            throw new InvalidOperationException(L.Get("Kernel_SwitchEntryInvalid"));
        if (HasDisabledKernel)
            throw new InvalidOperationException(L.Get("Kernel_SwitchEntryNeedEnabled"));
        try
        {
            SetInjectionMode(steamPath, mode);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
        {
            throw new InvalidOperationException(L.Get("Kernel_SwitchEntryFailed", ex.Message), ex);
        }
        LogService.Info(L.Get("LogCat_Kernel"), L.Get("Kernel_LogEntrySwitched", mode == InjectionMode.Version ? "version.dll" : "dwmapi.dll"));
        return Task.CompletedTask;
    }

    // 幂等归一：目标入口置为激活（清理冗余 .disabled 副本），另一入口置为停用
    private static void SetInjectionMode(string steamPath, InjectionMode mode)
    {
        var activeName = mode == InjectionMode.Version ? "version.dll" : DefaultInjectionEntry;
        var otherName = activeName == "version.dll" ? "dwmapi.dll" : "version.dll";

        var activePath = Path.Combine(steamPath, activeName);
        var activeDisabled = activePath + EntryDisabledSuffix;
        if (!File.Exists(activePath))
        {
            if (File.Exists(activeDisabled))
                File.Move(activeDisabled, activePath);
            else
                throw new IOException(L.Get("Kernel_EntryFileMissing", activeName));
        }
        else if (File.Exists(activeDisabled))
        {
            File.Delete(activeDisabled);
        }

        var otherPath = Path.Combine(steamPath, otherName);
        var otherDisabled = otherPath + EntryDisabledSuffix;
        if (File.Exists(otherPath))
        {
            if (File.Exists(otherDisabled))
                File.Delete(otherPath);
            else
                File.Move(otherPath, otherDisabled);
        }
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
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException(L.Get("Svc_CantDetectSteamPath"));
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
                LogService.Warn(L.Get("LogCat_Kernel"), L.Get("Kernel_LogScanFailed", file, ex.Message));
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
            LogService.Warn(L.Get("LogCat_Kernel"), L.Get("Kernel_LogScanGreenLumaFailed", ex.Message));
        }
        LogService.Info(L.Get("LogCat_Kernel"), found.Count == 0
            ? L.Get("Kernel_NoForeignFound")
            : L.Get("Kernel_ForeignFound", string.Join(", ", found.Select(f => $"{f.RelativePath}({f.Owner})"))));
        return Task.FromResult<IReadOnlyList<ForeignKernelFile>>(found);
    }

    // 净化：逐个删除扫描命中的文件，无备份（调用方确认框已列清）；
    // 路径越界、有目录嫌疑的一律跳过；只读属性先清再删；失败收集不中断
    public Task<(int removed, List<string> failed)> PurifyForeignKernelsAsync(IEnumerable<string> relativePaths)
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException(L.Get("Svc_CantDetectSteamPath"));
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
                failed.Add(L.Get("Kernel_PurifyBadPath", rel));
                continue;
            }
            if (!full.StartsWith(steamFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(full))
            {
                failed.Add(L.Get("Kernel_PurifyOutOfRoot", rel));
                continue;
            }
            if (!File.Exists(full)) continue;
            try
            {
                try { File.SetAttributes(full, FileAttributes.Normal); } catch { }
                File.Delete(full);
                removed++;
                LogService.Info(L.Get("LogCat_Kernel"), L.Get("Kernel_LogPurified", rel));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                failed.Add(L.Get("Kernel_PurifyLocked", rel, ex.Message));
            }
            catch (Exception ex)
            {
                failed.Add(L.Get("Cloud_LabelErrorGeneric", rel, ex.Message));
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
            LogService.Warn(L.Get("LogCat_Kernel"), L.Get("Kernel_LogDllFingerprintFailed", ex.Message));
            return Task.FromResult<SteamDllTomlInfo?>(null);
        }
    }

    public async Task<IReadOnlyList<TomlFetchResult>> FetchTomlFilesAsync(SteamDllTomlInfo info, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default)
    {
        var steamPath = GetSteamPath();
        if (string.IsNullOrEmpty(steamPath)) throw new InvalidOperationException(L.Get("Kernel_SteamPathNotFound"));
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
                    status?.Report(L.Get("Kernel_DownloadingToml", GitHubMirror.SourceDisplayName(url), fileName));
                    var bytes = await _httpClientProvider.SendWithProxyRetryAsync(
                        "ost-toml",
                        TimeSpan.FromSeconds(30),
                        client => client.GetByteArrayAsync(url, ct),
                        null,
                        2);
                    if (bytes == null || bytes.Length == 0) throw new InvalidOperationException(L.Get("Kernel_EmptyRemote"));
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
                    LogService.Info(L.Get("LogCat_Kernel"), L.Get("Kernel_LogTomlFetched", channel, component, fileName, bytes.Length));
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
                    LogService.Warn(L.Get("LogCat_Kernel"), L.Get("Kernel_LogTomlSourceFailed", url, ex.Message));
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
        if (ex == null) return L.Get("Kernel_TomlFailed");
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return L.Get("Kernel_TomlTlsBroken");
            if (e is SocketException)
                return L.Get("Kernel_TomlUnreachable");
        }
        if (ex is TaskCanceledException)
            return L.Get("Kernel_TomlTimeout");
        var msg = ex.Message;
        if (!string.IsNullOrEmpty(msg) && msg.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            return L.Get("Kernel_TomlTlsBroken");
        return string.IsNullOrEmpty(msg) ? L.Get("Kernel_TomlFailed") : msg;
    }

}
