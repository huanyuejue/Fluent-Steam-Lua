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
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _isEmuInstalled = true;

    [ObservableProperty]
    private string _emuButtonText = "更新EMU";

    [ObservableProperty]
    private string _emuVersionText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrackButtonText))]
    private bool _isUnpackOnly;

    // 勾上仅脱壳，一键破解按钮变为一键脱壳
    public string CrackButtonText => IsUnpackOnly ? "一键脱壳" : "一键破解";

    private Timer? _statusMessageTimer;

    // 通知 3 秒自动消失，跟主页逻辑一致
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
        StatusMessage = "就绪";
    }

    // 进页刷新 emu 状态：没下载显示 下载EMU，下载了显示 更新EMU + 版本号
    public void OnNavigatedTo() => RefreshEmuStatus();

    private void RefreshEmuStatus()
    {
        try
        {
            IsEmuInstalled = _crackService.IsEmuInstalled();
            var ver = _crackService.GetEmuVersion();
            EmuButtonText = IsEmuInstalled ? "更新EMU" : "下载EMU";
            EmuVersionText = IsEmuInstalled && !string.IsNullOrEmpty(ver) ? $"已安装：{ver}" : "未下载";
        }
        catch
        {
            IsEmuInstalled = false;
            EmuButtonText = "下载EMU";
            EmuVersionText = "未下载";
        }
    }

    private void AddLog(string message)
    {
        LogService.Info("免启动", message);
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
            StatusMessage = $"打不开申请页面：{ex.Message}";
        }
    }

    // 用目录末级名（多为游戏英文名）搜候选，选中回填 AppID
    [RelayCommand]
    private void QueryAppId()
    {
        var path = InputPath?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "请先选择游戏目录";
            return;
        }
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "目录名无效，无法搜索";
            return;
        }
        var win = new Views.AppIdQueryWindow(name);
        try
        {
            win.Owner = Application.Current.MainWindow;
            if (win.ShowDialog() == true && win.SelectedAppId is int appId)
            {
                AppId = appId.ToString();
                StatusMessage = $"已填入 AppID：{appId}";
                AddLog($"已选定 AppID：{appId}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开查询窗口失败：{ex.Message}";
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
            StatusMessage = $"打开目录选择失败：{ex.Message}";
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
        var label = IsUnpackOnly ? "脱壳" : "破解";
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
        await RunOpAsync("还原", (log, ct) =>
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
        StatusMessage = "正在更新 Goldberg emu…";
        AddLog("开始更新 Goldberg emu（走 GitHub，需网络）");
        var progress = new Progress<string>(msg => AddLog(msg));
        try
        {
            var ok = await _crackService.UpdateEmuAsync(progress, ct);
            StatusMessage = ok ? "emu 已是最新" : "emu 更新失败，详见日志";
            if (ok) RefreshEmuStatus();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消更新";
            AddLog("已取消更新");
        }
        catch (Exception ex)
        {
            StatusMessage = $"更新异常：{ex.Message}";
            AddLog($"更新异常：{ex.Message}");
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
            StatusMessage = "请先选择游戏目录";
            return false;
        }
        if (!Directory.Exists(path))
        {
            StatusMessage = "目录不存在，请重新选择";
            return false;
        }
        if (needAppId && !uint.TryParse(AppId?.Trim(), out _))
        {
            StatusMessage = "AppID 必填且必须为正整数";
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
        StatusMessage = $"正在{label}…";
        AddLog($"开始{label}：{InputPath.Trim()}");
        var progress = new Progress<string>(msg => AddLog(msg));
        try
        {
            var ok = await op(progress, ct);
            StatusMessage = ok ? $"{label}完成" : $"{label}失败，详见日志";
            AddLog(ok ? $"{label}完成" : $"{label}失败，详见日志");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"已取消{label}";
            AddLog($"已取消{label}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"{label}异常：{ex.Message}";
            AddLog($"{label}异常：{ex.Message}");
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
