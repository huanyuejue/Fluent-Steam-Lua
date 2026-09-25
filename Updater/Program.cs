using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

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

// 0. 先试写目标目录：Program Files 一类无权限目录、被管理员属主锁定的目录，
// 在这里就能发现，不用等 30 秒、不走到一半再回滚
if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir) && !CanWriteDirectory(targetDir))
{
    Log("目标目录不可写，请求提权后重试。");
    const uint MB_YESNO = 0x04;
    const uint MB_ICONWARNING = 0x30;
    const int IDYES = 6;
    var answer = Native.MessageBoxW(nint.Zero,
        $"安装目录无写入权限，可能是装在了 Program Files 等系统目录。\n\n是否以管理员身份重试本次更新？\n\n目标目录：{targetDir}",
        "Fluent Steam Lua 更新", MB_YESNO | MB_ICONWARNING);
    if (answer != IDYES)
        return Fail("安装目录无写入权限，已取消。可将软件重装到用户目录（如桌面）后再更新。");
    if (RelaunchElevated(opts))
    {
        Log("已拉起提权副本接管，本实例退出。");
        return 0;
    }
    return Fail("提权重启失败（可能取消了 UAC 确认），未做任何更改。");
}

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
List<string> stoppedMonitors = new();
try
{
    // 同版本重试时上次失败可能留下只读残留文件，不清掉会 poison 下一次备份；
    // 先整体清掉重建，清不掉也不拦路，后面单文件覆盖时各自再清一次
    try
    {
        if (Directory.Exists(backupDir))
        {
            foreach (var f in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(backupDir, recursive: true);
        }
    }
    catch (Exception ex) { Log($"清理旧备份目录失败（继续尝试覆盖写入）: {ex.Message}"); }

    foreach (var rel in stagedFiles)
    {
        var dest = Path.Combine(targetDir, rel);
        if (!File.Exists(dest)) continue;
        var backup = Path.Combine(backupDir, rel);
        Exception? backupError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                try { File.SetAttributes(dest, FileAttributes.Normal); }
                catch (Exception attrEx) { Log($"清理只读属性失败 {rel}：{attrEx.Message}"); }
                try { if (File.Exists(backup)) File.SetAttributes(backup, FileAttributes.Normal); } catch { }
                File.Copy(dest, backup, overwrite: true);
                backupError = null;
                break;
            }
            catch (Exception ex)
            {
                backupError = ex;
                Log($"备份 {rel} 第 {attempt + 1} 次失败: {ex.Message}");
                Thread.Sleep(500);
            }
        }
        if (backupError != null)
            throw new IOException($"备份 {rel} 失败: {backupError.Message}", backupError);
    }

    // 搬入前结束正在运行的自家常驻进程（如 SvcMonitor）：只杀路径在目标目录下的同名进程，
    // 主进程前面已经等过退出了；杀完记下来，搬完（无论成败）负责拉起，不把服务搞丢
    stoppedMonitors = StopBlockingProcesses(stagedFiles, targetDir, pid);

    foreach (var rel in stagedFiles)
    {
        var src = Path.Combine(stagingDir, rel);
        var dest = Path.Combine(targetDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Exception? lastError = null;
        // 杀毒软件/OneDrive 会瞬时锁文件，单次失败直接判死刑太冤，重试几次
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (File.Exists(dest))
                {
                    try { File.SetAttributes(dest, FileAttributes.Normal); } catch { }
                }
                File.Move(src, dest, overwrite: true);
                lastError = null;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log($"搬入 {rel} 第 {attempt + 1} 次失败: {ex.Message}");
                Thread.Sleep(500);
            }
        }
        if (lastError != null)
            throw new IOException($"搬入 {rel} 失败: {lastError.Message}", lastError);
        moved.Add(rel);
    }
    RestartStoppedProcesses(stoppedMonitors);
}
catch (Exception ex)
{
    Log($"搬入文件失败: {ex.Message}，开始回滚 {moved.Count} 个文件。");
    foreach (var rel in ((IEnumerable<string>)moved).Reverse())
    {
        try
        {
            var backup = Path.Combine(backupDir, rel);
            var dest = Path.Combine(targetDir, rel);
            if (!File.Exists(backup))
            {
                Log($"回滚 {rel}：无备份，跳过");
                continue;
            }
            try { if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal); } catch { }
            File.Move(backup, dest, overwrite: true);
            Log($"回滚 {rel} 成功");
        }
        catch (Exception rex)
        {
            Log($"回滚 {rel} 失败: {rex.Message}");
        }
    }
    RestartStoppedProcesses(stoppedMonitors);
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

// 建删一个临时文件试写，任何异常都算不可写
static bool CanWriteDirectory(string dir)
{
    try
    {
        var probe = Path.Combine(dir, $".write_test_{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(probe, new byte[1]);
        File.Delete(probe);
        return true;
    }
    catch
    {
        return false;
    }
}

// 原参数原样透传，拉起提权副本接管；拉起成功后调用方直接退出即可
static bool RelaunchElevated(Dictionary<string, string> opts)
{
    try
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var key in new[] { "--pid", "--staging", "--target", "--exe", "--version" })
        {
            psi.ArgumentList.Add(key);
            psi.ArgumentList.Add(opts[key]);
        }
        Process.Start(psi);
        return true;
    }
    catch (Exception ex)
    {
        Log($"提权重启失败: {ex.Message}");
        return false;
    }
}

// 找出更新包里、当前正从安装目录运行的自家 exe（如常驻的 SvcMonitor），结束并返回待重启路径；
// 路径不在目标目录下的同名进程一律不碰，等过退出的主进程也排除在外
static List<string> StopBlockingProcesses(List<string> stagedFiles, string targetDir, int mainPid)
{
    var stopped = new List<string>();
    var targetFull = Path.GetFullPath(targetDir);
    var exeNames = stagedFiles
        .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        .Select(Path.GetFileName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    foreach (var exeName in exeNames)
    {
        if (string.IsNullOrEmpty(exeName)) continue;
        Process[] procs;
        try { procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)); }
        catch { continue; }
        foreach (var p in procs)
        {
            try
            {
                if (p.Id == mainPid || p.HasExited) continue;
                var path = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) continue;
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(targetFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                Log($"结束常驻进程以释放文件：{full} (pid {p.Id})");
                p.Kill();
                if (!p.WaitForExit(5000))
                    Log($"等待进程退出超时：{full} (pid {p.Id})");
                if (!stopped.Contains(full, StringComparer.OrdinalIgnoreCase))
                    stopped.Add(full);
            }
            catch (Exception ex)
            {
                Log($"结束进程失败（跳过）：{ex.Message}");
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
    }
    return stopped;
}

static void RestartStoppedProcesses(List<string> exePaths)
{
    foreach (var exePath in exePaths)
    {
        try
        {
            if (!File.Exists(exePath)) continue;
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            Log($"已重启常驻进程：{exePath}");
        }
        catch (Exception ex)
        {
            Log($"重启常驻进程失败 {exePath}：{ex.Message}");
        }
    }
}

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
