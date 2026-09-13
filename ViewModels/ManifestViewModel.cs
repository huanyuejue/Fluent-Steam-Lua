using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class ManifestViewModel : ObservableObject
{
    private readonly IManifestMonitorService _monitorService;
    private readonly ISettingsService _settingsService;
    private static readonly TimeSpan KeyValidity = TimeSpan.FromHours(24);
    private const string KeyEntropy = "ManifestHub";
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

    public bool HasSavedKey => !string.IsNullOrEmpty(_settingsService.Load().EncryptedManifestHubKey);

    public System.Collections.ObjectModel.ObservableCollection<string> LogLines { get; } = new();

    public ManifestViewModel(IManifestMonitorService monitorService, ISettingsService settingsService)
    {
        _monitorService = monitorService;
        _settingsService = settingsService;
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
        if (string.IsNullOrWhiteSpace(password))
        {
            StatusMessage = HasSavedKey ? "Key 未改动" : "请先输入 API Key";
            return;
        }
        var encrypted = SecureTokenStorage.Protect(password.Trim(), KeyEntropy);
        if (encrypted == null)
        {
            StatusMessage = "Key 保存失败（DPAPI 不可用）";
            return;
        }
        var settings = _settingsService.Load();
        settings.EncryptedManifestHubKey = encrypted;
        settings.ManifestHubKeyTime = DateTime.UtcNow.ToString("o");
        _settingsService.Save(settings);
        RefreshKeyStatus();
        StatusMessage = "API Key 已保存，有效期 24 小时";
        AppendLog("API Key 已保存");
    }

    [RelayCommand]
    private async Task StartMonitorAsync()
    {
        var key = LoadKey();
        if (string.IsNullOrEmpty(key))
        {
            StatusMessage = "请先填写并保存 API Key";
            return;
        }
        if (IsKeyExpired())
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

    private string? LoadKey()
    {
        var encrypted = _settingsService.Load().EncryptedManifestHubKey;
        if (string.IsNullOrEmpty(encrypted)) return null;
        return SecureTokenStorage.Unprotect(encrypted, KeyEntropy);
    }

    private bool IsKeyExpired()
    {
        return GetKeyExpiry() is not DateTime expiry || DateTime.UtcNow > expiry;
    }

    private DateTime? GetKeyExpiry()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(settings.EncryptedManifestHubKey)) return null;
        if (!DateTime.TryParse(settings.ManifestHubKeyTime, null, DateTimeStyles.RoundtripKind, out var saved))
            return null;
        return saved.ToUniversalTime().Add(KeyValidity);
    }

    private void RefreshKeyStatus()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrEmpty(settings.EncryptedManifestHubKey))
        {
            KeyStatusText = "未填写 API Key";
            KeyStatusForeground = "#888888";
            return;
        }
        var expiry = GetKeyExpiry();
        if (expiry == null)
        {
            KeyStatusText = "Key 时间无效，请重新填写";
            KeyStatusForeground = "#F44336";
            return;
        }
        if (DateTime.UtcNow > expiry)
        {
            KeyStatusText = "api key 已过期，请重新获取填入后使用";
            KeyStatusForeground = "#F44336";
            return;
        }
        KeyStatusText = $"有效期至 {expiry.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
        KeyStatusForeground = "#4CAF50";
    }
}
