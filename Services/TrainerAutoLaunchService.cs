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

    public event Action<string>? StatusChanged;

    public TrainerAutoLaunchService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public void Start()
    {
        _bindings = _settingsService.Load().TrainerBindings ?? new();
        _cts = new CancellationTokenSource();
        _ = PollingLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _pendingLaunches.Clear();
        lock (_lock)
        {
            foreach (var state in _activeStates.Values)
            {
                if (!state.TrainerProcess.HasExited)
                    KillProcessTree(state.TrainerProcess);
            }
            _activeStates.Clear();
        }
    }

    public void ReloadBindings()
    {
        _bindings = _settingsService.Load().TrainerBindings ?? new();
    }

    public void Dispose() => Stop();

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
            catch { }
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
                    st.GameProcess = gameProc;
            }
        }

        lock (_lock)
        {
            foreach (var key in _activeStates.Keys.ToList())
            {
                if (!tracked.Contains(key))
                {
                    if (!_activeStates[key].TrainerProcess.HasExited)
                        KillProcessTree(_activeStates[key].TrainerProcess);
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
            if (!_pendingLaunches.Add(binding.TrainerFilePath)) return;

            var procName = Path.GetFileNameWithoutExtension(binding.TrainerFilePath);

            var existing = Process.GetProcessesByName(procName)
                .FirstOrDefault(p => !p.HasExited);
            if (existing != null)
            {
                _pendingLaunches.Remove(binding.TrainerFilePath);
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
                        realProc = Process.GetProcessesByName(procName)
                            .FirstOrDefault(p => !p.HasExited);
                        if (realProc != null) break;
                    }

                    _pendingLaunches.Remove(binding.TrainerFilePath);

                    if (realProc == null)
                    {
                        StatusChanged?.Invoke($"修改器启动超时: {capturedBinding.TrainerDisplayName}");
                        return;
                    }

                    ActivateTrainer(realProc, gameProc, capturedBinding.TrainerFilePath);
                }
                catch (Exception ex)
                {
                    _pendingLaunches.Remove(binding.TrainerFilePath);
                    StatusChanged?.Invoke($"启动修改器失败: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            _pendingLaunches.Remove(binding.TrainerFilePath);
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
        _pendingLaunches.Remove(filePath);
        if (!_activeStates.TryGetValue(filePath, out var state)) return;
        try
        {
            if (!state.TrainerProcess.HasExited)
                KillProcessTree(state.TrainerProcess);
        }
        catch { }
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
            return Process.GetProcessesByName(name)
                .FirstOrDefault(p =>
                {
                    try { return p.MainModule?.FileName?.Equals(exePath, StringComparison.OrdinalIgnoreCase) == true; }
                    catch { return false; }
                });
        }
        catch { return null; }
    }

    private class RunningState
    {
        public Process TrainerProcess { get; set; } = null!;
        public Process GameProcess { get; set; } = null!;
    }
}
