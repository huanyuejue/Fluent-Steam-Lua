using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class ManifestViewModel : ObservableObject
{
    private readonly HubManifestMonitorService _hubMonitor;
    private readonly CodeManifestMonitorService _codeMonitor;
    private readonly IManifestHubKeyService _keyService;
    private const int MaxLogLines = 500;

    // 页签：默认 Code 直下
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingHub))]
    private bool _isShowingCode = true;
    public bool IsShowingHub => !IsShowingCode;

    // Hub 页签状态
    [ObservableProperty]
    private bool _isHubMonitoring;

    [ObservableProperty]
    private string _keyStatusText = "未填写 API Key";

    [ObservableProperty]
    private string _keyStatusForeground = "#888888";

    [ObservableProperty]
    private int _hubFoundCount;

    [ObservableProperty]
    private int _hubSuccessCount;

    [ObservableProperty]
    private string _hubStatusMessage = string.Empty;

    // Code 页签状态
    [ObservableProperty]
    private bool _isCodeMonitoring;

    [ObservableProperty]
    private int _codeFoundCount;

    [ObservableProperty]
    private int _codeSuccessCount;

    [ObservableProperty]
    private string _codeStatusMessage = string.Empty;

    public bool HasSavedKey => _keyService.HasSavedKey;

    public System.Collections.ObjectModel.ObservableCollection<string> HubLogLines { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<string> CodeLogLines { get; } = new();

    public ManifestViewModel(
        HubManifestMonitorService hubMonitor,
        CodeManifestMonitorService codeMonitor,
        IManifestHubKeyService keyService)
    {
        _hubMonitor = hubMonitor;
        _codeMonitor = codeMonitor;
        _keyService = keyService;
        _hubMonitor.Log += m => Application.Current.Dispatcher.InvokeAsync(() => AppendLog(HubLogLines, m));
        _hubMonitor.RequestCompleted += (_, ok) => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            HubFoundCount++;
            if (ok) HubSuccessCount++;
        });
        _codeMonitor.Log += m => Application.Current.Dispatcher.InvokeAsync(() => AppendLog(CodeLogLines, m));
        _codeMonitor.RequestCompleted += (_, ok) => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            CodeFoundCount++;
            if (ok) CodeSuccessCount++;
        });
        RefreshKeyStatus();
    }

    private static void AppendLog(System.Collections.ObjectModel.ObservableCollection<string> lines, string message)
    {
        lines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (lines.Count > MaxLogLines)
            lines.RemoveAt(0);
    }

    private void AppendHubLog(string message) => AppendLog(HubLogLines, message);
    private void AppendCodeLog(string message) => AppendLog(CodeLogLines, message);

    /// <summary>切到本页时刷新：默认 Code 页签，Key 状态与监听开关回显。</summary>
    public void OnNavigatedTo()
    {
        IsShowingCode = true;
        RefreshKeyStatus();
        IsHubMonitoring = _hubMonitor.IsRunning;
        IsCodeMonitoring = _codeMonitor.IsRunning;
    }

    [RelayCommand]
    private void ShowCodeView() => SwitchTab(true);

    [RelayCommand]
    private void ShowHubView() => SwitchTab(false);

    private void SwitchTab(bool showCode)
    {
        IsShowingCode = showCode;
    }

    [RelayCommand]
    private void SaveApiKey(string? password)
    {
        // 界面用假内容占位时传 null：有存量 Key 就是"未改动"，没有才是"没填"
        var (ok, message) = _keyService.SaveKey(password);
        HubStatusMessage = message;
        if (!ok) return;
        RefreshKeyStatus();
        AppendHubLog("API Key 已保存");
    }

    // 双监听互斥：启动一个先停另一个
    [RelayCommand]
    private async Task StartHubMonitorAsync()
    {
        var key = _keyService.LoadKey();
        if (string.IsNullOrEmpty(key))
        {
            HubStatusMessage = "请先填写并保存 API Key";
            return;
        }
        if (_keyService.IsKeyExpired())
        {
            HubStatusMessage = "API Key 已过期，请重新获取填入后使用";
            return;
        }
        StopCodeSilently();
        var (ok, error) = await _hubMonitor.StartAsync();
        IsHubMonitoring = ok;
        HubStatusMessage = ok ? "清单监听已启动（ManifestHub）" : error ?? "启动失败";
        AppendHubLog(HubStatusMessage);
    }

    [RelayCommand]
    private async Task StartCodeMonitorAsync()
    {
        StopHubSilently();
        var (ok, error) = await _codeMonitor.StartAsync();
        IsCodeMonitoring = ok;
        CodeStatusMessage = ok ? "清单监听已启动（Code直下）" : error ?? "启动失败";
        AppendCodeLog(CodeStatusMessage);
    }

    [RelayCommand]
    private void StopHubMonitor()
    {
        _hubMonitor.Stop();
        IsHubMonitoring = false;
        HubStatusMessage = "清单监听已停止";
        AppendHubLog(HubStatusMessage);
    }

    [RelayCommand]
    private void StopCodeMonitor()
    {
        _codeMonitor.Stop();
        IsCodeMonitoring = false;
        CodeStatusMessage = "清单监听已停止";
        AppendCodeLog(CodeStatusMessage);
    }

    private void StopHubSilently()
    {
        if (!_hubMonitor.IsRunning) return;
        _hubMonitor.Stop();
        IsHubMonitoring = false;
        AppendHubLog("已自动停止 ManifestHub 监听（双监听互斥）");
    }

    private void StopCodeSilently()
    {
        if (!_codeMonitor.IsRunning) return;
        _codeMonitor.Stop();
        IsCodeMonitoring = false;
        AppendCodeLog("已自动停止 Code 监听（双监听互斥）");
    }

    [RelayCommand]
    private void ClearHubLog()
    {
        HubLogLines.Clear();
    }

    [RelayCommand]
    private void ClearCodeLog()
    {
        CodeLogLines.Clear();
    }

    private void RefreshKeyStatus()
    {
        KeyStatusText = _keyService.GetKeyStatusText();
        KeyStatusForeground = _keyService.GetKeyStatusColor();
    }
}
