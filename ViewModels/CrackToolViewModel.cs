using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

// 免启动页：一键破解（脱壳+部署emu）/ 还原 / 更新emu；仅 SteamStub 游戏有效，Denuvo 无效
public partial class CrackToolViewModel : ObservableObject, IDisposable
{
    private readonly ICrackToolService _crackService;
    private readonly ISettingsService _settingsService;
    private bool _disposed;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _inputPath = string.Empty;

    [ObservableProperty]
    private string _appId = string.Empty;

    [ObservableProperty]
    private string _webApiKey = string.Empty;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = L.Get("Crack_Ready");

    [ObservableProperty]
    private bool _isEmuInstalled = true;

    [ObservableProperty]
    private string _emuButtonText = L.Get("Crack_UpdateEmu");

    [ObservableProperty]
    private string _emuVersionText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrackButtonText))]
    private bool _isUnpackOnly;

    // 勾上仅脱壳，一键破解按钮变为一键脱壳
    public string CrackButtonText => IsUnpackOnly ? L.Get("Crack_UnpackOne") : L.Get("Crack_CrackOne");

    private Timer? _statusMessageTimer;

    // 通知短暂展示后自动清空，跟主页逻辑一致
    partial void OnStatusMessageChanged(string value)
    {
        _statusMessageTimer?.Dispose();
        _statusMessageTimer = null;
        if (!string.IsNullOrEmpty(value))
        {
            _statusMessageTimer = new Timer(_ => Application.Current.Dispatcher.Invoke(() => StatusMessage = string.Empty),
                null, 3000, Timeout.Infinite);
        }
    }

    public ObservableCollection<string> LogLines { get; } = new();

    public CrackToolViewModel(ICrackToolService crackService, ISettingsService settingsService)
    {
        _crackService = crackService;
        _settingsService = settingsService;
        _webApiKey = _settingsService.Load().CrackWebApiKey ?? string.Empty;
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CrackButtonText));
        RefreshEmuStatus();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;
        CancelCrack();
        _statusMessageTimer?.Dispose();
        _statusMessageTimer = null;
    }

    public void CancelCrack()
    {
        try { _cts?.Cancel(); } catch { }
    }

    [RelayCommand]
    private void Cancel() => CancelCrack();

    // 切页清空由 MainWindow 调用
    public void ResetState()
    {
        CancelCrack();
        StatusMessage = L.Get("Crack_Ready");
    }

    // 进页刷新 emu 状态：没下载显示 下载EMU，下载了显示 更新EMU + 版本号
    public void OnNavigatedTo() => RefreshEmuStatus();

    private void RefreshEmuStatus()
    {
        try
        {
            IsEmuInstalled = _crackService.IsEmuInstalled();
            var ver = _crackService.GetEmuVersion();
            EmuButtonText = IsEmuInstalled ? L.Get("Crack_UpdateEmu") : L.Get("Crack_DownloadEmu");
            EmuVersionText = IsEmuInstalled && !string.IsNullOrEmpty(ver) ? L.Get("Crack_InstalledVer", ver) : L.Get("Crack_NotDownloaded");
        }
        catch
        {
            IsEmuInstalled = false;
            EmuButtonText = L.Get("Crack_DownloadEmu");
            EmuVersionText = L.Get("Crack_NotDownloaded");
        }
    }

    private void AddLog(string message)
    {
        LogService.Info(L.Get("LogCat_Crack"), message);
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (LogLines.Count > 500)
                LogLines.RemoveAt(0);
        });
    }

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    // Key 改动即存档，下次打开直接用
    partial void OnWebApiKeyChanged(string value)
    {
        try
        {
            var settings = _settingsService.Load();
            if (settings.CrackWebApiKey == (value ?? string.Empty)) return;
            settings.CrackWebApiKey = value ?? string.Empty;
            _settingsService.Save(settings);
        }
        catch { }
    }

    // 去 Steam 官方申请 Key
    [RelayCommand]
    private void OpenApiKeyPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://steamcommunity.com/dev/apikey") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Crack_OpenPageFailed", ex.Message);
        }
    }

    // 用目录末级名（多为游戏英文名）搜候选，选中回填 AppID
    [RelayCommand]
    private void QueryAppId()
    {
        var path = InputPath?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = L.Get("Crack_SelectDirFirst");
            return;
        }
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = L.Get("Crack_DirInvalid");
            return;
        }
        var win = new Views.AppIdQueryWindow(name);
        try
        {
            win.Owner = Application.Current.MainWindow;
            if (win.ShowDialog() == true && win.SelectedAppId is int appId)
            {
                AppId = appId.ToString();
                StatusMessage = L.Get("Crack_AppIdFilled", appId);
                AddLog(L.Get("Crack_AppIdSelected", appId));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Crack_QueryWindowFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        try
        {
            var mainWindow = Application.Current?.MainWindow;
            var owner = mainWindow == null
                ? IntPtr.Zero
                : new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
            var dir = FolderPicker.PickFolder(
                string.IsNullOrWhiteSpace(InputPath) ? null : InputPath, owner);
            if (!string.IsNullOrEmpty(dir))
                InputPath = dir;
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Crack_BrowseFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CrackAsync()
    {
        if (IsRunning)
        {
            CancelCrack();
            return;
        }
        // 仅脱壳不需要 AppID（AppID 只给 emu 信息生成用）
        if (!CheckInput(needAppId: !IsUnpackOnly)) return;
        var label = IsUnpackOnly ? L.Get("Crack_LabelUnpack") : L.Get("Crack_LabelCrack");
        await RunOpAsync(label, (log, ct) =>
            _crackService.CrackAsync(InputPath.Trim(), AppId?.Trim() ?? string.Empty, WebApiKey, log, ct, IsUnpackOnly));
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (IsRunning)
        {
            CancelCrack();
            return;
        }
        if (!CheckInput(needAppId: false)) return;
        await RunOpAsync(L.Get("Crack_LabelRestore"), (log, ct) =>
            _crackService.RestoreAsync(InputPath.Trim(), log, ct));
    }

    [RelayCommand]
    private async Task UpdateEmuAsync()
    {
        if (IsRunning)
        {
            CancelCrack();
            return;
        }
        CancelCrack();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        IsRunning = true;
        StatusMessage = L.Get("Crack_UpdatingEmu");
        AddLog(L.Get("Crack_UpdatingEmuLog"));
        var progress = new Progress<string>(msg => AddLog(msg));
        try
        {
            var ok = await _crackService.UpdateEmuAsync(progress, ct);
            StatusMessage = ok ? L.Get("Crack_EmuLatest") : L.Get("Crack_EmuUpdateFailed");
            if (ok) RefreshEmuStatus();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = L.Get("Crack_UpdateCancelled");
            AddLog(L.Get("Crack_UpdateCancelled"));
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Crack_UpdateError", ex.Message);
            AddLog(L.Get("Crack_UpdateError", ex.Message));
        }
        finally
        {
            IsRunning = false;
            if (ReferenceEquals(_cts, cts))
            {
                _cts?.Dispose();
                _cts = null;
            }
            else
            {
                cts.Dispose();
            }
        }
    }

    private bool CheckInput(bool needAppId)
    {
        var path = InputPath?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            StatusMessage = L.Get("Crack_SelectDirFirst");
            return false;
        }
        if (!Directory.Exists(path))
        {
            StatusMessage = L.Get("Crack_DirNotExist");
            return false;
        }
        if (needAppId && !uint.TryParse(AppId?.Trim(), out _))
        {
            StatusMessage = L.Get("Crack_AppIdRequired");
            return false;
        }
        return true;
    }

    private async Task RunOpAsync(string label, Func<IProgress<string>, CancellationToken, Task<bool>> op)
    {
        CancelCrack();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        IsRunning = true;
        StatusMessage = L.Get("Crack_RunningLabel", label);
        AddLog(L.Get("Crack_StartLabel", label, InputPath.Trim()));
        var progress = new Progress<string>(msg => AddLog(msg));
        try
        {
            var ok = await op(progress, ct);
            StatusMessage = ok ? L.Get("Crack_LabelDone", label) : L.Get("Crack_LabelFailed", label);
            AddLog(ok ? L.Get("Crack_LabelDone", label) : L.Get("Crack_LabelFailed", label));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = L.Get("Crack_CancelledLabel", label);
            AddLog(L.Get("Crack_CancelledLabel", label));
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Crack_LabelError", label, ex.Message);
            AddLog(L.Get("Crack_LabelError", label, ex.Message));
        }
        finally
        {
            IsRunning = false;
            if (ReferenceEquals(_cts, cts))
            {
                _cts?.Dispose();
                _cts = null;
            }
            else
            {
                cts.Dispose();
            }
        }
    }
}
