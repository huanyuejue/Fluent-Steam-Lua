using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SteamLuaManager.Services;

/// <summary>
/// 商店页一键入库桥接进程的部署与生命周期管理。
/// 桥接程序以压缩包形式内嵌在主程序里，首次启用时释放到用户数据目录，
/// 之后按内容指纹判断是否需要重写，避免每次启动都解压。
/// </summary>
public interface IBridgeServiceManager
{
    /// <summary>桥接程序当前是否可用（已部署且文件完整）。</summary>
    bool IsDeployed { get; }

    /// <summary>桥接程序部署目录。</summary>
    string InstallDirectory { get; }

    /// <summary>进程是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>部署（必要时）并启动桥接进程。已在运行时直接返回 true。</summary>
    Task<bool> EnsureStartedAsync(CancellationToken ct = default);

    /// <summary>停止桥接进程。</summary>
    void Stop();

    /// <summary>扩展包路径（首次启用时提示用户加载用）。</summary>
    string? GetExtensionDirectory();

    /// <summary>是否已登记开机自启动。</summary>
    bool IsAutoStartEnabled { get; }

    /// <summary>写入开机自启动项。开关打开时调用。</summary>
    bool EnableAutoStart();

    /// <summary>移除开机自启动项。开关关闭时调用。</summary>
    void DisableAutoStart();

    /// <summary>内嵌包里的桥接版本，读不到返回 null。</summary>
    string? GetBundledVersion();

    /// <summary>用户机器上已部署的桥接版本，未部署时返回 null。</summary>
    string? GetDeployedVersion();

    /// <summary>内嵌包里的扩展版本，读不到返回 null。</summary>
    string? GetBundledExtensionVersion();

    /// <summary>已部署的扩展版本，未部署时返回 null。</summary>
    string? GetDeployedExtensionVersion();

    /// <summary>桥接程序需要更新。</summary>
    bool NeedsUpdate { get; }

    /// <summary>浏览器扩展需要更新。扩展被 Steam 加载后会常驻，
    /// 换了文件必须重启 Steam 才生效，因此要与桥接更新分开提示。</summary>
    bool NeedsExtensionUpdate { get; }
}

public sealed class BridgeServiceManager : IBridgeServiceManager
{
    private const string ProcessName = "FluentLuaBridge";

    // 与 Bridge 项目 BridgeConstants.Port 必须保持一致：
    // 扩展的 host_permissions 也写死了这个端口，三处改动要同步
    private const int BridgePort = 27361;
    // 内嵌资源名带根命名空间前缀，只写文件名取不到
    private const string EmbeddedPackage = "SteamLuaManager.Resources.Bridge.zip";

    // 部署指纹文件名：记录上次展开的包内容哈希
    private const string StampFileName = "bridge-deploy.stamp";

    // 桥接程序在程序目录下的子目录名
    private const string BridgeDirName = "bridge";

    private static readonly string[] BridgeFiles =
    {
        "FluentLuaBridge.exe",
        "FluentLuaBridge.dll"
    };

    private readonly string _dataRoot;
    private readonly object _gate = new();

    // 内嵌包的版本与扩展版本在整个进程内不会变，读一次就够；
    // 界面刷新与启动检查都会问，不缓存的话每次都解压一遍包
    private string? _bundledVersion;
    private bool _bundledVersionResolved;
    private string? _bundledExtensionVersion;
    private bool _bundledExtensionVersionResolved;

    public BridgeServiceManager()
    {
        // 允许用环境变量指定部署位置：便于排查部署问题，也方便绿色版把数据放到别处
        var overrideDir = Environment.GetEnvironmentVariable("FLUENT_BRIDGE_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir) && CanWriteTo(overrideDir))
        {
            _dataRoot = overrideDir;
            return;
        }

        // 优先部署在程序自身目录下：与主程序同级的 cache 等数据保持一致，
        // 用户把整个目录搬走后功能仍然可用，不会在系统盘留下残留。
        // 但程序装在 Program Files 这类受保护位置时目录不可写，退回到用户数据目录
        var appDir = Path.Combine(AppDirectory(), BridgeDirName);
        _dataRoot = CanWriteTo(appDir) ? appDir : GetFallbackRoot();
    }

    /// <summary>
    /// 程序实际所在目录。
    /// 单文件发布时 BaseDirectory 会指向临时解包目录，因此优先用进程路径推导，
    /// 拿不到再退回 BaseDirectory。
    /// </summary>
    private static string AppDirectory()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
        }
        catch
        {
            // 取不到进程路径时用下面的兜底
        }

        return AppDomain.CurrentDomain.BaseDirectory;
    }

    /// <summary>开机自启动在注册表里的位置：当前用户 + 系统自动运行。</summary>
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "FluentLuaBridge";

    public bool IsAutoStartEnabled
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) is string s && s.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 写入开机自启动。用户开启功能时调用，让桥接进程随系统起来，
    /// 否则每次开机都得先手动启动主程序才能用商店页入库。
    /// </summary>
    public bool EnableAutoStart()
    {
        try
        {
            if (!IsDeployed && !Deploy())
            {
                return false;
            }

            var exe = Path.Combine(_dataRoot, ProcessName + ".exe");
            if (!File.Exists(exe)) return false;

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return false;

            // 只写 exe 路径即可；桥接自己会根据所在目录定位资源
            key.SetValue(RunValueName, $"\"{exe}\"", Microsoft.Win32.RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Settings"), $"写入开机自启动失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>移除开机自启动。关闭功能时调用，不留系统残留。</summary>
    public void DisableAutoStart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(RunValueName) != null)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Settings"), $"移除开机自启动失败: {ex.Message}");
        }
    }

    /// <summary>探测目录是否可创建/写入，用于判断能否就地部署。</summary>
    private static bool CanWriteTo(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetFallbackRoot()
    {
        // 精简环境里 GetFolderPath 可能返回空串，直接用会让部署路径退化成相对路径；
        // 这时按 USERPROFILE 拼出标准位置
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            var profile = Environment.GetEnvironmentVariable("USERPROFILE");
            localAppData = string.IsNullOrWhiteSpace(profile)
                ? Path.GetTempPath()
                : Path.Combine(profile, "AppData", "Local");
        }
        return Path.Combine(localAppData, "FluentSteamLua", BridgeDirName);
    }

    public string InstallDirectory => _dataRoot;

    public bool IsDeployed =>
        BridgeFiles.All(f => File.Exists(Path.Combine(_dataRoot, f)));

    public bool IsRunning
    {
        get
        {
            try
            {
                return Process.GetProcessesByName(ProcessName).Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            // 必须无条件走部署：它内部会比对包指纹判断是否需要重写。
            // 早先写成 !IsDeployed && !Deploy() 会因为短路求值跳过版本检查，
            // 文件存在时永远不会更新。此处已持锁，直接调不带锁的实现
            if (!DeployCore())
            {
                return false;
            }
        }

        if (IsRunning)
        {
            return true;
        }

        try
        {
            var exe = Path.Combine(_dataRoot, ProcessName + ".exe");
            if (!File.Exists(exe)) return false;

            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = _dataRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // 把主程序的缓存根目录传给桥接进程，让它复用已有的密钥库，
            // 否则桥接会在自己目录下另存一份十几兆的 depotkeys.json。
            // 这个路径与部署位置无关，始终指向主程序自己的 cache
            psi.Environment["FLUENT_CACHE_DIR"] = Path.Combine(AppDirectory(), "cache");

            // 日志与主程序放在一起，排查时不用去用户目录里找
            psi.Environment["FLUENT_LOG_DIR"] = AppDirectory();

            Process.Start(psi);

            // 冷启动到端口可用可能要一两秒，按端口探活而不是只看进程存在，
            // 否则会在服务尚未就绪时被误判为启动失败
            for (var i = 0; i < 40; i++)
            {
                if (await IsPortListeningAsync(ct)) return true;
                if (!IsRunning) return false;   // 进程已经退出，没必要再等
                await Task.Delay(250, ct);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                try
                {
                    p.Kill(true);
                    p.WaitForExit(3000);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch
        {
            // 进程可能已被用户手动结束
        }
    }

    /// <summary>
    /// 从内嵌资源解压桥接程序。
    /// 是否重写由主程序集里的包内容哈希决定：包一变就重新展开，
    /// 不依赖包内清单文件（那种文件在打包时容易被跳过，导致永远判定为最新）。
    /// </summary>
    private bool Deploy()
    {
        // 部署过程会先停进程再覆盖文件，期间读到的是半写状态；
        // 与版本读取串行化，避免界面上闪出错误的版本号
        lock (_gate)
        {
            return DeployCore();
        }
    }

    private bool DeployCore()
    {
        try
        {
            var asm = typeof(BridgeServiceManager).Assembly;
            using var stream = asm.GetManifestResourceStream(EmbeddedPackage);
            if (stream == null) return false;

            Directory.CreateDirectory(_dataRoot);

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            // 用包内 Bridge 主程序集的大小与内容哈希作为版本指纹
            var expectedStamp = ComputePackageStamp(archive);

            var stampFile = Path.Combine(_dataRoot, StampFileName);
            if (File.Exists(stampFile)
                && File.ReadAllText(stampFile).Trim() == expectedStamp
                && IsDeployed)
            {
                return true;
            }

            // 展开前必须先停掉旧进程：DLL 被占用时覆盖会直接失败，
            // 而失败被上层 catch 吞掉后表现为“开关点了没反应”
            Stop();
            for (var attempt = 0; attempt < 10 && IsRunning; attempt++)
            {
                Thread.Sleep(200);
            }

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var target = Path.Combine(_dataRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            File.WriteAllText(stampFile, expectedStamp);
            return IsDeployed;
        }
        catch (Exception ex)
        {
            // 部署失败必须留痕：否则开关点了没反应时无从判断原因
            LogService.Warn(L.Get("LogCat_Settings"), $"部署桥接程序失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>探测桥接端口是否已在监听，用来判断服务真正就绪而不是进程刚起来。</summary>
    private static async Task<bool> IsPortListeningAsync(CancellationToken ct)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connect = client.ConnectAsync(System.Net.IPAddress.Loopback, BridgePort);
            var completed = await Task.WhenAny(connect, Task.Delay(500, ct));
            if (completed != connect) return false;
            await connect;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 用桥接主程序集的内容哈希作为包指纹。
    /// 只要重新构建过 Bridge，这个值就会变，从而触发重新展开。
    /// </summary>
    private static string ComputePackageStamp(ZipArchive archive)
    {
        try
        {
            var entry = archive.GetEntry(ProcessName + ".dll")
                        ?? archive.Entries.FirstOrDefault(e =>
                            e.Name.Equals(ProcessName + ".dll", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return string.Empty;

            using var s = entry.Open();
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(s);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 从内嵌包读取组件版本。
    /// 版本信息在程序集的 Win32 资源里，必须先落盘才能用 FileVersionInfo 读，
    /// 因此解压到临时文件读完即删，不污染部署目录。结果按进程生命周期缓存，
    /// 避免界面每次刷新都重复解压同一个包。
    /// </summary>
    public string? GetBundledVersion()
    {
        if (_bundledVersionResolved) return _bundledVersion;

        _bundledVersionResolved = true;
        try
        {
            var asm = typeof(BridgeServiceManager).Assembly;
            using var stream = asm.GetManifestResourceStream(EmbeddedPackage);
            if (stream == null) return null;

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = FindBridgeAssemblyEntry(archive);
            if (entry == null) return null;

            var temp = Path.Combine(Path.GetTempPath(), $"fluent-bridge-probe-{Guid.NewGuid():N}.dll");
            try
            {
                using (var es = entry.Open())
                using (var fo = File.Create(temp))
                {
                    es.CopyTo(fo);
                }

                var info = FileVersionInfo.GetVersionInfo(temp);
                _bundledVersion = ReadVersionString(info);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Settings"), $"读取内嵌组件版本失败: {ex.Message}");
        }
        return _bundledVersion;
    }

    /// <summary>从版本信息里取前三段，空值返回 null。</summary>
    private static string? ReadVersionString(FileVersionInfo info)
    {
        var ver = info.FileVersion?.Split(' ')[0] ?? info.ProductVersion;
        return string.IsNullOrWhiteSpace(ver) ? null : NormalizeVersion(ver);
    }

    /// <summary>读取已部署程序集的版本。文件不存在或损坏时返回 null。</summary>
    public string? GetDeployedVersion()
    {
        try
        {
            var dll = Path.Combine(_dataRoot, ProcessName + ".dll");
            if (!File.Exists(dll)) return null;

            // FileVersionInfo 读的是 Win32 资源里的版本，与 FileVersion 属性对应
            var info = FileVersionInfo.GetVersionInfo(dll);
            var ver = info.FileVersion?.Split(' ')[0] ?? info.ProductVersion;
            return string.IsNullOrWhiteSpace(ver) ? null : NormalizeVersion(ver);
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Settings"), $"读取已部署组件版本失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 已部署版本是否落后于内嵌版本。
    /// 任一侧读不到时按「无需更新」处理：宁可漏提示，也不要每次启动都弹一个查不清原因的窗。
    /// </summary>
    public bool NeedsUpdate
    {
        get
        {
            var bundled = GetBundledVersion();
            var deployed = GetDeployedVersion();
            if (string.IsNullOrEmpty(bundled) || string.IsNullOrEmpty(deployed)) return false;

            return !string.Equals(bundled, deployed, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>从内嵌包读取扩展清单里的版本号，同样只读一次。</summary>
    public string? GetBundledExtensionVersion()
    {
        if (_bundledExtensionVersionResolved) return _bundledExtensionVersion;

        _bundledExtensionVersionResolved = true;
        _bundledExtensionVersion = ReadExtensionVersionFromPackage();
        return _bundledExtensionVersion;
    }

    /// <summary>读取已部署扩展目录里的 manifest.json 版本号。</summary>
    public string? GetDeployedExtensionVersion()
    {
        try
        {
            var manifest = Path.Combine(_dataRoot, "extension", "manifest.json");
            if (!File.Exists(manifest)) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>扩展版本落后判据：两侧都读得到且不一致才算需要更新。</summary>
    public bool NeedsExtensionUpdate
    {
        get
        {
            var bundled = GetBundledExtensionVersion();
            var deployed = GetDeployedExtensionVersion();
            if (string.IsNullOrEmpty(bundled) || string.IsNullOrEmpty(deployed)) return false;

            return !string.Equals(bundled, deployed, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>从内嵌包里读扩展清单的版本，不需要先解压到磁盘。</summary>
    private static string? ReadExtensionVersionFromPackage()
    {
        try
        {
            var asm = typeof(BridgeServiceManager).Assembly;
            using var stream = asm.GetManifestResourceStream(EmbeddedPackage);
            if (stream == null) return null;

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Equals("extension/manifest.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;

            using var es = entry.Open();
            using var doc = System.Text.Json.JsonDocument.Parse(es);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 x.y.z.w 归一成 x.y.z，避免 AssemblyVersion 的多一段导致误判。</summary>
    private static string NormalizeVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 3 ? string.Join('.', parts.Take(3)) : version;
    }

    /// <summary>在包里定位桥接主程序集，找不到时按文件名宽松匹配。</summary>
    private static ZipArchiveEntry? FindBridgeAssemblyEntry(ZipArchive archive)
    {
        return archive.GetEntry(ProcessName + ".dll")
               ?? archive.Entries.FirstOrDefault(e =>
                   e.Name.Equals(ProcessName + ".dll", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>扩展目录：与桥接程序一同释放，供引导界面提示用户加载。</summary>
    public string? GetExtensionDirectory()
    {
        var dir = Path.Combine(InstallDirectory, "extension");
        return Directory.Exists(dir) ? dir : null;
    }
}
