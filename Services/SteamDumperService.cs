using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SteamLuaManager.Services;

public class SteamDumperService : ISteamDumperService
{
    public event Action<string>? LogLineReceived;

    private static string? _exePath;

    private static string ResolveExePath()
    {
        if (_exePath != null) return _exePath;

        // Extract embedded resource to temp directory
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "SteamLuaManager.Assets.Tools.SteamAppDumper.exe";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException($"嵌入资源未找到: {resourceName}");

        var tempDir = Path.Combine(Path.GetTempPath(), "SteamLuaManager", "Tools");
        Directory.CreateDirectory(tempDir);

        var path = Path.Combine(tempDir, "SteamAppDumper.exe");
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            stream.CopyTo(fs);
        }

        _exePath = path;
        return path;
    }

    public async Task<DumperResult> RunAsync(string appId, bool pinManifest, CancellationToken ct = default)
    {
        var result = new DumperResult();
        var exePath = ResolveExePath();

        if (!File.Exists(exePath))
        {
            EmitLog($"错误: 未找到 SteamAppDumper.exe: {exePath}");
            return result;
        }

        try
        {
            return await ConPtyRunAsync(exePath, appId, pinManifest, ct);
        }
        catch (OperationCanceledException)
        {
            EmitLog("操作已取消");
            return result;
        }
        catch (Exception ex)
        {
            EmitLog($"异常: {ex.Message}");
            return result;
        }
    }

    #region ConPTY

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe,
        IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer,
        uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer,
        uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName, string? lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
        IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr hPCON);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size,
        IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPCON);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint WAIT_TIMEOUT = 0x00000102;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; public COORD(short x, short y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public uint dwX; public uint dwY; public uint dwXSize; public uint dwYSize;
        public uint dwXCountChars; public uint dwYCountChars; public uint dwFillAttribute;
        public uint dwFlags; public short wShowWindow; public short cbReserved2;
        public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

    private async Task<DumperResult> ConPtyRunAsync(string exePath, string appId, bool pinManifest, CancellationToken ct)
    {
        var result = new DumperResult();
        var exeDir = Path.GetDirectoryName(exePath)!;

        // Yield to make this truly async (the heavy work comes right after)
        await Task.Yield();

        if (!CreatePipe(out var pipeOutRd, out var pipeOutWr, IntPtr.Zero, 0))
            throw new InvalidOperationException("CreatePipe(out) failed");

        if (!CreatePipe(out var pipeInRd, out var pipeInWr, IntPtr.Zero, 0))
        {
            CloseHandle(pipeOutRd); CloseHandle(pipeOutWr);
            throw new InvalidOperationException("CreatePipe(in) failed");
        }

        int hr = CreatePseudoConsole(new COORD(120, 6000), pipeInRd, pipeOutWr, 0, out var hPCON);
        if (hr != 0)
        {
            CloseHandle(pipeOutRd); CloseHandle(pipeOutWr);
            CloseHandle(pipeInRd); CloseHandle(pipeInWr);
            throw new InvalidOperationException($"CreatePseudoConsole failed: hr={hr}");
        }

        var siEx = new STARTUPINFOEX();
        siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        siEx.StartupInfo.dwFlags = 0x100;

        IntPtr attrListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        siEx.lpAttributeList = Marshal.AllocHGlobal(attrListSize);
        if (!InitializeProcThreadAttributeList(siEx.lpAttributeList, 1, 0, ref attrListSize))
        {
            Cleanup(pipeOutRd, pipeOutWr, pipeInRd, pipeInWr, hPCON, siEx.lpAttributeList);
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed");
        }

        if (!UpdateProcThreadAttribute(siEx.lpAttributeList, 0,
                new IntPtr(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE),
                hPCON, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
        {
            DeleteProcThreadAttributeList(siEx.lpAttributeList);
            Marshal.FreeHGlobal(siEx.lpAttributeList);
            Cleanup(pipeOutRd, pipeOutWr, pipeInRd, pipeInWr, hPCON);
            throw new InvalidOperationException("UpdateProcThreadAttribute failed");
        }

        // Start process with working dir = exe dir (so exe finds its own cache)
        // Don't pass appId as command-line arg — let it read from stdin so inputs stay in sync
        var cmdLine = $"\"{exePath}\"";
        if (!CreateProcess(null, cmdLine,
                IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero, exeDir, ref siEx, out var pi))
        {
            DeleteProcThreadAttributeList(siEx.lpAttributeList);
            Marshal.FreeHGlobal(siEx.lpAttributeList);
            Cleanup(pipeOutRd, pipeOutWr, pipeInRd, pipeInWr, hPCON);
            throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");
        }

        // Our copies of pipe ends that ConPTY now owns
        CloseHandle(pipeInRd);
        CloseHandle(pipeOutWr);
        DeleteProcThreadAttributeList(siEx.lpAttributeList);
        Marshal.FreeHGlobal(siEx.lpAttributeList);

        // Send input sequence (short delays between prompts for exe to be ready)
        WriteFile(pipeInWr, [0x0D], 1, out _, IntPtr.Zero);
        await Task.Delay(200);

        var appIdBytes = System.Text.Encoding.UTF8.GetBytes($"{appId}\r");
        WriteFile(pipeInWr, appIdBytes, (uint)appIdBytes.Length, out _, IntPtr.Zero);
        await Task.Delay(200);

        if (pinManifest)
            WriteFile(pipeInWr, [0x59, 0x0D], 2, out _, IntPtr.Zero);
        else
            WriteFile(pipeInWr, [0x0D], 1, out _, IntPtr.Zero);

        // Read output in background and watch for "Lua导出完毕" completion signal
        var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var extractionDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ReadPipeOutput(pipeOutRd, readCts.Token, extractionDone));

        // Wait for either: process exits on its own, OR read task signals completion
        while (!ct.IsCancellationRequested)
        {
            if (WaitForSingleObject(pi.hProcess, 200) == WAIT_OBJECT_0)
                break;

            if (extractionDone.Task.IsCompleted)
            {
                await Task.Delay(1000);
                TerminateProcess(pi.hProcess, 1);
                await Task.Delay(500);
                break;
            }
        }

        // Cleanup
        readCts.Cancel();
        readCts.Dispose();

        // Get exit code BEFORE closing the process handle
        GetExitCodeProcess(pi.hProcess, out var exitCode);
        if (exitCode == 259) TerminateProcess(pi.hProcess, 1);

        ClosePseudoConsole(hPCON);
        CloseHandle(pipeOutRd);
        CloseHandle(pipeInWr);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);

        result.ExitCode = (int)exitCode;

        // Scan recursively for {appId}.lua under exeDir (pin variant creates {appId}_{num}/ subdir)
        var luaFiles = Directory.GetFiles(exeDir, $"{appId}.lua", SearchOption.AllDirectories);
        result.OutputDirectory = luaFiles.Length > 0 ? Path.GetDirectoryName(luaFiles[0])! : exeDir;
        result.ExtractedFiles = [.. luaFiles];
        result.Success = luaFiles.Length > 0;

        return result;
    }

    private void ReadPipeOutput(IntPtr pipeOutRd, CancellationToken ct, TaskCompletionSource<bool> extractionDone)
    {
        var buf = new byte[8192];
        var leftover = new List<byte>();

        while (!ct.IsCancellationRequested)
        {
            if (!ReadFile(pipeOutRd, buf, (uint)buf.Length, out var bytesRead, IntPtr.Zero))
                break;

            if (bytesRead == 0)
                break;

            leftover.AddRange(buf.Take((int)bytesRead));
            var allBytes = leftover.ToArray();
            var text = System.Text.Encoding.UTF8.GetString(allBytes);
            var parts = text.Split('\n');

            for (int i = 0; i < parts.Length - 1; i++)
            {
                var line = parts[i].TrimEnd('\r');
                // Strip all ANSI / OSC escape sequences
                line = Regex.Replace(line, @"\x1B(?:\[[?0-9;]*[a-zA-Z]|\][^\\\x07]*(?:\\|\x07|$))", "");
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.Contains("开始解析数据"))
                {
                    EmitLog("正在检索数据......");
                    continue;
                }

                // Match both main app and DLC extractions (appId may contain non-digit chars, name may end with various punctuation)
                var gameMatch = Regex.Match(line, @"开始提取\s*\[[^\]]+\]\s*(.+)");
                if (gameMatch.Success)
                {
                    var name = gameMatch.Groups[1].Value.Trim().TrimEnd('。', '，', '、', '…', '.', '：');
                    if (name.Length > 0)
                        EmitLog($"开始提取{name}");
                    continue;
                }

                if (line.Contains("DecryptionKey") || line.Contains("Manifest"))
                {
                    foreach (var sub in Regex.Split(line, @"(?=DecryptionKey|Manifest)"))
                    {
                        var clean = sub.Trim();
                        if (string.IsNullOrWhiteSpace(clean))
                            continue;

                        var dk = Regex.Match(clean, @"DecryptionKey\s+(\d+)\s+Result:(\w+)");
                        if (dk.Success)
                        {
                            var id = dk.Groups[1].Value;
                            var ok = dk.Groups[2].Value;
                            EmitLog(ok == "0K" || ok == "OK"
                                ? $"{id} 密钥获取成功" : $"{id} 密钥获取失败");
                            continue;
                        }

                        var mf = Regex.Match(clean, @"Manifest\s+(.+?\.manifest)\s+Result:(\w+)");
                        if (mf.Success)
                        {
                            var id = mf.Groups[1].Value;
                            var ok = mf.Groups[2].Value;
                            EmitLog(ok == "0K" || ok == "OK"
                                ? $"{id} 版本清单获取成功" : $"{id} 版本清单获取失败");
                            continue;
                        }
                    }
                    continue;
                }

                // Pass through error/warning lines from exe, trim trailing punctuation and prompts
                if (line.Contains("失败") || line.Contains("跳过") || line.Contains("无有效数据") || line.Contains("未找到"))
                {
                    var clean = line.TrimEnd('。', '，', '.', '…', '：', '-', '—', ' ', '　');
                    // Remove trailing prompt like "请输入要提取的AppId"
                    var promptIdx = clean.IndexOf("请输入要提取的", StringComparison.Ordinal);
                    if (promptIdx > 0) clean = clean[..promptIdx].TrimEnd('.', '，');
                    EmitLog(clean);
                    if (line.Contains("无有效数据") || line.Contains("未找到指定的"))
                        extractionDone.TrySetResult(true);
                    continue;
                }

                if (line.Contains("Lua导出完毕"))
                {
                    EmitLog("------------------------------");
                    EmitLog("Lua导出成功");
                    EmitLog("");
                    extractionDone.TrySetResult(true);
                    continue;
                }

                // Everything else is silently dropped
            }
            leftover = [.. System.Text.Encoding.UTF8.GetBytes(parts[^1])];
        }
    }

    private static void Cleanup(IntPtr prd, IntPtr pwr, IntPtr pid, IntPtr piw, IntPtr hpcon, IntPtr? attrList = null)
    {
        if (prd != IntPtr.Zero) CloseHandle(prd);
        if (pwr != IntPtr.Zero) CloseHandle(pwr);
        if (pid != IntPtr.Zero) CloseHandle(pid);
        if (piw != IntPtr.Zero) CloseHandle(piw);
        if (hpcon != IntPtr.Zero) ClosePseudoConsole(hpcon);
        if (attrList.HasValue && attrList.Value != IntPtr.Zero) Marshal.FreeHGlobal(attrList.Value);
    }

    #endregion

    private void EmitLog(string message) => LogLineReceived?.Invoke(message);
}
