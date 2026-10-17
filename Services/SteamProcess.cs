using System.Diagnostics;
using System.IO;

namespace SteamLuaManager.Services;

// Steam 进程启停：主窗口浮动菜单与云存档页共用，行为保持一致
public static class SteamProcess
{
    public sealed record SteamLaunchResult(bool Ok, string Title, string Message);

    public static bool IsSteamRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("steam");
            try
            {
                return procs.Length > 0;
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

    public static void KillSteamProcesses()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("steam"))
            {
                using (proc)
                {
                    if (proc.Id != 0)
                        proc.Kill();
                }
            }
        }
        catch { }
    }

    public static SteamLaunchResult LaunchSteam(ISteamPathService steamPathService)
    {
        try
        {
            var path = steamPathService.GetCustomPath() ?? steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(path))
                return new SteamLaunchResult(false, L.Get("Main_Tip"), L.Get("Svc_SpNoPath"));

            var exePath = Path.Combine(path, "steam.exe");
            if (!File.Exists(exePath))
                return new SteamLaunchResult(false, L.Get("Main_Tip"), L.Get("Svc_SpNoExe", exePath));

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                // 子进程默认继承本工具的 CWD；Steam 必须以自家目录为 CWD 启动，
                // 否则旧版 OST 按 CWD 定位 steamclient 会直接初始化失败
                WorkingDirectory = path,
                UseShellExecute = true
            })?.Dispose();
            return new SteamLaunchResult(true, string.Empty, string.Empty);
        }
        catch (Exception ex)
        {
            return new SteamLaunchResult(false, L.Get("Main_DialogError"), L.Get("Svc_SpStartFailed", ex.Message));
        }
    }

    public static SteamLaunchResult RestartSteam(ISteamPathService steamPathService)
    {
        KillSteamProcesses();
        return LaunchSteam(steamPathService);
    }
}
