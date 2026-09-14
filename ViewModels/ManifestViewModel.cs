using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class ManifestViewModel : ObservableObject
{
    private readonly IManifestMonitorService _monitorService;
    private readonly IManifestHubKeyService _keyService;
    private const int MaxLogLines = 500;

    [ObservableProperty]
    private bool _isMonitoring;

    [ObservableProperty]
    private string _keyStatusText = "未填写 API Key";

    [ObservableProperty]
    private string _keyStatusForeground = "#888888";

    [ObservableProperty]
    private int _foundCount;

    [ObservableProperty]
    private int _successCount;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool HasSavedKey => _keyService.HasSavedKey;

    public System.Collections.ObjectModel.ObservableCollection<string> LogLines { get; } = new();

    public ManifestViewModel(IManifestMonitorService monitorService, IManifestHubKeyService keyService)
    {
        _monitorService = monitorService;
        _keyService = keyService;
        _monitorService.Log += OnMonitorLog;
        _monitorService.RequestCompleted += OnRequestCompleted;
        RefreshKeyStatus();
    }

    private void OnMonitorLog(string message)
    {
        Application.Current.Dispatcher.InvokeAsync(() => AppendLog(message));
    }

    private void OnRequestCompleted(ManifestRequest req, bool ok)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            FoundCount++;
            if (ok) SuccessCount++;
        });
    }

    private void AppendLog(string message)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogLines.Count > MaxLogLines)
            LogLines.RemoveAt(0);
    }

    /// <summary>切到本页时刷新：Key 状态与监听开关回显。</summary>
    public void OnNavigatedTo()
    {
        RefreshKeyStatus();
        IsMonitoring = _monitorService.IsRunning;
    }

    [RelayCommand]
    private void SaveApiKey(string? password)
    {
        // 界面用假内容占位时传 null：有存量 Key 就是"未改动"，没有才是"没填"
        var (ok, message) = _keyService.SaveKey(password);
        StatusMessage = message;
        if (!ok) return;
        RefreshKeyStatus();
        AppendLog("API Key 已保存");
    }

    [RelayCommand]
    private async Task StartMonitorAsync()
    {
        var key = _keyService.LoadKey();
        if (string.IsNullOrEmpty(key))
        {
            StatusMessage = "请先填写并保存 API Key";
            return;
        }
        if (_keyService.IsKeyExpired())
        {
            StatusMessage = "API Key 已过期，请重新获取填入后使用";
            return;
        }
        var (ok, error) = await _monitorService.StartAsync(key);
        IsMonitoring = ok;
        StatusMessage = ok ? "清单监听已启动" : error ?? "启动失败";
        AppendLog(StatusMessage);
    }

    [RelayCommand]
    private void StopMonitor()
    {
        _monitorService.Stop();
        IsMonitoring = false;
        StatusMessage = "清单监听已停止";
        AppendLog(StatusMessage);
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
    }

    private void RefreshKeyStatus()
    {
        KeyStatusText = _keyService.GetKeyStatusText();
        KeyStatusForeground = _keyService.GetKeyStatusColor();
    }
}
