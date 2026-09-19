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
            return Process.GetProcessesByName("steam").Length > 0;
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
                if (proc.Id != 0)
                    proc.Kill();
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
                return new SteamLaunchResult(false, "提示", "未检测到 Steam 安装路径，请先在设置页面配置");

            var exePath = Path.Combine(path, "steam.exe");
            if (!File.Exists(exePath))
                return new SteamLaunchResult(false, "提示", $"未找到 steam.exe：{exePath}");

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            });
            return new SteamLaunchResult(true, string.Empty, string.Empty);
        }
        catch (Exception ex)
        {
            return new SteamLaunchResult(false, "错误", $"启动 Steam 失败：{ex.Message}");
        }
    }

    public static SteamLaunchResult RestartSteam(ISteamPathService steamPathService)
    {
        KillSteamProcesses();
        return LaunchSteam(steamPathService);
    }
}
