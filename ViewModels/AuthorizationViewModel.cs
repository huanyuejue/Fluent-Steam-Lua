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
                LogService.Error(L.Get("LogCat_Auth"), text);
                break;
            case InfoBarSeverity.Warning:
                LogService.Warn(L.Get("LogCat_Auth"), text);
                break;
            default:
                LogService.Info(L.Get("LogCat_Auth"), text);
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
            SetStatus(InfoBarSeverity.Warning, L.Get("Auth_InvalidAppId"), L.Get("Auth_InvalidAppIdBody"));
            return;
        }

        IsExtracting = true;
        IsBusy = true;
        LastExtractPath = "";
        LastExtractSummary = "";
        SetStatus(InfoBarSeverity.Informational, L.Get("Auth_ExtractingTitle"),
            L.Get("Auth_ExtractingBody", appId));
        _extractCts = new CancellationTokenSource();
        var ct = _extractCts.Token;

        try
        {
            var result = await _authorizationService.ExtractAsync(appId, ct);
            LastExtractPath = result.OutputPath;
            LastExtractSummary =
                L.Get("Auth_SummaryTitle", result.Ticket.AppId) + "\n" +
                L.Get("Auth_SummaryAppTicket", result.Ticket.AppTicket.Length) + "\n" +
                L.Get("Auth_SummaryETicket", result.Ticket.ETicket.Length) + "\n" +
                L.Get("Auth_SummaryPath", result.OutputPath);
            SetStatus(InfoBarSeverity.Success, L.Get("Auth_DoneTitle"),
                L.Get("Auth_ExtractSavedNote"));
        }
        catch (OperationCanceledException)
        {
            SetStatus(InfoBarSeverity.Informational, L.Get("Auth_CancelledTitle"), L.Get("Auth_CancelledBody"));
        }
        catch (Exception ex)
        {
            SetStatus(InfoBarSeverity.Error, L.Get("Auth_ExtractFailed"), ex.Message);
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
            SetStatus(InfoBarSeverity.Informational, L.Get("Auth_PathCopied"), L.Get("Auth_PathCopiedBody"));
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
            Title = L.Get("Auth_PickTickets"),
            Filter = L.Get("Auth_TicketsFilter"),
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
            SetStatus(InfoBarSeverity.Warning, L.Get("Auth_NoFile"), L.Get("Auth_NoFileBody"));
            return;
        }
        if (IsImporting || IsExtracting)
        {
            SetStatus(InfoBarSeverity.Informational, L.Get("Auth_Processing"), L.Get("Auth_ProcessingBody"));
            return;
        }

        IsImporting = true;
        IsBusy = true;
        LastImportSummary = "";
        SetStatus(InfoBarSeverity.Informational, L.Get("Auth_ImportingTitle"), L.Get("Auth_ImportingBody", Path.GetFileName(path)));
        try
        {
            var parse = _authorizationService.ParseTicketsFile(path);
            if (!parse.Ok)
            {
                SetStatus(InfoBarSeverity.Error, L.Get("Auth_ParseFailed"), parse.Error ?? L.Get("Auth_UnknownParseError"));
                return;
            }

            SetStatus(InfoBarSeverity.Informational, L.Get("Auth_CheckingManifest"),
                L.Get("Auth_CheckingManifestBody", parse.AppId));

            var check = _authorizationService.CheckLuaManifest(parse.AppId);
            switch (check.Status)
            {
                case LuaManifestCheckStatus.NotConfigured:
                    SetStatus(InfoBarSeverity.Error, L.Get("Auth_NoManifestDir"), check.Message ?? L.Get("Auth_NoManifestDirBody"));
                    return;
                case LuaManifestCheckStatus.NotFound:
                    SetStatus(InfoBarSeverity.Warning, L.Get("Auth_GameNotImported"), check.Message ?? L.Get("Auth_GameNotImportedBody"));
                    return;
                case LuaManifestCheckStatus.DisabledOnly:
                    SetStatus(InfoBarSeverity.Warning, L.Get("Auth_ManifestDisabled"), check.Message ?? L.Get("Auth_ManifestDisabledBody"));
                    return;
                case LuaManifestCheckStatus.NoAddAppId:
                    SetStatus(InfoBarSeverity.Error, L.Get("Auth_NoAddAppId"), check.Message ?? L.Get("Auth_NoAddAppIdBody"));
                    return;
            }

            if (check.HasLegacyTicketLines && !string.IsNullOrEmpty(check.LuaFilePath))
            {
                var confirmed = await ConfirmRemoveLegacyAsync(parse.AppId);
                if (!confirmed)
                {
                    SetStatus(InfoBarSeverity.Informational, L.Get("Auth_CancelledTitle"),
                        L.Get("Auth_CancelledImportBody"));
                    return;
                }

                var removeError = _authorizationService.RemoveLegacyTicketLines(check.LuaFilePath, parse.AppId);
                if (removeError != null)
                {
                    SetStatus(InfoBarSeverity.Error, L.Get("Auth_RemoveLegacyFailed"), removeError);
                    return;
                }
            }

            var ticket = new TicketData(parse.AppId, parse.SteamId, parse.AppTicket!, parse.ETicket!, path);
            var import = _authorizationService.ImportTicket(ticket);
            var warning = string.IsNullOrEmpty(import.Warning) ? "" : $"\n\n{import.Warning}";
            LastImportSummary =
                L.Get("Auth_ImportSummaryAppId", import.AppId) + "\n" +
                L.Get("Auth_SummaryAppTicket", import.AppTicketBytes) + "\n" +
                L.Get("Auth_SummaryETicket", import.ETicketBytes) + "\n" +
                L.Get("Auth_ManifestActive");
            SetStatus(InfoBarSeverity.Success, L.Get("Auth_ImportOk"),
                L.Get("Auth_ImportOkBody", import.AppId, import.AppTicketBytes, import.ETicketBytes) + warning);
        }
        catch (Exception ex)
        {
            SetStatus(InfoBarSeverity.Error, L.Get("Auth_ImportFailed"), ex.Message);
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
            L.Get("Auth_LegacyTitle"),
            L.Get("Auth_LegacyBody", appId),
            L.Get("Auth_RemoveAndContinue"), L.Get("Common_Cancel"));
    }
}
