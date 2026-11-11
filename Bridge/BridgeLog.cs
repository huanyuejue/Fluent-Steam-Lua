using System;
using System.IO;

namespace FluentLuaBridge;

/// <summary>
/// 桥接进程的轻量日志。主程序不在场时出问题只能靠它定位，
/// 因此始终落在桥接程序自己目录下，跟主程序数据放在一起，
/// 不往系统盘的用户目录里散落文件。
/// </summary>
internal static class BridgeLog
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = ResolveLogPath();

    private static string ResolveLogPath()
    {
        // 主程序可以通过环境变量把日志指到与它同级的位置，
        // 这样排查问题时主程序日志与桥接日志在一起，不用满盘找
        try
        {
            var dir = Environment.GetEnvironmentVariable("FLUENT_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return Path.Combine(dir, "bridge.log");
            }
        }
        catch
        {
            // 读环境变量失败就退回自身目录
        }

        return Path.Combine(AppContext.BaseDirectory, "bridge.log");
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // 日志只保留最近一轮，超过 1MB 直接清空，避免长期常驻把磁盘写满
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 1024 * 1024)
                {
                    File.WriteAllText(LogPath, string.Empty);
                }

                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }
}
