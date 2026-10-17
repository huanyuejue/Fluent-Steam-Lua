using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class OnlineFixViewModel : ObservableObject, IDisposable
{
    private readonly IOnlineFixService _onlineFixService;
    private readonly IDialogService _dialogService;
    private bool _disposed;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _queryCts;

    [ObservableProperty]
    private string _searchText = string.Empty;

    // Pill 视图切换：默认 480 联机
    [ObservableProperty]
    private bool _isShowing480 = true;

    [ObservableProperty]
    private bool _isShowingPatch;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private bool _isSearchEmptyVisible;

    [ObservableProperty]
    private bool _isLoadingDetail;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

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

    public ObservableCollection<OnlineFixSearchResult> SearchResults { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private OnlineFixDetail? _detail;
    public OnlineFixDetail? Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(IsDetailVisible));
                OnPropertyChanged(nameof(HasFiles));
                RefreshSearchEmptyVisible();
            }
        }
    }

    public bool IsDetailVisible => Detail != null;
    public bool HasFiles => Detail != null && Detail.Files.Count > 0;

    public OnlineFixViewModel(IOnlineFixService onlineFixService, IDialogService dialogService)
    {
        _onlineFixService = onlineFixService;
        _dialogService = dialogService;
        SearchResults.CollectionChanged += (_, _) => RefreshSearchEmptyVisible();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelQuery();
        CancelDownload();
        try { _downloadCts?.Dispose(); } catch { }
        _downloadCts = null;
    }

    // 同步命令 CanExecute 恒 true：下载中取消按钮必须可点，不能复用异步的 DownloadCommand
    //（AsyncRelayCommand 执行期间 CanExecute 自动为 false，会把取消按钮一并禁用）
    [RelayCommand]
    public void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { }
    }

    private void CancelQuery()
    {
        try { _queryCts?.Cancel(); } catch { }
        try { _queryCts?.Dispose(); } catch { }
        _queryCts = null;
    }

    private void RefreshSearchEmptyVisible()
        => IsSearchEmptyVisible = HasSearched && SearchResults.Count == 0 && !IsSearching && Detail == null;

    // 切页清空由 MainWindow 调用
    public void ResetState()
    {
        CancelQuery();
        CancelDownload();
        HasSearched = false;
        Detail = null;
        RefreshSearchEmptyVisible();
    }

    private void AddLog(string message)
    {
        LogService.Info(L.Get("LogCat_Online"), message);
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (LogLines.Count > 500)
                LogLines.RemoveAt(0);
        });
    }

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    [RelayCommand]
    private void Show480View()
    {
        IsShowing480 = true;
        IsShowingPatch = false;
    }

    [RelayCommand]
    private void ShowPatchView()
    {
        IsShowing480 = false;
        IsShowingPatch = true;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = SearchText?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            StatusMessage = L.Get("Online_EnterGame");
            return;
        }
        if (IsSearching) return;
        CancelQuery();
        _queryCts = new CancellationTokenSource();
        var ct = _queryCts.Token;
        IsSearching = true;
        SearchResults.Clear();
        LogLines.Clear();
        Detail = null;
        StatusMessage = "";
        AddLog(L.Get("Online_Searching", query));
        try
        {
            var results = await _onlineFixService.SearchAsync(query, ct);
            foreach (var r in results)
                SearchResults.Add(r);
            StatusMessage = results.Count == 0 ? L.Get("Online_NoMatch") : L.Get("Online_FoundCount", results.Count);
            AddLog(results.Count == 0 ? L.Get("Online_NoMatch") : L.Get("Online_FoundCount", results.Count));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StatusMessage = L.Get("Online_SearchCancelled");
        }
        catch (OperationCanceledException)
        {
            // HttpClient 超时也是 OCE，但此时并未取消——源站境外，基本都是裸连太慢
            StatusMessage = L.Get("Online_SearchTimeout");
            AddLog(L.Get("Online_SearchTimeoutLog"));
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Online_SearchFailed", ex.Message);
            AddLog(L.Get("Online_SearchFailed", ex.Message));
        }
        finally
        {
            IsSearching = false;
            HasSearched = true;
            RefreshSearchEmptyVisible();
        }
    }

    [RelayCommand]
    private async Task LoadDetailAsync(OnlineFixSearchResult? entry)
    {
        if (entry == null || IsLoadingDetail) return;
        CancelQuery();
        _queryCts = new CancellationTokenSource();
        var ct = _queryCts.Token;
        IsLoadingDetail = true;
        StatusMessage = L.Get("Online_ReadingDetail");
        AddLog(L.Get("Online_ReadingDetailLog", entry.Title));
        try
        {
            Detail = await _onlineFixService.GetDetailAsync(entry.Url, ct);
            if (Detail == null)
            {
                StatusMessage = L.Get("Online_DetailParseFailed");
                AddLog(L.Get("Online_DetailNoDirect"));
            }
            else
            {
                // 进详情即清结果列表，只看当前游戏
                SearchResults.Clear();
                StatusMessage = Detail.Files.Count > 0
                    ? L.Get("Online_DetailLoaded", Detail.Files.Count)
                    : L.Get("Online_DetailNoFiles");
                AddLog(L.Get("Online_DetailLoadedLog", Detail.Title, Detail.Files.Count));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StatusMessage = L.Get("Online_Cancelled");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = L.Get("Online_DetailTimeout");
            AddLog(L.Get("Online_DetailTimeoutLog"));
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Online_DetailFailed", ex.Message);
            AddLog(L.Get("Online_DetailFailed", ex.Message));
        }
        finally
        {
            IsLoadingDetail = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync(OnlineFixFileEntry? file)
    {
        // 下载中点取消（取消按钮传 null）；平时按文件下载
        if (IsDownloading)
        {
            CancelDownload();
            return;
        }
        if (file == null || Detail == null) return;
        // 下载全程只认快照：中途再搜索会把 Detail 置空，直接读属性必 NRE
        var detail = Detail;
        CancelDownload();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        var ct = cts.Token;

        var dir = Path.Combine(AppContext.BaseDirectory, "cache", "onlinefix", detail.GameDirName);
        var dest = Path.Combine(dir, file.Name);
        if (File.Exists(dest))
        {
            var overwrite = await _dialogService.ShowConfirmAsync(
                L.Get("Online_OverwriteTitle"), L.Get("Online_OverwriteBody", file.Name), L.Get("Online_Overwrite"), L.Get("Common_Cancel"));
            if (!overwrite) return;
        }

        IsDownloading = true;
        DownloadProgress = 0;
        StatusMessage = L.Get("Online_Downloading", file.Name);
        AddLog(L.Get("Online_DownloadStart", file.Name));
        try
        {
            Directory.CreateDirectory(dir);
            var progress = new Progress<double>(p => DownloadProgress = p);
            await _onlineFixService.DownloadFileAsync(file.Url, detail.UploadsBaseUrl + "Fix%20Repair/", dest, progress, ct);
            await File.WriteAllTextAsync(BuildInstructionPath(dir), BuildInstructionText(detail), ct);
            StatusMessage = L.Get("Online_DownloadDone", file.Name);
            AddLog(L.Get("Online_DownloadSaved", dest));
            var open = await _dialogService.ShowConfirmAsync(
                L.Get("Online_DownloadDoneTitle"), L.Get("Online_DownloadDoneBody", dir),
                L.Get("Online_OpenDir"), L.Get("Online_Close"));
            if (open)
            {
                try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) { StatusMessage = L.Get("Online_OpenDirFailed", ex.Message); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            StatusMessage = L.Get("Online_DownloadCancelled");
            AddLog(L.Get("Online_DownloadCancelled"));
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = L.Get("Online_DownloadTimeout");
            AddLog(L.Get("Online_DownloadTimeoutLog"));
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Online_DownloadFailed", ex.Message);
            AddLog(L.Get("Online_DownloadFailed", ex.Message));
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
        }
        finally
        {
            IsDownloading = false;
            if (ReferenceEquals(_downloadCts, cts))
            {
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
            else
            {
                cts.Dispose();
            }
        }
    }

    private static string BuildInstructionPath(string dir)
        => Path.Combine(dir, L.Get("Online_FileName"));

    // 使用说明取自详情页，俄文原文照搬；顶部追加中文提示让用户自行翻译
    private static string BuildInstructionText(OnlineFixDetail detail)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(L.Get("Online_FileHeader"));
        sb.AppendLine();
        sb.AppendLine(L.Get("Online_FileGame", detail.Title));
        if (!string.IsNullOrEmpty(detail.VersionText)) sb.AppendLine(L.Get("Online_FileVersion", detail.VersionText));
        if (!string.IsNullOrEmpty(detail.Tablet)) sb.AppendLine(L.Get("Online_FileSource", detail.Tablet));
        sb.AppendLine(L.Get("Online_FileUrl", detail.ArticleUrl));
        sb.AppendLine(L.Get("Online_FilePassword"));
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(detail.Instructions) ? L.Get("Online_FileNone") : detail.Instructions);
        return sb.ToString();
    }
}
