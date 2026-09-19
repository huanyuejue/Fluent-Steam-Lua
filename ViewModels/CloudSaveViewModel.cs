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
    private readonly Dictionary<int, string> _saveDirs = new();

    public ObservableCollection<GameInfo> RedirectedGames { get; } = new();

    [ObservableProperty]
    private bool _isCloudEnabled;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _syncPathText = "未配置";

    [ObservableProperty]
    private string _statusMessage = "";

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
    }

    public void OnNavigatedTo() => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        try
        {
            IsBusy = true;
            await RefreshCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 内部调用走这里：调用方已持有 IsBusy，不再经过入口守卫
    private async Task RefreshCoreAsync()
    {
        try
        {
            StatusMessage = "正在刷新状态...";
            var st = await _cloudService.RefreshStatusAsync();
            _syncingCloudEnabled = true;
            try { IsCloudEnabled = st.CloudEnabled; }
            finally { _syncingCloudEnabled = false; }
            SyncPathText = string.IsNullOrEmpty(st.SyncPath) ? "未配置" : st.SyncPath;

            // 名单与封面沿用主页逻辑：先用 Lua 名单与本地缓存秒填，缺的再走网络补齐
            var scanned = _cloudService.GetRedirectedApps();
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
            StatusMessage = string.Empty;
            _ = RefreshMissingInfoAsync(list);
        }
        catch (Exception ex)
        {
            StatusMessage = $"刷新失败：{ex.Message}";
            LogService.Warn("云存档", $"刷新状态失败: {ex.Message}");
        }
    }

    // 后台补齐缺失的名称与封面，失败静默（本地已有内容不受影响）
    private async Task RefreshMissingInfoAsync(List<GameInfo> games)
    {
        try
        {
            await _steamApiService.RefreshGameInfoAsync(games);
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
            if (!_saveDirs.TryGetValue(appId, out var dir) || !Directory.Exists(dir))
            {
                StatusMessage = "存档目录不存在";
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = "打开存档目录失败";
            LogService.Warn("云存档", $"打开存档目录失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteGameAsync(GameInfo? game)
    {
        if (IsBusy || game == null) return;
        if (SteamProcess.IsSteamRunning())
        {
            StatusMessage = "请先退出 Steam 再删除存档";
            await _dialogService.ShowAlertAsync("Steam 正在运行",
                "删除存档前请先完全退出 Steam，否则残留缓存可能把已删文件复活。");
            return;
        }

        DeletePreview preview;
        try
        {
            preview = await Task.Run(() => _cloudService.PreviewAppDelete(game.AppId));
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LogService.Warn("云存档", $"统计待删存档失败: {ex.Message}");
            return;
        }
        if (preview.Targets.Count == 0)
        {
            StatusMessage = "没有可删除的存档";
            return;
        }

        var displayName = string.IsNullOrEmpty(game.GameName) ? game.AppId.ToString() : game.GameName;
        if (!await _dialogService.ShowDeleteSavesConfirmAsync(displayName, game.AppId, preview.Targets, preview.BackupDir))
            return;

        try
        {
            IsBusy = true;
            var progress = new Progress<string>(msg => StatusMessage = msg);
            await _cloudService.DeleteAppSavesAsync(preview, progress);
            await RefreshCoreAsync();
            await _dialogService.ShowAlertAsync("删除完成",
                $"已删除《{displayName}》的全部存档。\n\n备份位于：\n{preview.BackupDir}\n恢复需手动拷回对应目录。");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
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
                    var confirmed = await _dialogService.ShowConfirmAsync(
                        "启用前确认",
                        "云存档会接管 Lua 游戏的存档读写，首次启用前请先备份重要存档。\n\n确认已备份并启用吗？",
                        "已备份，启用", "取消");
                    if (!confirmed)
                    {
                        RevertToggle(false);
                        return;
                    }
                    settings.CloudBackupConfirmed = true;
                    _settingsService.Save(settings);
                }

                var progress = new Progress<string>(msg => StatusMessage = msg);
                await _cloudService.EnableAsync(null, progress);
                await PromptRestartSteamAsync("云存档已启用");
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
            StatusMessage = ex.Message;
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
        var result = SteamProcess.RestartSteam(_steamPathService);
        if (!result.Ok)
        {
            StatusMessage = result.Message;
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
            StatusMessage = "打开目录选择失败，请重试";
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
            StatusMessage = "未检测到 Steam 路径";
            LogService.Warn("云存档", "重置目录失败：未检测到 Steam 路径");
            return;
        }
        await ApplyNewSyncPathAsync(def);
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
                var migrateProgress = new Progress<string>(msg => StatusMessage = msg);
                var m = await _cloudService.MigrateSavesAsync(current, dir, migrateProgress);
                if (m.FailedFiles.Count > 0)
                {
                    LogService.Warn("云存档", $"迁移失败文件: {string.Join(", ", m.FailedFiles)}");
                    migrateFailure = $"已迁移 {m.MovedFiles} 个文件，{m.FailedFiles.Count} 个失败（可能被 Steam 占用），重启 Steam 后可手动复制剩余文件";
                    StatusMessage = migrateFailure;
                }
                else if (m.MovedFiles > 0)
                {
                    StatusMessage = $"已迁移 {m.MovedFiles} 个文件";
                }
            }
            await _cloudService.SetSyncPathAsync(dir);
            await PromptRestartSteamAsync("重定向目录已更新");
            await RefreshCoreAsync();
            // 刷新会清空状态行，失败摘要需要保留
            if (migrateFailure != null)
                StatusMessage = migrateFailure;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LogService.Warn("云存档", $"切换目录失败: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
