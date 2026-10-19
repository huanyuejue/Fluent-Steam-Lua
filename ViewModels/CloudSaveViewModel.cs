using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace SteamLuaManager.ViewModels;

public partial class CloudSaveViewModel : ObservableObject
{
    private readonly ICloudRedirectService _cloudService;
    private readonly ISteamPathService _steamPathService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;
    private readonly ISteamApiService _steamApiService;
    private readonly ILuaFileManager _luaFileManager;

    private bool _syncingCloudEnabled;
    private bool _listProgressSeen;
    private readonly Dictionary<int, string> _saveDirs = new();

    public ObservableCollection<GameInfo> RedirectedGames { get; } = new();

    // 选项列表永不重建：显示名每次按当前语言计算，切语言只通知刷新文本
    public sealed class SortOptionItem : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly string _displayKey;
        public string Code { get; }

        public SortOptionItem(string displayKey, string code)
        {
            _displayKey = displayKey;
            Code = code;
        }

        public string Display => L.Get(_displayKey);

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void RefreshDisplay() =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Display)));
    }

    public System.Collections.Generic.List<SortOptionItem> SortOptions { get; } = new()
    {
        new("Cloud_SortTimeDesc", "TimeDesc"),
        new("Cloud_SortTimeAsc", "TimeAsc"),
        new("Cloud_SortAppIdAsc", "AppIdAsc"),
        new("Cloud_SortAppIdDesc", "AppIdDesc"),
    };

    private string _sortCode = "TimeDesc";

    public SortOptionItem? SelectedSortOptionItem
    {
        get => SortOptions.FirstOrDefault(o => o.Code == _sortCode);
        set
        {
            if (value is null || value.Code == _sortCode) return;
            _sortCode = value.Code;
            ApplySorting();
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private bool _isCloudEnabled;

    [ObservableProperty]
    private bool _isBusy;

    private string _syncPathText = "";
    public string SyncPathText
    {
        get => string.IsNullOrEmpty(_syncPathText) ? L.Get("Cloud_NotConfigured") : _syncPathText;
        set => SetProperty(ref _syncPathText, value == L.Get("Cloud_NotConfigured") ? "" : value);
    }

    // 列表区状态行：闲置显示默认提示，连接/读取/删除中与失败时替换显示；
    // 本页所有操作反馈统一走这里，顶部不再保留任何输出
    [ObservableProperty]
    private string _listStatusText = L.Get("Cloud_IdleFolderHint");

    [ObservableProperty]
    private bool _isListHintVisible = true;

    public CloudSaveViewModel(
        ICloudRedirectService cloudService,
        ISteamPathService steamPathService,
        ISettingsService settingsService,
        IDialogService dialogService,
        ISteamApiService steamApiService,
        ILuaFileManager luaFileManager)
    {
        _cloudService = cloudService;
        _steamPathService = steamPathService;
        _settingsService = settingsService;
        _dialogService = dialogService;
        _steamApiService = steamApiService;
        _luaFileManager = luaFileManager;
        // 存储服务选项列表只构建一次，切语言靠刷新显示名而非重建列表
        CloudProviders = _cloudService.ProviderOptions.Select(o => new ProviderChoice(o)).ToList();
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 选项列表保持同一实例，只刷新显示名；列表不重建，选择器选中项不受影响
        foreach (var o in SortOptions) o.RefreshDisplay();
        foreach (var o in CloudProviders) o.RefreshDisplay();
        foreach (var a in WebDavAuthOptions) a.RefreshDisplay();
        OnPropertyChanged(nameof(SyncPathText));
        RefreshProviderStatus();
        SetIdleListHint();
        foreach (var g in RedirectedGames) g.RefreshLocalizedTexts();
    }

    public void OnNavigatedTo() => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        // 云端名单是 N+1 远端请求，设总超时兜底，避免无限挂起
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_refreshCts.Token, timeout.Token);
        try
        {
            IsBusy = true;
            await RefreshCoreAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // 被新切换中断静默让路；超时才在列表区提示
            if (timeout.IsCancellationRequested)
            {
                IsListHintVisible = true;
                ListStatusText = L.Get("Cloud_ReadTimeout");
                LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogRefreshTimeout"));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 内部调用走这里：调用方已持有 IsBusy，不再经过入口守卫
    private async Task RefreshCoreAsync(CancellationToken ct = default)
    {
        try
        {
            var st = await _cloudService.RefreshStatusAsync();
            _syncingCloudEnabled = true;
            try { IsCloudEnabled = st.CloudEnabled; }
            finally { _syncingCloudEnabled = false; }
            SyncPathText = st.SyncPath ?? "";
            RefreshProviderBlock();
            SetIdleListHint();
            RefreshProviderStatus();

            // 云端源未登录/无凭证时不发起连接，直接显示闲置提示，避免空等超时
            if (SelectedProvider != "folder" && !IsProviderSignedIn)
            {
                _saveDirs.Clear();
                RedirectedGames.Clear();
                SetIdleListHint();
                return;
            }

            // 名单与封面沿用主页逻辑：先用 Lua 名单与本地缓存秒填，缺的再走网络补齐；
            // 连接与读取进度直接显示在列表区
            var progress = new Progress<string>(msg =>
            {
                _listProgressSeen = true;
                ListStatusText = msg;
                IsListHintVisible = true;
            });
            List<RedirectedApp> scanned;
            try
            {
                if (SelectedProvider == "folder")
                {
                    scanned = await _cloudService.GetRedirectedAppsAsync(progress, ct);
                }
                else
                {
                    // 连接看门狗：25 秒内无任何进度回报视为连接阶段卡死（DNS/代理黑洞），
                    // 有进度则放行到 90 秒总预算；本地目录扫描快，不走看门狗
                    _listProgressSeen = false;
                    ListStatusText = L.Get("Cloud_Connecting");
                    IsListHintVisible = true;
                    var listTask = _cloudService.GetRedirectedAppsAsync(progress, ct);
                    var winner = await Task.WhenAny(listTask, Task.Delay(TimeSpan.FromSeconds(25), ct));
                    if (winner != listTask && !_listProgressSeen)
                    {
                        ct.ThrowIfCancellationRequested();
                        _refreshCts?.Cancel();
                        throw new TimeoutException(L.Get("Cloud_ConnectTimeout"));
                    }
                    scanned = await listTask;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 名单失败清空旧源残留，错误直接显示在列表区
                _saveDirs.Clear();
                RedirectedGames.Clear();
                IsListHintVisible = true;
                ListStatusText = ex is TimeoutException ? ex.Message : L.Get("Cloud_RefreshListFailed", ex.Message);
                LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogRefreshListFailed", ex.Message));
                return;
            }
            var luaById = new Dictionary<int, GameInfo>();
            try
            {
                foreach (var g in await _luaFileManager.ScanLuaFilesAsync())
                    luaById[g.AppId] = g;
            }
            catch (Exception ex)
            {
                LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_ReadLuaFailed", ex.Message));
            }
            _saveDirs.Clear();
            var list = new List<GameInfo>();
            foreach (var s in scanned)
            {
                _saveDirs[s.AppId] = s.SaveDir;
                GameInfo g;
                if (luaById.TryGetValue(s.AppId, out var lg))
                    g = lg;
                else
                    g = new GameInfo { AppId = s.AppId, GameName = $"AppID: {s.AppId}" };
                g.LastSaveTime = s.LastSaveTime;
                list.Add(g);
            }
            _steamApiService.PopulateFromCache(list);
            RedirectedGames.Clear();
            foreach (var g in list)
                RedirectedGames.Add(g);
            ApplySorting();
            // 有名单隐藏提示行；空名单恢复闲置提示；有跳过项则常驻显示失败说明
            var listFails = _cloudService.GetLastListFailures();
            if (listFails.Count > 0)
            {
                var shown = listFails.Count <= 3
                    ? string.Join("，", listFails)
                    : string.Join("，", listFails.Take(3)) + L.Get("Cloud_EtAl", listFails.Count);
                ListStatusText = RedirectedGames.Count == 0
                    ? L.Get("Cloud_SkippedEmpty", listFails.Count, shown)
                    : L.Get("Cloud_SkippedPartial", shown, RedirectedGames.Count);
                IsListHintVisible = true;
                LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogSkipped", listFails.Count, string.Join("；", listFails)));
            }
            else
            {
                RestoreIdleListHint();
            }
            RefreshProviderStatus();
            _ = RefreshMissingInfoAsync(list);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            IsListHintVisible = true;
            ListStatusText = L.Get("Cloud_RefreshFailed", ex.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogRefreshFailed2", ex.Message));
        }
    }

    // 列表区闲置提示：未配置时显示，连接/读取中与失败时被替换
    private void SetIdleListHint()
    {
        ListStatusText = SelectedProvider == "folder"
            ? L.Get("Cloud_IdleFolderHint")
            : L.Get("Cloud_IdleCloudHint");
        IsListHintVisible = true;
    }

    // 本页所有操作反馈统一走列表状态区，顶部不再保留任何输出
    private void SetListStatus(string message)
    {
        ListStatusText = message;
        IsListHintVisible = true;
    }

    // 名单排序：默认存档时间倒序（无时间沉底），切换只重排不重拉
    private void ApplySorting()
    {
        if (RedirectedGames.Count == 0) return;
        var sorted = _sortCode switch
        {
            "TimeAsc" => RedirectedGames
                .OrderBy(g => g.LastSaveTime ?? DateTime.MaxValue)
                .ThenBy(g => g.AppId).ToList(),
            "AppIdAsc" => RedirectedGames.OrderBy(g => g.AppId).ToList(),
            "AppIdDesc" => RedirectedGames.OrderByDescending(g => g.AppId).ToList(),
            _ => RedirectedGames
                .OrderByDescending(g => g.LastSaveTime ?? DateTime.MinValue)
                .ThenBy(g => g.AppId).ToList(),
        };
        RedirectedGames.Clear();
        foreach (var g in sorted)
            RedirectedGames.Add(g);
    }

    // 后台补齐缺失的名称与封面，失败静默（本地已有内容不受影响）
    private async Task RefreshMissingInfoAsync(List<GameInfo> games)
    {
        try
        {
            await _steamApiService.RefreshGameInfoAsync(games);
            // 后台补到的名字可能改变按名排序，收尾重排一次
            ApplySorting();
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_InfoFillFailed", ex.Message));
        }
    }

    [RelayCommand]
    private void OpenSaveFolder(int appId)
    {
        try
        {
            // 云端源无本地目录，打开对应网页控制台
            if (_cloudService.GetCloudProvider() != "folder")
            {
                var url = _cloudService.GetCloudConsoleUrl(appId);
                if (string.IsNullOrEmpty(url))
                {
                    SetListStatus(L.Get("Cloud_CantLocateCloud"));
                    return;
                }
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return;
            }
            if (!_saveDirs.TryGetValue(appId, out var dir) || !Directory.Exists(dir))
            {
                SetListStatus(L.Get("Cloud_NoSaveDir"));
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetListStatus(L.Get("Cloud_OpenSaveFailed"));
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogOpenSaveFailed", ex.Message));
        }
    }

    [RelayCommand]
    private async Task DeleteGameAsync(GameInfo? game)
    {
        if (IsBusy || game == null) return;
        if (SteamProcess.IsSteamRunning())
        {
            SetListStatus(L.Get("Cloud_ExitSteamFirst"));
            await _dialogService.ShowAlertAsync(L.Get("Cloud_SteamRunningDeleteTitle"),
                L.Get("Cloud_SteamRunningDeleteBody"));
            return;
        }

        // 提供商在 preview 前捕获：确认文案、删除语义、完成提示与本次 preview 口径一致
        var isCloud = _cloudService.GetCloudProvider() != "folder";
        DeletePreview preview;
        try
        {
            var previewProgress = new Progress<string>(msg => SetListStatus(msg));
            preview = await Task.Run(() => _cloudService.PreviewAppDeleteAsync(game.AppId, previewProgress));
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogPreviewFailed", ex.Message));
            return;
        }
        if (preview.Targets.Count == 0)
        {
            SetListStatus(L.Get("Cloud_NoDeletable"));
            return;
        }

        var displayName = string.IsNullOrEmpty(game.GameName) ? game.AppId.ToString() : game.GameName;
        var confirmNote = isCloud
            ? L.Get("Cloud_CloudDeleteNote")
            : null;
        if (!await _dialogService.ShowDeleteSavesConfirmAsync(displayName, game.AppId, preview.Targets, preview.BackupDir, confirmNote))
        {
            RestoreIdleListHint();
            return;
        }

        try
        {
            IsBusy = true;
            var progress = new Progress<string>(msg => SetListStatus(msg));
            await _cloudService.DeleteAppSavesAsync(preview, progress);
            await RefreshCoreAsync();
            await _dialogService.ShowAlertAsync(L.Get("Cloud_DeletedTitle"),
                isCloud
                    ? L.Get("Cloud_DeletedCloudBody", displayName, preview.BackupDir)
                    : L.Get("Cloud_DeletedLocalBody", displayName, preview.BackupDir));
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogDeleteFailed", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIsCloudEnabledChanged(bool value)
    {
        if (_syncingCloudEnabled) return;
        _ = ApplyCloudEnabledAsync(value);
    }

    private void RevertToggle(bool value)
    {
        _syncingCloudEnabled = true;
        try { IsCloudEnabled = value; }
        finally { _syncingCloudEnabled = false; }
    }

    private async Task ApplyCloudEnabledAsync(bool value)
    {
        if (IsBusy)
        {
            RevertToggle(!value);
            return;
        }
        try
        {
            IsBusy = true;
            if (value)
            {
                var settings = _settingsService.Load();
                if (!settings.CloudBackupConfirmed)
                {
                    // 三合一：备份确认 + 启用 + 重启选项一次问完，不勾备份不让启用；
                    // 确认标记只在启用成功后落盘，启用失败下次重问
                    var (confirmed, restartNow) = await _dialogService.ShowEnableCloudConfirmAsync();
                    if (!confirmed)
                    {
                        RevertToggle(false);
                        return;
                    }
                    var progress = new Progress<string>(msg => SetListStatus(msg));
                    await _cloudService.EnableAsync(null, progress);
                    settings.CloudBackupConfirmed = true;
                    _settingsService.Save(settings);
                    if (restartNow)
                    {
                        var result = SteamProcess.RestartSteam(_steamPathService);
                        if (!result.Ok)
                        {
                            SetListStatus(result.Message);
                            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogRestartFailed", result.Message));
                        }
                    }
                    else
                    {
                        SetListStatus(L.Get("Cloud_EnabledNoRestart"));
                    }
                }
                else
                {
                    var progress = new Progress<string>(msg => SetListStatus(msg));
                    await _cloudService.EnableAsync(null, progress);
                    await PromptRestartSteamAsync(L.Get("Cloud_EnabledShort"));
                }
            }
            else
            {
                await _cloudService.DisableAsync();
                await PromptRestartSteamAsync(L.Get("Cloud_DisabledShort"));
            }
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogToggleFailed", ex.Message));
            RevertToggle(!value);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 重启二选一：立即重启走共用逻辑，暂不重启保留现状
    private async Task PromptRestartSteamAsync(string doneMessage)
    {
        var restart = await _dialogService.ShowConfirmAsync(
            L.Get("Cloud_RestartTitle"),
            L.Get("Cloud_RestartBody", doneMessage),
            L.Get("Cloud_RestartNow"), L.Get("Cloud_RestartLater"));
        if (!restart) return;
        var result = SteamProcess.RestartSteam(_steamPathService);
        if (!result.Ok)
        {
            SetListStatus(result.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogRestartFailed", result.Message));
        }
    }

    [RelayCommand]
    private async Task BrowseSyncPathAsync()
    {
        if (IsBusy) return;
        string? dir;
        try
        {
            var mainWindow = System.Windows.Application.Current?.MainWindow;
            var owner = mainWindow == null
                ? IntPtr.Zero
                : new System.Windows.Interop.WindowInteropHelper(mainWindow).Handle;
            dir = FolderPicker.PickFolder(
                string.IsNullOrEmpty(_syncPathText) ? null : _syncPathText, owner);
        }
        catch (Exception ex)
        {
            SetListStatus(L.Get("Cloud_BrowseFailed"));
            LogService.Error(L.Get("LogCat_Cloud"), L.Get("Cloud_LogBrowseFailed", ex));
            return;
        }
        if (string.IsNullOrEmpty(dir)) return;
        await ApplyNewSyncPathAsync(dir);
    }

    [RelayCommand]
    private async Task ResetSyncPathAsync()
    {
        var def = _cloudService.GetDefaultSyncPath();
        if (string.IsNullOrEmpty(def))
        {
            SetListStatus(L.Get("Cloud_NoSteamPath"));
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogResetDirFailed"));
            return;
        }
        await ApplyNewSyncPathAsync(def);
    }

    // ========== 云端提供商（与上游 companion 同契约，唯一真相在 config.json） ==========

    // 存储服务选项：显示名按当前语言实时取值（本地/s3 需本地化，品牌名恒定）
    public sealed class ProviderChoice : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly CloudProviderOption _option;

        public ProviderChoice(CloudProviderOption option) => _option = option;

        public string Id => _option.Id;

        public string DisplayName => _option.DisplayKey is { } key ? L.Get(key) : _option.DisplayName;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void RefreshDisplay() =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayName)));
    }

    public System.Collections.Generic.List<ProviderChoice> CloudProviders { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProviderOption))]
    private string _selectedProvider = "folder";

    public ProviderChoice? SelectedProviderOption
    {
        get => CloudProviders.FirstOrDefault(o => o.Id == SelectedProvider);
        set { if (value != null) SelectedProvider = value.Id; }
    }

    [ObservableProperty]
    private bool _isCloudProvider;

    [ObservableProperty]
    private string _providerStatusLine = "";

    private string _remoteRootPart = "";
    private string _authStatusPart = "";

    [ObservableProperty]
    private bool _isProviderSignedIn;

    [ObservableProperty]
    private bool _isSigningIn;

    public System.Collections.ObjectModel.ObservableCollection<string> AuthLogLines { get; } = new();

    // R2 表单
    [ObservableProperty] private string _r2AccountId = "";
    [ObservableProperty] private string _r2AccessKeyId = "";
    [ObservableProperty] private string _r2SecretKey = "";
    [ObservableProperty] private string _r2Bucket = "";
    [ObservableProperty] private string _r2KeyPrefix = "";
    [ObservableProperty] private string _r2Endpoint = "";

    // S3 表单
    [ObservableProperty] private string _s3AccessKeyId = "";
    [ObservableProperty] private string _s3SecretKey = "";
    [ObservableProperty] private string _s3Bucket = "";
    [ObservableProperty] private string _s3Endpoint = "";
    [ObservableProperty] private string _s3Region = "";
    [ObservableProperty] private string _s3KeyPrefix = "";
    [ObservableProperty] private string _s3CaCertPath = "";
    [ObservableProperty] private bool _s3SignPayload;
    [ObservableProperty] private bool _s3AllowInsecureHttp;
    [ObservableProperty] private bool _s3AllowInsecureTls;

    // WebDAV 表单
    [ObservableProperty] private string _webDavUrl = "";
    [ObservableProperty] private string _webDavUsername = "";
    [ObservableProperty] private string _webDavPassword = "";
    [ObservableProperty] private string _webDavBearerToken = "";
    [ObservableProperty] private string _webDavKeyPrefix = "";
    [ObservableProperty] private string _webDavTimeout = "0";
    [ObservableProperty] private string _webDavExtraHeaders = "";
    [ObservableProperty] private bool _webDavAllowInsecureHttp;
    [ObservableProperty] private bool _webDavAllowInsecureTls;

    // 三个云存储配置卡片默认展开；折叠状态只作用于当前界面，不写入配置文件。
    [ObservableProperty] private bool _isR2ConfigExpanded = true;
    [ObservableProperty] private bool _isS3ConfigExpanded = true;
    [ObservableProperty] private bool _isWebDavConfigExpanded = true;

    public sealed class WebDavAuthChoice : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly string _displayKey;
        public string Id { get; }

        public WebDavAuthChoice(string id, string displayKey)
        {
            Id = id;
            _displayKey = displayKey;
        }

        public string Display => L.Get(_displayKey);

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void RefreshDisplay() =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Display)));
    }

    public System.Collections.Generic.List<WebDavAuthChoice> WebDavAuthOptions { get; } = new()
    {
        new("basic", "Cloud_WebDavAuthBasic"),
        new("auto", "Cloud_WebDavAuthAuto"),
        new("digest", "Cloud_WebDavAuthDigest"),
        new("ntlm", "Cloud_WebDavAuthNtlm"),
        new("negotiate", "Cloud_WebDavAuthNegotiate"),
        new("bearer", "Cloud_WebDavAuthBearer"),
        new("none", "Cloud_WebDavAuthNone"),
    };

    private string _webDavAuth = "basic";

    public WebDavAuthChoice? SelectedWebDavAuth
    {
        get => WebDavAuthOptions.FirstOrDefault(o => o.Id == _webDavAuth);
        set
        {
            if (value == null || value.Id == _webDavAuth) return;
            _webDavAuth = value.Id;
            OnPropertyChanged();
        }
    }

    private bool _syncingProvider;
    private CancellationTokenSource? _signInCts;
    private CancellationTokenSource? _refreshCts;
    private int _authLogGeneration;

    partial void OnSelectedProviderChanged(string value)
    {
        if (_syncingProvider) return;
        IsCloudProvider = value != "folder";
        try
        {
            _cloudService.SetCloudProvider(value);
            LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_ProviderSwitchedLog", ProviderDisplayName(value)));
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogProviderSwitchFailed", ex.Message));
            RefreshProviderStatus();
            return;
        }
        RefreshProviderStatus();
        // 切源即切名单：中断进行中的刷新，以新源为准重拉
        _ = RefreshAfterProviderSwitchAsync();
        // DLL 启动时读配置，切源必须重启 Steam 才生效；连点切换只弹一次
        _pendingRestartProviderName = value;
        _ = PromptRestartAfterSwitchAsync();
    }

    private bool _restartPromptShowing;
    private string _pendingRestartProviderName = string.Empty;

    private async Task PromptRestartAfterSwitchAsync()
    {
        if (_restartPromptShowing) return;
        _restartPromptShowing = true;
        try
        {
            // 连点切换只弹一次，弹窗时用最后一次切换的源名，避免新旧对不上
            await PromptRestartSteamAsync(L.Get("Cloud_ProviderSwitched", ProviderDisplayName(_pendingRestartProviderName)));
        }
        finally { _restartPromptShowing = false; }
    }

    // 等待进行中的刷新让路，避免 IsBusy 守卫吞掉本次切换；5 秒等不到则放弃，用户可手动刷新
    private async Task RefreshAfterProviderSwitchAsync()
    {
        _refreshCts?.Cancel();
        for (var i = 0; i < 50 && IsBusy; i++)
            await Task.Delay(100);
        if (IsBusy)
        {
            SetListStatus(L.Get("Cloud_SwitchQueued"));
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogSwitchWaitTimeout"));
            return;
        }
        await RefreshAsync();
    }

    private string ProviderDisplayName(string id) =>
        CloudProviders.FirstOrDefault(p => p.Id == id)?.DisplayName ?? id;

    // 切源/刷新后重读：下拉回显 + 状态行 + 有效根目录 + R2/S3 表单预填（secret 为空则不覆盖界面输入）
    private void RefreshProviderBlock()
    {
        _syncingProvider = true;
        try { SelectedProvider = _cloudService.GetCloudProvider(); }
        finally { _syncingProvider = false; }
        IsCloudProvider = SelectedProvider != "folder";
        RefreshEffectiveRoot();
        PrefillCredentialForms();
    }

    // 有效远端根目录：两台机器核对前缀是否一致就看这里；登录态追加在前面，
    // 未登录时根目录为空，只显示登录态
    private void RefreshEffectiveRoot()
    {
        try
        {
            var root = _cloudService.GetEffectiveRemoteRoot();
            _remoteRootPart = string.IsNullOrEmpty(root) ? "" : L.Get("Cloud_RemoteRoot", root);
        }
        catch
        {
            _remoteRootPart = "";
        }
        UpdateProviderStatusLine();
    }

    private void UpdateProviderStatusLine()
    {
        ProviderStatusLine = string.IsNullOrEmpty(_remoteRootPart)
            ? _authStatusPart
            : string.IsNullOrEmpty(_authStatusPart)
                ? _remoteRootPart
                : $"{_authStatusPart}；{_remoteRootPart}";
    }

    // 登录态单独刷新：登录/保存凭证后只翻按钮状态，不覆盖操作结果提示
    private void UpdateSignedInFlag()
    {
        try
        {
            IsProviderSignedIn = SelectedProvider switch
            {
                "gdrive" or "onedrive" => _cloudService.CheckOAuthToken(SelectedProvider).Ok,
                "r2" => _cloudService.LoadR2Credentials() != null,
                "s3" => _cloudService.LoadS3Credentials() != null,
                "webdav" => _cloudService.LoadWebDavCredentials() != null,
                _ => false,
            };
        }
        catch (Exception ex)
        {
            IsProviderSignedIn = false;
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_SignedInReadFailed", ex.Message));
        }
    }

    // 登录态拼到远端根目录行；本地目录不显示，保持界面干净；
    // 登录态同步到 IsProviderSignedIn，供登录面板切换按钮
    private void RefreshProviderStatus()
    {
        try
        {
            UpdateSignedInFlag();
            switch (SelectedProvider)
            {
                case "gdrive" or "onedrive":
                    var (ok, msg) = _cloudService.CheckOAuthToken(SelectedProvider);
                    _authStatusPart = ok ? L.Get("Cloud_SignedIn", msg) : L.Get("Cloud_NotSignedIn", msg);
                    break;
                case "r2":
                case "s3":
                case "webdav":
                    var (_, credMsg) = _cloudService.CheckStoredCredentials(SelectedProvider);
                    _authStatusPart = credMsg;
                    break;
                default:
                    _authStatusPart = string.Empty;
                    break;
            }
        }
        catch (Exception ex)
        {
            IsProviderSignedIn = false;
            _authStatusPart = L.Get("Cloud_StatusReadFailed", ex.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogProviderStatusFailed", ex.Message));
        }
        UpdateProviderStatusLine();
    }

    private void PrefillCredentialForms()
    {
        try
        {
            var r2 = _cloudService.LoadR2Credentials();
            if (r2 != null)
            {
                R2AccountId = r2.AccountId;
                R2AccessKeyId = r2.AccessKeyId;
                R2Bucket = r2.Bucket;
                if (!string.IsNullOrEmpty(r2.KeyPrefix)) R2KeyPrefix = r2.KeyPrefix;
                if (!string.IsNullOrEmpty(r2.Endpoint)) R2Endpoint = r2.Endpoint;
                if (!string.IsNullOrEmpty(r2.SecretAccessKey)) R2SecretKey = r2.SecretAccessKey;
            }
            var s3 = _cloudService.LoadS3Credentials();
            if (s3 != null)
            {
                S3AccessKeyId = s3.AccessKeyId;
                S3Bucket = s3.Bucket;
                S3Endpoint = s3.Endpoint;
                S3Region = s3.Region;
                if (!string.IsNullOrEmpty(s3.KeyPrefix)) S3KeyPrefix = s3.KeyPrefix;
                if (!string.IsNullOrEmpty(s3.CaCertPath)) S3CaCertPath = s3.CaCertPath;
                S3SignPayload = s3.SignPayload;
                S3AllowInsecureHttp = s3.AllowInsecureHttp;
                S3AllowInsecureTls = s3.AllowInsecureTls;
                if (!string.IsNullOrEmpty(s3.SecretAccessKey)) S3SecretKey = s3.SecretAccessKey;
            }
            var webdav = _cloudService.LoadWebDavCredentials();
            if (webdav != null)
            {
                WebDavUrl = webdav.Url;
                WebDavUsername = webdav.Username;
                WebDavBearerToken = webdav.BearerToken;
                WebDavKeyPrefix = webdav.KeyPrefix;
                WebDavExtraHeaders = webdav.ExtraHeaders;
                WebDavTimeout = webdav.TimeoutSec.ToString();
                WebDavAllowInsecureHttp = webdav.AllowInsecureHttp;
                WebDavAllowInsecureTls = webdav.AllowInsecureTls;
                _webDavAuth = string.IsNullOrWhiteSpace(webdav.Auth)
                    ? "basic" : webdav.Auth.Trim().ToLowerInvariant();
                if (_webDavAuth is not ("basic" or "auto" or "digest" or "ntlm" or "negotiate" or "bearer" or "none"))
                    _webDavAuth = "basic";
                OnPropertyChanged(nameof(SelectedWebDavAuth));
                if (!string.IsNullOrEmpty(webdav.Password)) WebDavPassword = webdav.Password;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogPrefillFailed", ex.Message));
        }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsSigningIn) return;
        if (SelectedProvider is not ("gdrive" or "onedrive")) return;
        IsSigningIn = true;
        _authLogGeneration++;
        AuthLogLines.Clear();
        _signInCts = new CancellationTokenSource();
        void Log(string msg) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            AuthLogLines.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            while (AuthLogLines.Count > 500)
                AuthLogLines.RemoveAt(0);
        });
        try
        {
            using var oauth = new CloudOAuthService();
            var tokenPath = _cloudService.GetTokenPath(SelectedProvider);
            var ok = await oauth.AuthorizeAsync(SelectedProvider, tokenPath, Log, _signInCts.Token);
            SetListStatus(ok ? L.Get("Cloud_LoginOkStatus") : L.Get("Cloud_LoginIncomplete"));
            if (ok) ScheduleAuthLogAutoClear();
            UpdateSignedInFlag();
            LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_OAuthResultLog", ok ? L.Get("Cloud_OAuthOk") : L.Get("Cloud_OAuthNo"), SelectedProvider));
        }
        catch (Exception ex)
        {
            SetListStatus(L.Get("Cloud_LoginError", ex.Message));
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogOAuthError", ex.Message));
        }
        finally
        {
            IsSigningIn = false;
            _signInCts?.Dispose();
            _signInCts = null;
        }
    }

    [RelayCommand]
    private void ClearAuthLog()
    {
        _authLogGeneration++;
        AuthLogLines.Clear();
    }

    // 登录成功后日志框 8 秒自动收起；期间重登/手动清空则取消本次自动清理
    private void ScheduleAuthLogAutoClear()
    {
        var gen = _authLogGeneration;
        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ =>
        {
            if (gen == _authLogGeneration)
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => AuthLogLines.Clear());
        });
    }

    [RelayCommand]
    private void CancelSignIn()
    {
        try { _signInCts?.Cancel(); } catch { }
    }

    // 列表区回到闲置：有名单隐藏提示行，空名单恢复闲置提示
    private void RestoreIdleListHint()
    {
        IsListHintVisible = RedirectedGames.Count == 0;
        if (IsListHintVisible) SetIdleListHint();
    }

    // 连接测试：只列举两级目录，不断 stats；0 数据不算错（提示核对前缀），鉴权/网络失败才报错
    // 结果由弹窗承载，不写顶部状态行；进行中与失败过程显示在列表状态区
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsBusy || IsSigningIn) return;
        var provider = _cloudService.GetCloudProvider();
        if (provider == "folder")
        {
            ListStatusText = L.Get("Cloud_FolderModeNoTest");
            IsListHintVisible = true;
            return;
        }
        // 未登录直接指引去登录，不发 60 秒请求干等
        var signedIn = provider switch
        {
            "gdrive" or "onedrive" => _cloudService.CheckOAuthToken(provider).Ok,
            "r2" => _cloudService.LoadR2Credentials() != null,
            "s3" => _cloudService.LoadS3Credentials() != null,
            "webdav" => _cloudService.LoadWebDavCredentials() != null,
            _ => false,
        };
        if (!signedIn)
        {
            ListStatusText = L.Get("Cloud_NotLoggedInTest", ProviderDisplayName(provider));
            IsListHintVisible = true;
            return;
        }
        var display = ProviderDisplayName(provider);
        var root = _cloudService.GetEffectiveRemoteRoot();
        try
        {
            IsBusy = true;
            ListStatusText = L.Get("Cloud_Testing");
            IsListHintVisible = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var r = await _cloudService.TestCloudConnectionAsync(cts.Token);
            if (!string.IsNullOrEmpty(r.Error))
            {
                ListStatusText = L.Get("Cloud_TestFailedStatus", r.Error);
                IsListHintVisible = true;
                LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogTestFailed", provider, r.Error));
                await _dialogService.ShowAlertAsync(L.Get("Cloud_TestFailedTitle"),
                    L.Get("Cloud_TestFailedBody", display, root, r.Error));
                return;
            }
            var detail = r.AccountCount == 0
                ? L.Get("Cloud_NoAccounts")
                : r.AppCount == 0
                    ? L.Get("Cloud_NoAppsUnderAccount", r.AccountCount)
                    : L.Get("Cloud_AccountsApps", r.AccountCount, r.AppCount);
            LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogTestPassed", provider, detail));
            RestoreIdleListHint();
            await _dialogService.ShowAlertAsync(L.Get("Cloud_TestPassedTitle"),
                L.Get("Cloud_TestPassedBody", display, root, detail));
        }
        catch (OperationCanceledException)
        {
            ListStatusText = L.Get("Cloud_TestTimeout");
            IsListHintVisible = true;
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogTestTimeout"));
        }
        catch (Exception ex)
        {
            ListStatusText = ex.Message;
            IsListHintVisible = true;
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogTestError", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 退出登录：删 token/凭证文件；DLL 共用同一文件，退出后该源同步即失效，需二次确认
    [RelayCommand]
    private async Task SignOutAsync(string? provider)
    {
        if (IsBusy || IsSigningIn) return;
        var target = string.IsNullOrEmpty(provider) ? SelectedProvider : provider;
        var display = ProviderDisplayName(target);
        var isOAuth = target is "gdrive" or "onedrive";
        var confirmed = await _dialogService.ShowConfirmAsync(
            isOAuth ? L.Get("Cloud_SignOutOAuthTitle") : L.Get("Cloud_ClearCredTitle"),
            isOAuth
                ? L.Get("Cloud_SignOutOAuthBody", display)
                : L.Get("Cloud_ClearCredBody", display),
            isOAuth ? L.Get("Cloud_SignOutOAuthTitle") : L.Get("Cloud_ClearCredTitle"), L.Get("Common_Cancel"));
        if (!confirmed) return;
        try
        {
            IsBusy = true;
            await Task.Run(() => _cloudService.SignOut(target));
            // 退出后清空界面 secret（文件已删，留着旧值误导人）；其他表单项保持原样
            if (target == "r2") R2SecretKey = "";
            if (target == "s3") S3SecretKey = "";
            if (target == "webdav")
            {
                WebDavPassword = "";
                WebDavBearerToken = "";
            }
            SetListStatus(isOAuth ? L.Get("Cloud_SignedOut") : L.Get("Cloud_CredCleared"));
            LogService.Info(L.Get("LogCat_Cloud"), isOAuth ? L.Get("Cloud_SignedOut") : L.Get("Cloud_CredCleared"));
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogSignOutFailed", ex.Message));
            return;
        }
        finally
        {
            IsBusy = false;
        }
        RefreshProviderStatus();
        RefreshEffectiveRoot();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SaveR2Async()
    {
        if (IsBusy) return;
        // 必填先验：缺谁报谁，免得裸调服务端吃英文错
        var missingR2 = new List<string>();
        if (string.IsNullOrWhiteSpace(R2AccountId)) missingR2.Add(L.Get("Cloud_AccountId"));
        if (string.IsNullOrWhiteSpace(R2AccessKeyId)) missingR2.Add("Access Key");
        if (string.IsNullOrWhiteSpace(R2SecretKey)) missingR2.Add("Secret Key");
        if (string.IsNullOrWhiteSpace(R2Bucket)) missingR2.Add("Bucket");
        if (missingR2.Count > 0)
        {
            SetListStatus(L.Get("Cloud_FillMissing", string.Join("、", missingR2)));
            return;
        }
        try
        {
            IsBusy = true;
            var path = await Task.Run(() => _cloudService.SaveR2Credentials(new R2Credentials(
                R2AccountId.Trim(), R2AccessKeyId.Trim(), R2SecretKey,
                R2Bucket.Trim(), R2KeyPrefix.Trim(), R2Endpoint.Trim())));
            SetListStatus(L.Get("Cloud_R2Saved"));
            LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogR2Saved", path));
            SyncProviderSelection("r2");
            RefreshEffectiveRoot();
            UpdateSignedInFlag();
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogR2SaveFailed", ex.Message));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveS3Async()
    {
        if (IsBusy) return;
        var missingS3 = new List<string>();
        if (string.IsNullOrWhiteSpace(S3AccessKeyId)) missingS3.Add("Access Key");
        if (string.IsNullOrWhiteSpace(S3SecretKey)) missingS3.Add("Secret Key");
        if (string.IsNullOrWhiteSpace(S3Bucket)) missingS3.Add("Bucket");
        if (string.IsNullOrWhiteSpace(S3Endpoint)) missingS3.Add("Endpoint");
        if (string.IsNullOrWhiteSpace(S3Region)) missingS3.Add("Region");
        if (missingS3.Count > 0)
        {
            SetListStatus(L.Get("Cloud_FillMissing", string.Join("、", missingS3)));
            return;
        }
        try
        {
            IsBusy = true;
            // 明文 HTTP 或跳过 TLS 校验会让密钥在传输中可被截获，保存前必须让用户知情确认
            if (S3AllowInsecureHttp || S3AllowInsecureTls)
            {
                var ok = await _dialogService.ShowConfirmAsync(L.Get("Cloud_InsecureTitle"),
                    L.Get("Cloud_InsecureBody"),
                    L.Get("Cloud_KeepSaving"), L.Get("Common_Cancel"));
                if (!ok)
                {
                    SetListStatus(L.Get("Cloud_S3SaveCancelled"));
                    return;
                }
            }
            var path = await Task.Run(() => _cloudService.SaveS3Credentials(new S3Credentials(
                S3AccessKeyId.Trim(), S3SecretKey, S3Bucket.Trim(),
                S3Endpoint.Trim(), S3Region.Trim(), S3KeyPrefix.Trim(),
                S3SignPayload, S3AllowInsecureHttp, S3AllowInsecureTls, S3CaCertPath.Trim())));
            SetListStatus(L.Get("Cloud_S3Saved"));
            LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogS3Saved", path));
            SyncProviderSelection("s3");
            RefreshEffectiveRoot();
            UpdateSignedInFlag();
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogS3SaveFailed", ex.Message));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveWebDavAsync()
    {
        if (IsBusy) return;
        var needsUser = _webDavAuth is "basic" or "auto" or "digest" or "ntlm" or "negotiate";
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(WebDavUrl)) missing.Add("URL");
        if (needsUser && string.IsNullOrWhiteSpace(WebDavUsername))
            missing.Add(L.Get("Cloud_WebDavUser"));
        if (_webDavAuth == "bearer" && string.IsNullOrWhiteSpace(WebDavBearerToken))
            missing.Add(L.Get("Cloud_WebDavToken"));
        if (missing.Count > 0)
        {
            SetListStatus(L.Get("Cloud_FillMissing", string.Join("、", missing)));
            return;
        }
        if (!int.TryParse(WebDavTimeout.Trim(), out var timeout) || timeout < 0 || timeout > 300)
        {
            SetListStatus(L.Get("Cloud_WebDavTimeoutInvalid"));
            return;
        }
        try
        {
            IsBusy = true;
            // 明文 HTTP / 跳过证书校验会让账号密码可被截获，保存前明确告知
            if (WebDavAllowInsecureHttp || WebDavAllowInsecureTls)
            {
                var ok = await _dialogService.ShowConfirmAsync(L.Get("Cloud_InsecureTitle"),
                    L.Get("Cloud_InsecureBody"), L.Get("Cloud_KeepSaving"), L.Get("Common_Cancel"));
                if (!ok)
                {
                    SetListStatus(L.Get("Cloud_WebDavSaveCancelled"));
                    return;
                }
            }
            var path = await Task.Run(() => _cloudService.SaveWebDavCredentials(new WebDavCredentials(
                WebDavUrl.Trim(), WebDavUsername.Trim(), WebDavPassword, _webDavAuth,
                WebDavBearerToken.Trim(), WebDavKeyPrefix.Trim(),
                WebDavAllowInsecureHttp, WebDavAllowInsecureTls, timeout, WebDavExtraHeaders)));
            SetListStatus(L.Get("Cloud_WebDavSaved"));
            LogService.Info(L.Get("LogCat_Cloud"), L.Get("Cloud_LogWebDavSaved", path));
            SyncProviderSelection("webdav");
            RefreshEffectiveRoot();
            UpdateSignedInFlag();
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogWebDavSaveFailed", ex.Message));
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void BrowseCaCert()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.Get("Cloud_CaTitle"),
                Filter = L.Get("Cloud_CaFilter"),
                CheckFileExists = true,
            };
            if (!string.IsNullOrEmpty(S3CaCertPath) && System.IO.File.Exists(S3CaCertPath))
                dialog.InitialDirectory = System.IO.Path.GetDirectoryName(S3CaCertPath);
            if (dialog.ShowDialog() == true)
                S3CaCertPath = dialog.FileName;
        }
        catch (Exception ex)
        {
            SetListStatus(L.Get("Cloud_CaBrowseFailed"));
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogCaFailed", ex.Message));
        }
    }

    // 保存凭证后把下拉静默同步到对应源（服务侧已落盘，这里只做界面回显，不重触发变更提示）
    private void SyncProviderSelection(string id)
    {
        if (SelectedProvider == id) return;
        _syncingProvider = true;
        try { SelectedProvider = id; }
        finally { _syncingProvider = false; }
    }

    // 目录切换统一走这里：同目录直接跳过，否则先搬旧存档再切配置
    private async Task ApplyNewSyncPathAsync(string dir)
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            var current = string.IsNullOrEmpty(_syncPathText) ? null : _syncPathText;
            if (!string.IsNullOrEmpty(current) && string.Equals(
                    current.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                    dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return;

            // 先搬旧存档再切配置，旧目录无存档则直接切换
            string? migrateFailure = null;
            if (!string.IsNullOrEmpty(current) && System.IO.Directory.Exists(current))
            {
                var migrateProgress = new Progress<string>(msg => SetListStatus(msg));
                var m = await _cloudService.MigrateSavesAsync(current, dir, migrateProgress);
                if (m.FailedFiles.Count > 0)
                {
                    LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogMigrateFailed", string.Join(", ", m.FailedFiles)));
                    migrateFailure = L.Get("Cloud_MigratePartial", m.MovedFiles, m.FailedFiles.Count);
                    SetListStatus(migrateFailure);
                }
                else if (m.MovedFiles > 0)
                {
                    SetListStatus(L.Get("Cloud_Migrated", m.MovedFiles));
                }
            }
            await _cloudService.SetSyncPathAsync(dir);
            await PromptRestartSteamAsync(L.Get("Cloud_SyncDirUpdated"));
            await RefreshCoreAsync();
            // 刷新会清空状态行，失败摘要需要保留
            if (migrateFailure != null)
                SetListStatus(migrateFailure);
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Cloud_LogChdirFailed", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }
}
