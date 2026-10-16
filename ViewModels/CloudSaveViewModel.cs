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

    public System.Collections.Generic.List<string> SortOptions { get; } =
        new() { "按存档时间降序", "按存档时间升序", "按AppID升序", "按AppID降序" };

    [ObservableProperty]
    private string _selectedSortOption = "按存档时间降序";

    [ObservableProperty]
    private bool _isCloudEnabled;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _syncPathText = "未配置";

    // 列表区状态行：闲置显示默认提示，连接/读取/删除中与失败时替换显示；
    // 本页所有操作反馈统一走这里，顶部不再保留任何输出
    [ObservableProperty]
    private string _listStatusText = "暂无已重定向游戏：启用云存档后进游戏存一次档即会出现在列表";

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
        CloudProviders = new System.Collections.Generic.List<CloudProviderOption>(_cloudService.ProviderOptions);
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
                ListStatusText = "读取超时，请检查网络后重试";
                LogService.Warn("云存档", "刷新云端名单超时");
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
            SyncPathText = string.IsNullOrEmpty(st.SyncPath) ? "未配置" : st.SyncPath;
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
                    ListStatusText = "正在连接云端…";
                    IsListHintVisible = true;
                    var listTask = _cloudService.GetRedirectedAppsAsync(progress, ct);
                    var winner = await Task.WhenAny(listTask, Task.Delay(TimeSpan.FromSeconds(25), ct));
                    if (winner != listTask && !_listProgressSeen)
                    {
                        ct.ThrowIfCancellationRequested();
                        _refreshCts?.Cancel();
                        throw new TimeoutException("连接云端超时（25 秒无响应），请检查网络或代理后重试");
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
                ListStatusText = ex is TimeoutException ? ex.Message : $"刷新名单失败：{ex.Message}";
                LogService.Warn("云存档", $"刷新名单失败: {ex.Message}");
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
                LogService.Warn("云存档", $"读取 Lua 游戏列表失败: {ex.Message}");
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
                    : string.Join("，", listFails.Take(3)) + $"等{listFails.Count}个";
                ListStatusText = RedirectedGames.Count == 0
                    ? $"{listFails.Count} 个游戏读取失败，已跳过：{shown}"
                    : $"部分游戏读取失败，已跳过：{shown}；其余 {RedirectedGames.Count} 个正常";
                IsListHintVisible = true;
                LogService.Warn("云存档", $"云端名单读取跳过 {listFails.Count} 个：{string.Join("；", listFails)}");
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
            ListStatusText = $"刷新失败：{ex.Message}";
            LogService.Warn("云存档", $"刷新状态失败: {ex.Message}");
        }
    }

    // 列表区闲置提示：未配置时显示，连接/读取中与失败时被替换
    private void SetIdleListHint()
    {
        ListStatusText = SelectedProvider == "folder"
            ? "暂无已重定向游戏：启用云存档后进游戏存一次档即会出现在列表"
            : "暂无云端存档：该源下尚未同步过，或登录/凭证未配置";
        IsListHintVisible = true;
    }

    // 本页所有操作反馈统一走列表状态区，顶部不再保留任何输出
    private void SetListStatus(string message)
    {
        ListStatusText = message;
        IsListHintVisible = true;
    }

    partial void OnSelectedSortOptionChanged(string value) => ApplySorting();

    // 名单排序：默认存档时间倒序（无时间沉底），切换只重排不重拉
    private void ApplySorting()
    {
        if (RedirectedGames.Count == 0) return;
        var sorted = SelectedSortOption switch
        {
            "按存档时间升序" => RedirectedGames
                .OrderBy(g => g.LastSaveTime ?? DateTime.MaxValue)
                .ThenBy(g => g.AppId).ToList(),
            "按AppID升序" => RedirectedGames.OrderBy(g => g.AppId).ToList(),
            "按AppID降序" => RedirectedGames.OrderByDescending(g => g.AppId).ToList(),
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
            LogService.Warn("云存档", $"补齐游戏信息失败: {ex.Message}");
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
                    SetListStatus("无法定位云端目录，请先刷新名单");
                    return;
                }
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return;
            }
            if (!_saveDirs.TryGetValue(appId, out var dir) || !Directory.Exists(dir))
            {
                SetListStatus("存档目录不存在");
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetListStatus("打开存档目录失败");
            LogService.Warn("云存档", $"打开存档目录失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteGameAsync(GameInfo? game)
    {
        if (IsBusy || game == null) return;
        if (SteamProcess.IsSteamRunning())
        {
            SetListStatus("请先退出 Steam 再删除存档");
            await _dialogService.ShowAlertAsync("Steam 正在运行",
                "删除存档前请先完全退出 Steam，否则残留缓存可能把已删文件复活。");
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
            LogService.Warn("云存档", $"统计待删存档失败: {ex.Message}");
            return;
        }
        if (preview.Targets.Count == 0)
        {
            SetListStatus("没有可删除的存档");
            return;
        }

        var displayName = string.IsNullOrEmpty(game.GameName) ? game.AppId.ToString() : game.GameName;
        var confirmNote = isCloud
            ? "注：云端源下将删除远端存档（本地 DLL 缓存与 Steam 用户数据一并清理），删除前自动下载备份到本地。"
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
            await _dialogService.ShowAlertAsync("删除完成",
                isCloud
                    ? $"已删除《{displayName}》的全部存档（含云端远端）。\n\n备份位于：\n{preview.BackupDir}\n恢复需手动上传回云端对应目录。"
                    : $"已删除《{displayName}》的全部存档。\n\n备份位于：\n{preview.BackupDir}\n恢复需手动拷回对应目录。");
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn("云存档", $"删除存档失败: {ex.Message}");
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
                        var result = await SteamProcess.RestartSteamAsync(_steamPathService);
                        if (!result.Ok)
                        {
                            SetListStatus(result.Message);
                            LogService.Warn("云存档", $"重启 Steam 失败: {result.Message}");
                        }
                    }
                    else
                    {
                        SetListStatus("云存档已启用，重启 Steam 后生效");
                    }
                }
                else
                {
                    var progress = new Progress<string>(msg => SetListStatus(msg));
                    await _cloudService.EnableAsync(null, progress);
                    await PromptRestartSteamAsync("云存档已启用");
                }
            }
            else
            {
                await _cloudService.DisableAsync();
                await PromptRestartSteamAsync("云存档已关闭");
            }
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn("云存档", $"切换开关失败: {ex.Message}");
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
            "需要重启 Steam",
            $"{doneMessage}，需重启 Steam 后生效。\n\n是否立即重启 Steam？",
            "立即重启", "暂不重启");
        if (!restart) return;
        var result = await SteamProcess.RestartSteamAsync(_steamPathService);
        if (!result.Ok)
        {
            SetListStatus(result.Message);
            LogService.Warn("云存档", $"重启 Steam 失败: {result.Message}");
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
                SyncPathText == "未配置" ? null : SyncPathText, owner);
        }
        catch (Exception ex)
        {
            SetListStatus("打开目录选择失败，请重试");
            LogService.Error("云存档", $"选择本地目录失败: {ex}");
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
            SetListStatus("未检测到 Steam 路径");
            LogService.Warn("云存档", "重置目录失败：未检测到 Steam 路径");
            return;
        }
        await ApplyNewSyncPathAsync(def);
    }

    // ========== 云端提供商（与上游 companion 同契约，唯一真相在 config.json） ==========

    public System.Collections.Generic.List<CloudProviderOption> CloudProviders { get; }

    [ObservableProperty]
    private string _selectedProvider = "folder";

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
            LogService.Info("云存档", $"云端提供商已切换为{ProviderDisplayName(value)}，重启 Steam 后生效");
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn("云存档", $"切换提供商失败: {ex.Message}");
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
            await PromptRestartSteamAsync($"云端提供商已切换为{ProviderDisplayName(_pendingRestartProviderName)}");
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
            SetListStatus("切换排队中：上一次刷新尚未结束，可稍后手动刷新");
            LogService.Warn("云存档", "切源后等待刷新让路超时（5 秒），已放弃自动刷新");
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
            _remoteRootPart = string.IsNullOrEmpty(root) ? "" : $"远端根目录：{root}";
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
                _ => false,
            };
        }
        catch (Exception ex)
        {
            IsProviderSignedIn = false;
            LogService.Warn("云存档", $"读取登录态失败: {ex.Message}");
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
                    _authStatusPart = ok ? $"已登录：{msg}" : $"未登录：{msg}";
                    break;
                case "r2":
                case "s3":
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
            _authStatusPart = $"状态读取失败：{ex.Message}";
            LogService.Warn("云存档", $"读取提供商状态失败: {ex.Message}");
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
        }
        catch (Exception ex)
        {
            LogService.Warn("云存档", $"预填凭证表单失败: {ex.Message}");
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
            SetListStatus(ok ? "登录成功，重启 Steam 后生效" : "登录未完成");
            if (ok) ScheduleAuthLogAutoClear();
            UpdateSignedInFlag();
            LogService.Info("云存档", $"OAuth 登录{(ok ? "成功" : "未完成")}：{SelectedProvider}");
        }
        catch (Exception ex)
        {
            SetListStatus($"登录异常：{ex.Message}");
            LogService.Warn("云存档", $"OAuth 登录异常: {ex.Message}");
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
            ListStatusText = "本地目录模式无需连接测试";
            IsListHintVisible = true;
            return;
        }
        // 未登录直接指引去登录，不发 60 秒请求干等
        var signedIn = provider switch
        {
            "gdrive" or "onedrive" => _cloudService.CheckOAuthToken(provider).Ok,
            "r2" => _cloudService.LoadR2Credentials() != null,
            "s3" => _cloudService.LoadS3Credentials() != null,
            _ => false,
        };
        if (!signedIn)
        {
            ListStatusText = $"尚未登录{ProviderDisplayName(provider)}，请先在上方登录后再测试连接";
            IsListHintVisible = true;
            return;
        }
        var display = ProviderDisplayName(provider);
        var root = _cloudService.GetEffectiveRemoteRoot();
        try
        {
            IsBusy = true;
            ListStatusText = "正在测试远端连接…";
            IsListHintVisible = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var r = await _cloudService.TestCloudConnectionAsync(cts.Token);
            if (!string.IsNullOrEmpty(r.Error))
            {
                ListStatusText = $"连接测试失败：{r.Error}";
                IsListHintVisible = true;
                LogService.Warn("云存档", $"连接测试失败：{provider} {r.Error}");
                await _dialogService.ShowAlertAsync("连接测试失败",
                    $"提供商：{display}\n远端根目录：{root}\n\n错误：{r.Error}");
                return;
            }
            var detail = r.AccountCount == 0
                ? "认证通过，但该前缀下没有任何账号目录：空号或 key_prefix/目录名不对，请核对后再试。"
                : r.AppCount == 0
                    ? $"找到账号 {r.AccountCount} 个，但首个账号下没有游戏存档：DLL 尚未同步过，进游戏存一次档后再试。"
                    : $"账号 {r.AccountCount} 个，首个账号下游戏 {r.AppCount} 个";
            LogService.Info("云存档", $"连接测试通过：{provider} {detail}");
            RestoreIdleListHint();
            await _dialogService.ShowAlertAsync("连接测试通过",
                $"提供商：{display}\n远端根目录：{root}\n\n{detail}");
        }
        catch (OperationCanceledException)
        {
            ListStatusText = "连接测试超时，请检查网络后重试";
            IsListHintVisible = true;
            LogService.Warn("云存档", "连接测试超时");
        }
        catch (Exception ex)
        {
            ListStatusText = ex.Message;
            IsListHintVisible = true;
            LogService.Warn("云存档", $"连接测试异常: {ex.Message}");
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
            isOAuth ? "退出登录" : "清除凭证",
            isOAuth
                ? $"退出{display}登录后，DLL 也无法再同步到该源（需重启 Steam 生效）。本地备份不受影响。\n\n确认退出吗？"
                : $"清除{display}凭证后，DLL 也无法再同步到该源（需重启 Steam 生效）。本地备份不受影响。\n\n确认清除吗？",
            isOAuth ? "退出登录" : "清除凭证", "取消");
        if (!confirmed) return;
        try
        {
            IsBusy = true;
            await Task.Run(() => _cloudService.SignOut(target));
            // 退出后清空界面 secret（文件已删，留着旧值误导人）；其他表单项保持原样
            if (target == "r2") R2SecretKey = "";
            if (target == "s3") S3SecretKey = "";
            SetListStatus(isOAuth ? "已退出登录" : "凭证已清除");
            LogService.Info("云存档", isOAuth ? "已退出登录" : "凭证已清除");
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn("云存档", $"退出登录失败: {ex.Message}");
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
        if (string.IsNullOrWhiteSpace(R2AccountId)) missingR2.Add("账户 ID");
        if (string.IsNullOrWhiteSpace(R2AccessKeyId)) missingR2.Add("Access Key");
        if (string.IsNullOrWhiteSpace(R2SecretKey)) missingR2.Add("Secret Key");
        if (string.IsNullOrWhiteSpace(R2Bucket)) missingR2.Add("Bucket");
        if (missingR2.Count > 0)
        {
            SetListStatus($"请填写：{string.Join("、", missingR2)}");
            return;
        }
        try
        {
            IsBusy = true;
            var path = await Task.Run(() => _cloudService.SaveR2Credentials(new R2Credentials(
                R2AccountId.Trim(), R2AccessKeyId.Trim(), R2SecretKey,
                R2Bucket.Trim(), R2KeyPrefix.Trim(), R2Endpoint.Trim())));
            SetListStatus($"R2 凭证已保存，重启 Steam 后生效");
            LogService.Info("云存档", $"R2 凭证已保存：{path}");
            SyncProviderSelection("r2");
            RefreshEffectiveRoot();
            UpdateSignedInFlag();
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn("云存档", $"保存 R2 凭证失败: {ex.Message}");
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
            SetListStatus($"请填写：{string.Join("、", missingS3)}");
            return;
        }
        try
        {
            IsBusy = true;
            // 明文 HTTP 或跳过 TLS 校验会让密钥在传输中可被截获，保存前必须让用户知情确认
            if (S3AllowInsecureHttp || S3AllowInsecureTls)
            {
                var ok = await _dialogService.ShowConfirmAsync("安全警告",
                    "你勾选了不安全的连接选项（明文 HTTP / 跳过证书校验），" +
                    "同一网络下的攻击者可以直接截获你的访问密钥并完全控制该存储桶。\n\n" +
                    "仅在内网自建存储等可信环境下继续，确定保存吗？",
                    "仍要保存", "取消");
                if (!ok)
                {
                    SetListStatus("已取消保存 S3 凭证");
                    return;
                }
            }
            var path = await Task.Run(() => _cloudService.SaveS3Credentials(new S3Credentials(
                S3AccessKeyId.Trim(), S3SecretKey, S3Bucket.Trim(),
                S3Endpoint.Trim(), S3Region.Trim(), S3KeyPrefix.Trim(),
                S3SignPayload, S3AllowInsecureHttp, S3AllowInsecureTls, S3CaCertPath.Trim())));
            SetListStatus($"S3 凭证已保存，重启 Steam 后生效");
            LogService.Info("云存档", $"S3 凭证已保存：{path}");
            SyncProviderSelection("s3");
            RefreshEffectiveRoot();
            UpdateSignedInFlag();
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn("云存档", $"保存 S3 凭证失败: {ex.Message}");
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
                Title = "选择 CA 证书",
                Filter = "PEM 文件|*.pem|证书文件|*.crt;*.cer|所有文件|*.*",
                CheckFileExists = true,
            };
            if (!string.IsNullOrEmpty(S3CaCertPath) && System.IO.File.Exists(S3CaCertPath))
                dialog.InitialDirectory = System.IO.Path.GetDirectoryName(S3CaCertPath);
            if (dialog.ShowDialog() == true)
                S3CaCertPath = dialog.FileName;
        }
        catch (Exception ex)
        {
            SetListStatus("打开文件选择失败，请重试");
            LogService.Warn("云存档", $"选择 CA 证书失败: {ex.Message}");
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
            var current = SyncPathText == "未配置" ? null : SyncPathText;
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
                    LogService.Warn("云存档", $"迁移失败文件: {string.Join(", ", m.FailedFiles)}");
                    migrateFailure = $"已迁移 {m.MovedFiles} 个文件，{m.FailedFiles.Count} 个失败（可能被 Steam 占用），重启 Steam 后可手动复制剩余文件";
                    SetListStatus(migrateFailure);
                }
                else if (m.MovedFiles > 0)
                {
                    SetListStatus($"已迁移 {m.MovedFiles} 个文件");
                }
            }
            await _cloudService.SetSyncPathAsync(dir);
            await PromptRestartSteamAsync("重定向目录已更新");
            await RefreshCoreAsync();
            // 刷新会清空状态行，失败摘要需要保留
            if (migrateFailure != null)
                SetListStatus(migrateFailure);
        }
        catch (Exception ex)
        {
            SetListStatus(ex.Message);
            LogService.Warn("云存档", $"切换目录失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
