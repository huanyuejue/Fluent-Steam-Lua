using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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
                    StatusMessage = "启动 Steam 会话失败，请确认 Steam 正在运行且已登录";
                    EmptyHintText = "启动 Steam 会话失败，可点击「重新加载」重试";
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
            StatusMessage = "自动加载异常：" + ex.Message;
            EmptyHintText = "加载失败，可点击「重新加载」重试";
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
        if (_session == null) return false;

        IsBusy = true;
        StatusMessage = "";
        EmptyHintText = "";
        try
        {
            var data = await _session.LoadAsync();
            if (data == null)
            {
                StatusMessage = _session.LastError ?? "加载成就数据失败";
                EmptyHintText = "加载失败，可点击「重新加载」重试";
                return false;
            }

            Achievements.Clear();
            foreach (var entry in ToAchievementEntries(data))
            {
                Achievements.Add(entry);
            }

            StatusMessage = $"已加载 {Achievements.Count} 个成就";
            if (Achievements.Count == 0)
            {
                EmptyHintText = "未找到成就定义（游戏未启动过或缺少 schema 文件）";
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
            StatusMessage = "没有需要保存的修改";
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
            StatusMessage = "保存失败（会话无响应）";
            return;
        }

        if (result.Ok == false)
        {
            StatusMessage = result.Message;
            return;
        }

        foreach (var entry in modifiedAchievements)
        {
            entry.OriginalAchieved = entry.IsAchieved;
        }

        StatusMessage = $"已保存 {modifiedAchievements.Count} 个成就";
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

    public void Dispose()
    {
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
