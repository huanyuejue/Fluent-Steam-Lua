using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

/// <summary>成就编辑窗口 VM：持有常驻 worker 会话，窗口生命周期内多次加载/保存不重启游戏。</summary>
public partial class AchievementEditViewModel : ObservableObject, IDisposable
{
    private readonly uint _appId;
    private readonly string _gameName;
    private WorkerSession? _session;
    private bool _loading;

    public ObservableCollection<AchievementEntry> Achievements { get; } = new();

    public string GameName => _gameName;
    public string AppIdText => $"AppID: {_appId}";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _emptyHintText = "";

    private DispatcherTimer? _statusTimer;

    /// <summary>统一 3 秒后自动清除底部通知（与全局各页一致）。</summary>
    partial void OnStatusMessageChanged(string value)
    {
        _statusTimer?.Stop();
        if (string.IsNullOrEmpty(value)) return;
        _statusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick -= StatusTimer_Tick;
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        StatusMessage = string.Empty;
    }

    public AchievementEditViewModel(uint appId, string gameName)
    {
        _appId = appId;
        _gameName = gameName;
    }

    public async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            IsBusy = true;
            StatusMessage = "";
            if (_session == null)
            {
                _session = await WorkerSession.StartAsync(_appId);
                if (_session == null)
                {
                    StatusMessage = L.Get("AchEdit_SessionStartFailed");
                    EmptyHintText = L.Get("AchEdit_RetryHint", L.Get("AchEdit_Reload"));
                    LogService.Error(L.Get("LogCat_Achieve"), $"{L.Get("AchEdit_SessionStartFailed")} ({_gameName} AppID {_appId})");
                    return;
                }
            }

            if (await ReloadCoreAsync() == false)
            {
                // 首次加载失败（stats 会话可能尚未就绪），自动重试一次
                await Task.Delay(1500);
                await ReloadCoreAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("AchEdit_AutoLoadError", ex.Message);
            EmptyHintText = L.Get("AchEdit_RetryHint", L.Get("AchEdit_Reload"));
            LogService.Error(L.Get("LogCat_Achieve"), $"{L.Get("AchEdit_AutoLoadError", ex.Message)} ({_gameName} AppID {_appId})");
        }
        finally
        {
            _loading = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_session == null)
        {
            await LoadAsync();
            return;
        }

        await ReloadCoreAsync();
    }

    private async Task<bool> ReloadCoreAsync()
    {
        // 会话为空（子进程未启动或已退出）不再静默返回，给一行提示免得界面干等
        if (_session == null)
        {
            StatusMessage = L.Get("AchEdit_SessionUnavailable") + "，" + L.Get("AchEdit_RetryHint", L.Get("AchEdit_Reload"));
            EmptyHintText = L.Get("AchEdit_SessionUnavailable") + "，" + L.Get("AchEdit_RetryHint", L.Get("AchEdit_Reload"));
            return false;
        }

        IsBusy = true;
        StatusMessage = "";
        EmptyHintText = "";
        try
        {
            var data = await _session.LoadAsync();
            if (data == null)
            {
                StatusMessage = _session.LastError ?? L.Get("AchEdit_LoadFailed");
                EmptyHintText = L.Get("AchEdit_RetryHint", L.Get("AchEdit_Reload"));
                LogService.Error(L.Get("LogCat_Achieve"), L.Get("AchEdit_LogSaveFailed", _gameName, _appId, StatusMessage));
                return false;
            }

            Achievements.Clear();
            foreach (var entry in ToAchievementEntries(data))
            {
                Achievements.Add(entry);
            }

            LogService.Info(L.Get("LogCat_Achieve"), L.Get("AchEdit_LogLoaded", Achievements.Count, _gameName, _appId));
            StatusMessage = L.Get("AchEdit_LoadedStatus", Achievements.Count);
            if (Achievements.Count == 0)
            {
                EmptyHintText = L.Get("AchEdit_NoDefinitions");
            }
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_session == null) return;

        var modifiedAchievements = Achievements
            .Where(a => a.IsModified && !a.IsProtected)
            .ToList();

        if (modifiedAchievements.Count == 0)
        {
            StatusMessage = L.Get("AchEdit_NoChanges");
            return;
        }

        StatusMessage = "";

        var request = new WorkerSaveRequest();
        foreach (var entry in modifiedAchievements)
        {
            request.Achievements.Add(new WorkerAchievementChange { Id = entry.Id, Achieved = entry.IsAchieved });
        }

        var result = await _session.SaveAsync(request);
        if (result == null)
        {
            StatusMessage = L.Get("AchEdit_SaveNoResponse");
            LogService.Error(L.Get("LogCat_Achieve"), L.Get("AchEdit_LogSaveFailed", _gameName, _appId, L.Get("AchEdit_SaveNoResponse")));
            return;
        }

        if (result.Ok == false)
        {
            StatusMessage = result.Message;
            LogService.Error(L.Get("LogCat_Achieve"), L.Get("AchEdit_LogSaveFailed", _gameName, _appId, result.Message));
            return;
        }

        foreach (var entry in modifiedAchievements)
        {
            entry.OriginalAchieved = entry.IsAchieved;
        }

        LogService.Info(L.Get("LogCat_Achieve"), L.Get("AchEdit_LogSaved", modifiedAchievements.Count, _gameName, _appId));
        StatusMessage = L.Get("AchEdit_SavedStatus", modifiedAchievements.Count);
    }

    [RelayCommand]
    private void UnlockAll()
    {
        foreach (var entry in Achievements.Where(a => !a.IsProtected && !a.IsAchieved))
        {
            entry.IsAchieved = true;
        }
    }

    [RelayCommand]
    private void LockAll()
    {
        foreach (var entry in Achievements.Where(a => !a.IsProtected && a.IsAchieved))
        {
            entry.IsAchieved = false;
        }
    }

    [RelayCommand]
    private void InvertAll()
    {
        foreach (var entry in Achievements.Where(a => !a.IsProtected))
        {
            entry.IsAchieved = !entry.IsAchieved;
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_statusTimer != null)
        {
            _statusTimer.Stop();
            _statusTimer.Tick -= StatusTimer_Tick;
            _statusTimer = null;
        }
        _session?.Dispose();
        _session = null;
    }

    private static System.Collections.Generic.List<AchievementEntry> ToAchievementEntries(WorkerLoadResult data)
    {
        var result = new System.Collections.Generic.List<AchievementEntry>();
        foreach (var a in data.Achievements)
        {
            var def = new AchievementDefinition
            {
                Id = a.Id,
                Name = a.Name,
                Description = a.Description,
                IconNormal = a.IconNormal,
                IconLocked = a.IconLocked,
                IsHidden = a.Hidden,
                Permission = a.Permission
            };

            var unlockTime = a.UnlockTimeUtc is long t && t > 0
                ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime
                : (DateTime?)null;

            result.Add(new AchievementEntry(data.AppId, def, a.Achieved, unlockTime));
        }
        return result;
    }
}
