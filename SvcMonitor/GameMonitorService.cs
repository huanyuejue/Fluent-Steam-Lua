using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SvcMonitor.Models;

namespace SvcMonitor;

public class GameMonitorService : BackgroundService
{
    private readonly ILogger<GameMonitorService> _logger;
    private List<TrainerBinding> _bindings = new();
    private readonly Dictionary<string, Process> _activeTrainers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRead = DateTime.MinValue;

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteamLuaManager", "bindings.json");

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
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动修改器失败: {Name}", binding.TrainerDisplayName);
        }
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
