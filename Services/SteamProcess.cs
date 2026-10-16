using System.Diagnostics;
using System.IO;

namespace SteamLuaManager.Services;

// Steam 进程启停：主窗口浮动菜单与云存档页共用，行为保持一致
public static class SteamProcess
{
    public sealed record SteamLaunchResult(bool Ok, string Title, string Message);

    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ForcedShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly SteamLaunchResult Success = new(true, string.Empty, string.Empty);

    public static bool IsOperationInProgress => OperationGate.CurrentCount == 0;

    public static bool IsSteamRunning() => TryGetSteamRunning(out var running) && running;

    // GetProcessesByName 在 Windows 上枚举进程；检测失败与确认未运行分开处理。
    public static bool TryGetSteamRunning(out bool running)
    {
        running = false;
        try
        {
            var procs = Process.GetProcessesByName("steam");
            try
            {
                running = procs.Length > 0;
                return true;
            }
            finally
            {
                foreach (var p in procs)
                    try { p.Dispose(); } catch { }
            }
        }
        catch
        {
            return false;
        }
    }

    public static Task<SteamLaunchResult> ToggleSteamAsync(ISteamPathService steamPathService,
        CancellationToken cancellationToken = default) =>
        RunOperationAsync(() => GetSteamRunning()
            ? StopSteamCoreAsync(steamPathService, cancellationToken)
            : LaunchSteamCoreAsync(steamPathService, cancellationToken), cancellationToken);

    public static Task<SteamLaunchResult> LaunchSteamAsync(ISteamPathService steamPathService,
        CancellationToken cancellationToken = default) =>
        RunOperationAsync(() => LaunchSteamCoreAsync(steamPathService, cancellationToken), cancellationToken);

    public static Task<SteamLaunchResult> StopSteamAsync(ISteamPathService steamPathService,
        CancellationToken cancellationToken = default) =>
        RunOperationAsync(() => StopSteamCoreAsync(steamPathService, cancellationToken), cancellationToken);

    public static Task<SteamLaunchResult> RestartSteamAsync(ISteamPathService steamPathService,
        Action? beforeLaunch = null, CancellationToken cancellationToken = default) =>
        RunOperationAsync(async () =>
        {
            // 先确认能够重新启动，再关闭现有进程。
            var error = ResolveSteamExecutable(steamPathService, out var exePath);
            if (error != null) return error;

            var stopResult = await StopSteamCoreAsync(steamPathService, cancellationToken).ConfigureAwait(false);
            if (!stopResult.Ok) return stopResult;

            cancellationToken.ThrowIfCancellationRequested();
            beforeLaunch?.Invoke();
            return await LaunchSteamCoreAsync(steamPathService, cancellationToken, exePath).ConfigureAwait(false);
        }, cancellationToken);

    private static async Task<SteamLaunchResult> RunOperationAsync(Func<Task<SteamLaunchResult>> operation,
        CancellationToken cancellationToken)
    {
        // 不排队重复操作；所有入口共用同一个锁，整个重启流程只持锁一次。
        if (!await OperationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new SteamLaunchResult(false, "提示", "Steam 启停操作正在进行，请稍后重试");

        try
        {
            // 路径/注册表查询、进程启动与终止均在后台执行，等待使用异步轮询。
            return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Warn("Steam进程", $"启停操作失败: {ex.Message}");
            return new SteamLaunchResult(false, "错误", $"Steam 操作失败：{ex.Message}");
        }
        finally
        {
            OperationGate.Release();
        }
    }

    private static bool GetSteamRunning()
    {
        if (!TryGetSteamRunning(out var running))
            throw new InvalidOperationException("无法确认 Steam 运行状态，请稍后重试");
        return running;
    }

    private static SteamLaunchResult? ResolveSteamExecutable(ISteamPathService steamPathService,
        out string? exePath)
    {
        exePath = null;
        var path = steamPathService.GetCustomPath();
        if (string.IsNullOrWhiteSpace(path)) path = steamPathService.DetectSteamPath();
        if (string.IsNullOrWhiteSpace(path))
            return new SteamLaunchResult(false, "提示", "未检测到 Steam 安装路径，请先在设置页面配置");

        exePath = Path.Combine(path, "steam.exe");
        if (!File.Exists(exePath))
            return new SteamLaunchResult(false, "提示", $"未找到 steam.exe：{exePath}");
        return null;
    }

    private static void StartSteamProcess(string exePath, string arguments = "")
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            // Steam 必须以自家目录为 CWD，保留 OpenSteamTool 的路径约定。
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = true
        })?.Dispose();
    }

    private static async Task<SteamLaunchResult> LaunchSteamCoreAsync(ISteamPathService steamPathService,
        CancellationToken cancellationToken, string? exePath = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (GetSteamRunning()) return Success;
        if (exePath == null)
        {
            var error = ResolveSteamExecutable(steamPathService, out exePath);
            if (error != null) return error;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (GetSteamRunning()) return Success;
        StartSteamProcess(exePath!);
        if (await WaitForSteamStateAsync(true, StartupTimeout, cancellationToken).ConfigureAwait(false))
        {
            LogService.Info("Steam进程", "已确认 Steam 启动");
            return Success;
        }
        return new SteamLaunchResult(false, "错误", "启动 Steam 后 15 秒内未检测到 steam.exe，请检查 Steam 是否能正常启动");
    }

    private static async Task<SteamLaunchResult> StopSteamCoreAsync(ISteamPathService steamPathService,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GetSteamRunning()) return Success;

        try
        {
            // 优先使用正在运行的 Steam 路径，无法读取时回退到项目的路径服务。
            var exePath = FindRunningSteamExecutable();
            if (exePath == null)
            {
                var error = ResolveSteamExecutable(steamPathService, out exePath);
                if (error != null) LogService.Warn("Steam进程", error.Message);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (exePath != null && File.Exists(exePath))
                StartSteamProcess(exePath, "-shutdown");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogService.Warn("Steam进程", $"发送正常退出命令失败: {ex.Message}");
        }

        if (await WaitForSteamStateAsync(false, ShutdownTimeout, cancellationToken).ConfigureAwait(false))
        {
            LogService.Info("Steam进程", "已确认 Steam 正常退出");
            return Success;
        }

        cancellationToken.ThrowIfCancellationRequested();
        LogService.Warn("Steam进程", "Steam 10 秒内未退出，尝试强制结束进程");
        var killError = ForceStopSteamProcesses(cancellationToken);
        if (await WaitForSteamStateAsync(false, ForcedShutdownTimeout, cancellationToken).ConfigureAwait(false))
        {
            LogService.Info("Steam进程", "已确认 Steam 进程退出");
            return Success;
        }
        return new SteamLaunchResult(false, "错误", "强制结束后 Steam 仍在运行，请检查权限后重试" +
            (killError == null ? string.Empty : $"：{killError}"));
    }

    private static string? FindRunningSteamExecutable()
    {
        var procs = Process.GetProcessesByName("steam");
        try
        {
            foreach (var proc in procs)
            {
                try
                {
                    var path = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
                catch { } // 进程已退出或无权限读取模块，尝试其他进程/安装路径。
            }
            return null;
        }
        finally
        {
            foreach (var proc in procs) proc.Dispose();
        }
    }

    private static string? ForceStopSteamProcesses(CancellationToken cancellationToken)
    {
        var procs = Process.GetProcessesByName("steam");
        string? error = null;
        try
        {
            foreach (var proc in procs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { if (!proc.HasExited) proc.Kill(); }
                catch (InvalidOperationException) { } // 枚举后已自行退出。
                catch (Exception ex)
                {
                    error = ex.Message;
                    LogService.Warn("Steam进程", $"强制结束进程失败: {ex.Message}");
                }
            }
            return error;
        }
        finally
        {
            foreach (var proc in procs) proc.Dispose();
        }
    }

    private static async Task<bool> WaitForSteamStateAsync(bool running, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetSteamRunning() == running) return true;
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) return false;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
