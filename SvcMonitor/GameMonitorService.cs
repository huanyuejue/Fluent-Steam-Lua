using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SvcMonitor.Models;

namespace SvcMonitor;

public class GameMonitorService : BackgroundService
{
    // ── SendInput Win32 ──
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    const int INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // ── Key maps ──
    static readonly Dictionary<string, byte> VKMap = new()
    {
        ["Num 1"] = 0x61, ["Num 2"] = 0x62, ["Num 3"] = 0x63,
        ["Num 4"] = 0x64, ["Num 5"] = 0x65, ["Num 6"] = 0x66,
        ["Num 7"] = 0x67, ["Num 8"] = 0x68, ["Num 9"] = 0x69,
        ["Num 0"] = 0x60, ["Num +"] = 0x6B, ["Num -"] = 0x6D,
        ["Num *"] = 0x6A, ["Num /"] = 0x6F, ["Num ."] = 0x6E,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["Home"] = 0x24, ["End"] = 0x23, ["Insert"] = 0x2D,
        ["Delete"] = 0x2E, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
    };

    static readonly Dictionary<string, byte> ScanMap = new()
    {
        ["Num 1"] = 0x4F, ["Num 2"] = 0x50, ["Num 3"] = 0x51,
        ["Num 4"] = 0x4B, ["Num 5"] = 0x4C, ["Num 6"] = 0x4D,
        ["Num 7"] = 0x47, ["Num 8"] = 0x48, ["Num 9"] = 0x49,
        ["Num 0"] = 0x52, ["Num +"] = 0x4E, ["Num -"] = 0x4A,
        ["Num *"] = 0x37, ["Num /"] = 0x35,
        ["F1"] = 0x3B, ["F2"] = 0x3C, ["F3"] = 0x3D, ["F4"] = 0x3E,
        ["F5"] = 0x3F, ["F6"] = 0x40, ["F7"] = 0x41, ["F8"] = 0x42,
        ["F9"] = 0x43, ["F10"] = 0x44, ["F11"] = 0x57, ["F12"] = 0x58,
        ["Home"] = 0x47, ["End"] = 0x4F, ["Insert"] = 0x52,
        ["Delete"] = 0x53, ["PageUp"] = 0x49, ["PageDown"] = 0x51,
    };

    // ── Modifier keys ──
    static readonly Dictionary<string, (byte vk, byte scan)> ModifierMap = new()
    {
        ["Ctrl"] = (0x11, 0x1D),
        ["Alt"] = (0x12, 0x38),
        ["Shift"] = (0x10, 0x2A),
    };

    static readonly Dictionary<string, bool> ExtendedMap = new()
    {
        ["Home"] = true, ["End"] = true, ["Insert"] = true,
        ["Delete"] = true, ["PageUp"] = true, ["PageDown"] = true,
    };

    private readonly ILogger<GameMonitorService> _logger;
    private List<TrainerBinding> _bindings = new();
    private readonly Dictionary<string, Process> _activeTrainers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRead = DateTime.MinValue;

    private static string ConfigPath => Path.Combine(
        AppContext.BaseDirectory, "bindings.json");

    public GameMonitorService(ILogger<GameMonitorService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SvcMonitor 服务已启动");
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ReloadBindingsIfChanged();

                // 无激活绑定项则自我退出
                if (!_bindings.Any(b => b.IsEnabled))
                {
                    _logger.LogInformation("无激活绑定项，SvcMonitor 服务已退出");
                    Environment.Exit(0);
                }

                ProcessBindings();
                await Task.Delay(2000, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "监控循环异常");
                await Task.Delay(5000, stoppingToken);
            }
        }

        CleanupAll();
    }

    private void ReloadBindingsIfChanged()
    {
        var file = new FileInfo(ConfigPath);
        if (!file.Exists) return;
        if (file.LastWriteTimeUtc <= _lastRead) return;

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var list = JsonSerializer.Deserialize<List<TrainerBinding>>(json);
            if (list != null)
            {
                _bindings = list;
                _lastRead = file.LastWriteTimeUtc;
                var enabled = _bindings.Count(b => b.IsEnabled);
                _logger.LogInformation("已加载绑定配置: {Total} 个, 启用: {Enabled}", _bindings.Count, enabled);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取绑定配置失败");
        }
    }

    private void ProcessBindings()
    {
        var enabled = _bindings.Where(b => b.IsEnabled).ToList();
        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in enabled)
        {
            tracked.Add(binding.TrainerFilePath);
            var gameProc = FindGameProcess(binding.GameExePath);

            if (gameProc != null && !_activeTrainers.ContainsKey(binding.TrainerFilePath))
                LaunchTrainer(binding);
            else if (gameProc == null && _activeTrainers.TryGetValue(binding.TrainerFilePath, out var proc))
            {
                if (!proc.HasExited)
                {
                    try { proc.Kill(); proc.WaitForExit(2000); }
                    catch { }
                }
                _activeTrainers.Remove(binding.TrainerFilePath);
            }
        }

        foreach (var key in _activeTrainers.Keys.ToList())
        {
            if (!tracked.Contains(key))
            {
                var p = _activeTrainers[key];
                if (!p.HasExited)
                {
                    try { p.Kill(); p.WaitForExit(2000); }
                    catch { }
                }
                _activeTrainers.Remove(key);
            }
        }
    }

    private void LaunchTrainer(TrainerBinding binding)
    {
        try
        {
            if (!File.Exists(binding.TrainerFilePath))
            {
                _logger.LogWarning("修改器文件不存在: {Path}", binding.TrainerFilePath);
                return;
            }

            var procName = Path.GetFileNameWithoutExtension(binding.TrainerFilePath);
            var existing = Process.GetProcessesByName(procName)
                .FirstOrDefault(p => !p.HasExited);
            if (existing != null)
            {
                _activeTrainers[binding.TrainerFilePath] = existing;
                return;
            }

            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = binding.TrainerFilePath,
                UseShellExecute = true
            });

            if (proc != null)
            {
                _activeTrainers[binding.TrainerFilePath] = proc;
                _logger.LogInformation("已启动修改器: {Name}", binding.TrainerDisplayName);

                // 延迟发送自动按键
                if (binding.AutoKeys.Count > 0)
                    _ = SendAutoKeysAsync(binding);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动修改器失败: {Name}", binding.TrainerDisplayName);
        }
    }

    private async Task SendAutoKeysAsync(TrainerBinding binding)
    {
        // 等待修改器初始化 (约10秒)
        _logger.LogInformation("等待修改器初始化后发送自动按键 ({Count} 个)", binding.AutoKeys.Count);
        await Task.Delay(10000);

        foreach (var keyStr in binding.AutoKeys)
        {
            // Handle both FullKey and DisplayText format
            var actualKey = keyStr;
            var dashIdx = keyStr.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx > 0) actualKey = keyStr[..dashIdx];

            // Parse modifiers like "Ctrl+Num 1", "Alt+F1"
            string? modifier = null;
            string keyName;
            var plusIdx = actualKey.IndexOf('+');
            if (plusIdx > 0 && ModifierMap.ContainsKey(actualKey[..plusIdx]))
            {
                modifier = actualKey[..plusIdx];
                keyName = actualKey[(plusIdx + 1)..];
            }
            else
            {
                keyName = actualKey;
            }

            if (!VKMap.TryGetValue(keyName, out var vk)) continue;

            _logger.LogInformation("SendInput VK: {Key}", keyStr);

            // 全部用 VK 发送，避免 scan+VK 双重按下
            // Modifier down
            if (modifier != null && ModifierMap.TryGetValue(modifier, out var mod))
            {
                SendInputVk(mod.vk, down: true);
                await Task.Delay(50);
            }

            // Main key down
            SendInputVk(vk, down: true);
            await Task.Delay(100);
            SendInputVk(vk, down: false);

            // Modifier up
            if (modifier != null && ModifierMap.TryGetValue(modifier, out mod))
            {
                await Task.Delay(30);
                SendInputVk(mod.vk, down: false);
            }

            // 按键间隔
            await Task.Delay(300);
        }

        _logger.LogInformation("自动按键发送完成 ({Count} 个)", binding.AutoKeys.Count);
    }

    private static void SendInputKey(byte scan, bool extended, bool down)
    {
        uint flags = KEYEVENTF_SCANCODE;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendInputVk(byte vk, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = down ? 0u : KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static Process? FindGameProcess(string exePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(exePath);
            return Process.GetProcessesByName(name)
                .FirstOrDefault(p =>
                {
                    try { return p.MainModule?.FileName?.Equals(exePath, StringComparison.OrdinalIgnoreCase) == true; }
                    catch { return false; }
                });
        }
        catch { return null; }
    }

    private void CleanupAll()
    {
        foreach (var proc in _activeTrainers.Values)
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(); proc.WaitForExit(2000); }
                catch { }
            }
        }
        _activeTrainers.Clear();
    }
}
