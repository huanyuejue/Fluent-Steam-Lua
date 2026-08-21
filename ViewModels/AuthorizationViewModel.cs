using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Win32;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class AuthorizationViewModel : ObservableObject, IDisposable
{
    private readonly IAuthorizationService _authorizationService;
    private readonly IDialogService _dialogService;
    private readonly DispatcherTimer _statusTimer;
    private CancellationTokenSource? _extractCts;
    private bool _disposed;

    public AuthorizationViewModel(IAuthorizationService authorizationService, IDialogService dialogService)
    {
        _authorizationService = authorizationService;
        _dialogService = dialogService;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += OnStatusTimerTick;
    }

    private void OnStatusTimerTick(object? sender, EventArgs e) => StatusOpen = false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _extractCts?.Cancel();
        _extractCts?.Dispose();
        _extractCts = null;
    }

    // ===== 状态区 =====
    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private string _statusTitle = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _statusOpen;

    private void SetStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        StatusOpen = true;
        _statusTimer.Stop();
        _statusTimer.Start();

        var text = string.IsNullOrEmpty(title) ? message : $"{title}：{message}";
        switch (severity)
        {
            case InfoBarSeverity.Error:
                LogService.Error("授权", text);
                break;
            case InfoBarSeverity.Warning:
                LogService.Warn("授权", text);
                break;
            default:
                LogService.Info("授权", text);
                break;
        }
    }

    // ===== 提取区 =====
    [ObservableProperty]
    private string _extractAppIdText = "";

    [ObservableProperty]
    private bool _isExtracting;

    /// <summary>提取或导入进行中（任一操作进行时锁定输入与拖放区）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _lastExtractPath = "";

    [ObservableProperty]
    private string _lastExtractSummary = "";

    [RelayCommand]
    private async Task ExtractAsync()
    {
        if (IsExtracting)
        {
            CancelExtract();
            return;
        }

        var id = ExtractAppIdText.Trim();
        if (!uint.TryParse(id, out var appId) || appId == 0)
        {
            SetStatus(InfoBarSeverity.Warning, "AppID 无效", "请输入 1~4294967295 之间的数字 AppID");
            return;
        }

        IsExtracting = true;
        IsBusy = true;
        LastExtractPath = "";
        LastExtractSummary = "";
        SetStatus(InfoBarSeverity.Informational, "正在提取",
            $"正在提取 AppID {appId} 的授权票据…请确认 Steam 已运行并登录拥有该游戏的账号");
        _extractCts = new CancellationTokenSource();
        var ct = _extractCts.Token;

        try
        {
            var result = await _authorizationService.ExtractAsync(appId, ct);
            LastExtractPath = result.OutputPath;
            LastExtractSummary =
                $"已提取 AppID {result.Ticket.AppId} 授权\n" +
                $"AppTicket：{result.Ticket.AppTicket.Length} 字节\n" +
                $"ETicket：{result.Ticket.ETicket.Length} 字节\n" +
                $"保存位置：{result.OutputPath}";
            SetStatus(InfoBarSeverity.Success, "提取完成",
                "已提取授权票据并保存。授权文件有效期为 30 分钟，请尽快使用。");
        }
        catch (OperationCanceledException)
        {
            SetStatus(InfoBarSeverity.Informational, "已取消", "提取已取消");
        }
        catch (Exception ex)
        {
            SetStatus(InfoBarSeverity.Error, "提取失败", ex.Message);
        }
        finally
        {
            _extractCts?.Dispose();
            _extractCts = null;
            IsExtracting = false;
            IsBusy = false;
        }
    }

    public void CancelExtract()
    {
        _extractCts?.Cancel();
    }

    [RelayCommand]
    private void CopyExtractPath()
    {
        if (string.IsNullOrEmpty(LastExtractPath)) return;
        try
        {
            Clipboard.SetText(LastExtractPath);
            SetStatus(InfoBarSeverity.Informational, "路径已复制", "tickets.txt 的保存路径已复制到剪贴板");
        }
        catch { }
    }

    [RelayCommand]
    private void OpenExtractFolder()
    {
        if (string.IsNullOrEmpty(LastExtractPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(LastExtractPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
        }
        catch { }
    }

    // ===== 导入区 =====
    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private string _lastImportSummary = "";

    [RelayCommand]
    private void BrowseImport()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 tickets.txt",
            Filter = "票据文件|tickets.txt;*.txt|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            _ = ImportFileAsync(dialog.FileName);
        }
    }

    [RelayCommand]
    private async Task ImportFileAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus(InfoBarSeverity.Warning, "未选择文件", "请拖入或选择有效的 tickets.txt 文件");
            return;
        }
        if (IsImporting || IsExtracting)
        {
            SetStatus(InfoBarSeverity.Informational, "正在处理", "存在进行中的操作，请稍候再试");
            return;
        }

        IsImporting = true;
        IsBusy = true;
        LastImportSummary = "";
        SetStatus(InfoBarSeverity.Informational, "正在导入", $"正在解析 {Path.GetFileName(path)}…");
        try
        {
            var parse = _authorizationService.ParseTicketsFile(path);
            if (!parse.Ok)
            {
                SetStatus(InfoBarSeverity.Error, "文件解析失败", parse.Error ?? "未知解析错误");
                return;
            }

            SetStatus(InfoBarSeverity.Informational, "正在校验清单",
                $"正在检查 AppID {parse.AppId} 的 Lua 清单入库状态…");

            var check = _authorizationService.CheckLuaManifest(parse.AppId);
            switch (check.Status)
            {
                case LuaManifestCheckStatus.NotConfigured:
                    SetStatus(InfoBarSeverity.Error, "未配置 Lua 清单目录", check.Message ?? "未检测到 Lua 清单目录");
                    return;
                case LuaManifestCheckStatus.NotFound:
                    SetStatus(InfoBarSeverity.Warning, "游戏未入库", check.Message ?? "该授权只适用于通过 Lua 清单入库的游戏");
                    return;
                case LuaManifestCheckStatus.DisabledOnly:
                    SetStatus(InfoBarSeverity.Warning, "清单已禁用", check.Message ?? "清单已被禁用，请先启用");
                    return;
                case LuaManifestCheckStatus.NoAddAppId:
                    SetStatus(InfoBarSeverity.Error, "缺少 addappid 配置", check.Message ?? "Lua 清单中缺少 addappid 入库配置");
                    return;
            }

            if (check.HasLegacyTicketLines && !string.IsNullOrEmpty(check.LuaFilePath))
            {
                var confirmed = await ConfirmRemoveLegacyAsync(parse.AppId);
                if (!confirmed)
                {
                    SetStatus(InfoBarSeverity.Informational, "已取消",
                        "已停止导入，旧授权语句未移除。");
                    return;
                }

                var removeError = _authorizationService.RemoveLegacyTicketLines(check.LuaFilePath, parse.AppId);
                if (removeError != null)
                {
                    SetStatus(InfoBarSeverity.Error, "清理旧授权语句失败", removeError);
                    return;
                }
            }

            var ticket = new TicketData(parse.AppId, parse.SteamId, parse.AppTicket!, parse.ETicket!, path);
            var import = _authorizationService.ImportTicket(ticket);
            var warning = string.IsNullOrEmpty(import.Warning) ? "" : $"\n\n{import.Warning}";
            LastImportSummary =
                $"AppID：{import.AppId}\n" +
                $"AppTicket：{import.AppTicketBytes} 字节\n" +
                $"ETicket：{import.ETicketBytes} 字节\n" +
                $"清单状态：已检测到生效 Lua 清单";
            SetStatus(InfoBarSeverity.Success, "授权导入成功",
                $"AppID {import.AppId} 的 AppTicket（{import.AppTicketBytes} 字节）和 ETicket（{import.ETicketBytes} 字节）" +
                $"已写入 Steam 凭据存储，现有旧票据已由本次导入的新票据覆盖。授权有效期约 30 分钟，请尽快启动游戏完成授权。" + warning);
        }
        catch (Exception ex)
        {
            SetStatus(InfoBarSeverity.Error, "导入失败", ex.Message);
        }
        finally
        {
            IsImporting = false;
            IsBusy = false;
        }
    }

    private async Task<bool> ConfirmRemoveLegacyAsync(uint appId)
    {
        return await _dialogService.ShowConfirmAsync(
            "检测到旧授权语句",
            $"Lua 清单中仍包含 AppID {appId} 的旧 setAppTicket/setETicket 语句。" +
            "继续使用注册表授权前需要移除这些语句，否则 Steam 重启或 Lua 热重载后会将其写回，覆盖刚导入的新授权。",
            "移除并继续", "取消");
    }
}
