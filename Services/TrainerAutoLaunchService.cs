using System.Diagnostics;
using System.IO;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class TrainerAutoLaunchService : ITrainerAutoLaunchService
{
    private readonly ISettingsService _settingsService;
    private List<TrainerBinding> _bindings = new();
    private readonly Dictionary<string, RunningState> _activeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingLaunches = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private readonly object _lock = new();
    private bool _pollErrorLogged;

    public event Action<string>? StatusChanged;

    public TrainerAutoLaunchService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public void Start()
    {
        _bindings = _settingsService.Load().TrainerBindings ?? new();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _ = PollingLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        lock (_lock)
        {
            _pendingLaunches.Clear();
            foreach (var state in _activeStates.Values)
            {
                try
                {
                    if (!state.TrainerProcess.HasExited)
                        KillProcessTree(state.TrainerProcess);
                }
                catch { }
                state.Dispose();
            }
            _activeStates.Clear();
        }
    }

    public void ReloadBindings()
    {
        lock (_lock)
        {
            _bindings = _settingsService.Load().TrainerBindings ?? new();
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task PollingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
                ProcessBindings();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!_pollErrorLogged)
                {
                    _pollErrorLogged = true;
                    LogService.Warn("自动启动", $"轮询绑定异常: {ex.Message}");
                }
            }
        }
    }

    private void ProcessBindings()
    {
        List<TrainerBinding> currentBindings;
        lock (_lock) { currentBindings = _bindings.Where(b => b.IsEnabled).ToList(); }

        var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in currentBindings)
        {
            tracked.Add(binding.TrainerFilePath);
            var gameProc = FindGameProcess(binding.GameExePath);

            lock (_lock)
            {
                var isActive = _activeStates.ContainsKey(binding.TrainerFilePath)
                    || _pendingLaunches.Contains(binding.TrainerFilePath);

                if (gameProc != null && !isActive)
                    LaunchTrainer(binding, gameProc);
                else if (gameProc == null && isActive)
                    StopTrainer(binding.TrainerFilePath);
                else if (gameProc != null && _activeStates.TryGetValue(binding.TrainerFilePath, out var st))
                {
                    try { st.GameProcess?.Dispose(); } catch { }
                    st.GameProcess = gameProc;
                }
                else
                {
                    // 未匹配分支产生的临时 Process 需释放，避免句柄泄漏
                    try { gameProc?.Dispose(); } catch { }
                }
            }
        }

        lock (_lock)
        {
            foreach (var key in _activeStates.Keys.ToList())
            {
                if (!tracked.Contains(key))
                {
                    var state = _activeStates[key];
                    try
                    {
                        if (!state.TrainerProcess.HasExited)
                            KillProcessTree(state.TrainerProcess);
                    }
                    catch { }
                    state.Dispose();
                    _activeStates.Remove(key);
                }
            }
        }
    }

    private void LaunchTrainer(TrainerBinding binding, Process gameProc)
    {
        try
        {
            if (!File.Exists(binding.TrainerFilePath)) return;
            lock (_lock)
            {
                if (!_pendingLaunches.Add(binding.TrainerFilePath)) return;
            }

            var procName = Path.GetFileNameWithoutExtension(binding.TrainerFilePath);

            var existing = FindProcess(procName, p => !p.HasExited);
            if (existing != null)
            {
                lock (_lock) _pendingLaunches.Remove(binding.TrainerFilePath);
                ActivateTrainer(existing, gameProc, binding.TrainerFilePath);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = binding.TrainerFilePath,
                UseShellExecute = true
            });

            StatusChanged?.Invoke($"正在启动修改器: {binding.TrainerDisplayName}");

            var capturedBinding = binding;

            _ = Task.Run(async () =>
            {
                try
                {
                    Process? realProc = null;
                    for (int i = 0; i < 15; i++)
                    {
                        await Task.Delay(500);
                        realProc = FindProcess(procName, p => !p.HasExited);
                        if (realProc != null) break;
                    }

                    lock (_lock) _pendingLaunches.Remove(binding.TrainerFilePath);

                    if (realProc == null)
                    {
                        StatusChanged?.Invoke($"修改器启动超时: {capturedBinding.TrainerDisplayName}");
                        return;
                    }

                    ActivateTrainer(realProc, gameProc, capturedBinding.TrainerFilePath);
                }
                catch (Exception ex)
                {
                    lock (_lock) _pendingLaunches.Remove(binding.TrainerFilePath);
                    StatusChanged?.Invoke($"启动修改器失败: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            lock (_lock) _pendingLaunches.Remove(binding.TrainerFilePath);
            StatusChanged?.Invoke($"启动修改器失败: {ex.Message}");
        }
    }

    private void ActivateTrainer(Process trainerProc, Process gameProc, string filePath)
    {
        var state = new RunningState
        {
            TrainerProcess = trainerProc,
            GameProcess = gameProc
        };
        _activeStates[filePath] = state;
        StatusChanged?.Invoke($"已启动修改器: {trainerProc.ProcessName}");
    }

    private void StopTrainer(string filePath)
    {
        lock (_lock) _pendingLaunches.Remove(filePath);
        if (!_activeStates.TryGetValue(filePath, out var state)) return;
        try
        {
            if (!state.TrainerProcess.HasExited)
                KillProcessTree(state.TrainerProcess);
        }
        catch { }
        state.Dispose();
        _activeStates.Remove(filePath);
    }

    private static void KillProcessTree(Process proc)
    {
        try
        {
            proc.Kill();
            proc.WaitForExit(2000);
        }
        catch { }
    }

    private static Process? FindGameProcess(string exePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(exePath);
            return FindProcess(name, p =>
            {
                try { return p.MainModule?.FileName?.Equals(exePath, StringComparison.OrdinalIgnoreCase) == true; }
                catch { return false; }
            });
        }
        catch { return null; }
    }

    // 返回匹配的进程并释放其余 Process 句柄
    private static Process? FindProcess(string name, Func<Process, bool> predicate)
    {
        Process? match = null;
        foreach (var p in Process.GetProcessesByName(name))
        {
            if (match == null && predicate(p))
                match = p;
            else
                try { p.Dispose(); } catch { }
        }
        return match;
    }

    private sealed class RunningState : IDisposable
    {
        public Process TrainerProcess { get; set; } = null!;
        public Process GameProcess { get; set; } = null!;

        public void Dispose()
        {
            try { TrainerProcess?.Dispose(); } catch { }
            try { GameProcess?.Dispose(); } catch { }
        }
    }
}
