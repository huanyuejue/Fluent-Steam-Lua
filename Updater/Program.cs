using System.Diagnostics;
using System.Runtime.InteropServices;

// 独立更新器：主程序退出后接管文件替换，避免运行时文件锁定。
// 参数: --pid <主进程id> --staging <新文件目录> --target <安装目录> --exe <主程序文件名> --version <新版本号>
// 约定: staging 内为相对安装目录的文件布局；仅搬入 staging 中列出的文件，其余文件（配置/缓存/日志）原样保留。
// 成功静默退出（新程序已拉起）；失败弹框说明并尝试回滚，未做任何更改时静默退出。

var opts = ParseArgs(args);
if (!opts.TryGetValue("--pid", out var pidText) || !int.TryParse(pidText, out var pid)
    || !opts.TryGetValue("--staging", out var stagingDir)
    || !opts.TryGetValue("--target", out var targetDir)
    || !opts.TryGetValue("--exe", out var exeName)
    || !opts.TryGetValue("--version", out var version))
{
    Fail("更新器参数不完整，无法继续。");
    return 1;
}

// 日志封顶，避免无限增长
try
{
    var logFile = new FileInfo(Path.Combine(Path.GetTempPath(), "FluentSteamLuaUpdater.log"));
    if (logFile.Exists && logFile.Length > 512 * 1024)
        logFile.Delete();
}
catch { }

Log($"Updater 启动: pid={pid} staging={stagingDir} target={targetDir} exe={exeName} version={version}");
Log($"原始命令行: {Environment.CommandLine}");

// 1. 等待主进程退出，超时则放弃（不动任何文件）
try
{
    var proc = Process.GetProcessById(pid);
    if (!proc.WaitForExit(30000))
    {
        Log("主进程 30 秒内未退出，放弃本次更新。");
        return 0;
    }
}
catch (ArgumentException)
{
    // 进程已不存在，视为已退出
}
catch (Exception ex)
{
    Log($"等待主进程退出失败: {ex.Message}");
    return Fail("等待主程序退出失败，未做任何更改。");
}

if (string.IsNullOrEmpty(stagingDir) || !Directory.Exists(stagingDir))
    return Fail("更新暂存目录不存在，未做任何更改。");
if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
    return Fail("安装目录不存在，未做任何更改。");

// 2. 收集待搬入文件（相对路径），校验主程序在其中
var stagedFiles = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories)
    .Select(f => Path.GetRelativePath(stagingDir, f))
    .ToList();
if (string.IsNullOrEmpty(exeName)
    || !stagedFiles.Any(f => string.Equals(Path.GetFileName(f), exeName, StringComparison.OrdinalIgnoreCase)))
    return Fail("更新包中未找到主程序，未做任何更改。");

// 3. 先备份目标同名文件，再搬入；失败按已搬入清单逆序回滚
var backupDir = Path.Combine(targetDir, $"backup-{version}");
var moved = new List<string>();
try
{
    foreach (var rel in stagedFiles)
    {
        var dest = Path.Combine(targetDir, rel);
        if (!File.Exists(dest)) continue;
        var backup = Path.Combine(backupDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(dest, backup, overwrite: true);
    }

    foreach (var rel in stagedFiles)
    {
        var src = Path.Combine(stagingDir, rel);
        var dest = Path.Combine(targetDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try
        {
            if (File.Exists(dest))
                File.SetAttributes(dest, FileAttributes.Normal);
        }
        catch { }
        File.Move(src, dest, overwrite: true);
        moved.Add(rel);
    }
}
catch (Exception ex)
{
    Log($"搬入文件失败: {ex.Message}，开始回滚 {moved.Count} 个文件。");
    foreach (var rel in ((IEnumerable<string>)moved).Reverse())
    {
        try
        {
            var backup = Path.Combine(backupDir, rel);
            if (File.Exists(backup))
                File.Move(backup, Path.Combine(targetDir, rel), overwrite: true);
        }
        catch (Exception rex)
        {
            Log($"回滚 {rel} 失败: {rex.Message}");
        }
    }
    return Fail($"更新文件替换失败，已回滚：{ex.Message}");
}

// 4. 拉起新版（目标 exe 缺失则仅记录，不视为失败，文件已经就位）
var newExePath = Path.Combine(targetDir, exeName);
if (File.Exists(newExePath))
{
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = newExePath,
            WorkingDirectory = targetDir,
            UseShellExecute = true,
        });
    }
    catch (Exception ex)
    {
        Log($"拉起新版失败: {ex.Message}");
        return Fail($"文件已更新，但启动新版失败，请手动启动：{ex.Message}");
    }
}
else
{
    Log($"目标 exe 不存在，跳过拉起：{newExePath}");
}

// 5. 打扫 staging（备份由新版首次启动时删除）
try
{
    if (Directory.Exists(stagingDir))
        Directory.Delete(stagingDir, recursive: true);
}
catch (Exception ex)
{
    Log($"清理 staging 失败（无害）: {ex.Message}");
}

Log("更新完成。");
return 0;

static Dictionary<string, string> ParseArgs(string[] raw)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < raw.Length; i += 2)
    {
        if (raw[i].StartsWith("--"))
            map[raw[i]] = raw[i + 1];
    }
    return map;
}

static void Log(string message)
{
    try
    {
        File.AppendAllText(
            Path.Combine(Path.GetTempPath(), "FluentSteamLuaUpdater.log"),
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
    }
    catch { }
}

static int Fail(string message)
{
    Log("FAIL: " + message);
    Native.MessageBoxW(nint.Zero, message + "\n\n可前往 GitHub 手动下载覆盖更新。", "Fluent Steam Lua 更新", 0x10);
    return 1;
}

static class Native
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
