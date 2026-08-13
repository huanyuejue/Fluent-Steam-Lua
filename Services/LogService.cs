using System;
using System.IO;
using System.Text;

namespace SteamLuaManager.Services;

public static class LogService
{
    private const long MaxLogSize = 5 * 1024 * 1024;
    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static volatile bool _enabled;

    public static bool IsEnabled => _enabled;

    public static string LogFilePath { get; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log");

    public static void SetEnabled(bool enabled)
    {
        lock (Sync)
        {
            if (enabled == _enabled && _writer != null) return;
            _enabled = enabled;
            if (!enabled)
            {
                CloseWriter();
                return;
            }
            try
            {
                RotateIfNeeded();
                // FileShare.ReadWrite：提取 worker 子进程与主进程会同时追加写同一 app.log
                _writer = new StreamWriter(
                    new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false)) { AutoFlush = true };
                WriteCore("INFO", "日志系统", "日志记录已开启");
            }
            catch
            {
                _writer = null;
            }
        }
    }

    public static void Info(string category, string message) => Write("INFO", category, message);

    public static void Warn(string category, string message) => Write("WARN", category, message);

    public static void Error(string category, string message) => Write("ERROR", category, message);

    public static void Exception(string category, Exception ex)
    {
        if (!_enabled) return;
        Write("ERROR", category, ex.ToString());
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (_enabled && _writer != null)
            {
                try { WriteCore("INFO", "日志系统", "程序退出，日志记录已关闭"); }
                catch { }
            }
            CloseWriter();
            _enabled = false;
        }
    }

    private static void Write(string level, string category, string message)
    {
        if (!_enabled) return;
        lock (Sync)
        {
            WriteCore(level, category, message);
        }
    }

    private static void WriteCore(string level, string category, string message)
    {
        if (_writer == null) return;
        try
        {
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}][{level}] {category} | {message}");
        }
        catch { }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogFilePath)) return;
            if (new FileInfo(LogFilePath).Length < MaxLogSize) return;
            var old = LogFilePath + ".old";
            try { File.Delete(old); }
            catch { }
            File.Move(LogFilePath, old);
        }
        catch { }
    }

    private static void CloseWriter()
    {
        try { _writer?.Dispose(); }
        catch { }
        _writer = null;
    }
}
