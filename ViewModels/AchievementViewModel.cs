using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

/// <summary>成就管理页 VM：游戏列表展示、搜索、排序筛选；成就编辑在独立窗口（AchievementEditWindow）中进行。</summary>
public partial class AchievementViewModel : ObservableObject
{
    private readonly ISteamAchievementService _achievementService;
    private readonly ISteamApiService _steamApiService;
    private readonly ISettingsService _settingsService;
    private List<AchievementGameInfo> _allGames = new();
    private int _fillVersion;

    public ObservableCollection<AchievementGameInfo> Games { get; } = new();

    public IReadOnlyList<string> SortOptions { get; } = ["名称 A-Z", "名称 Z-A", "AppID 升序", "AppID 降序"];

    [ObservableProperty]
    private bool _isLoadingGames;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _gameCountText = "";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedSortOption = "名称 A-Z";

    /// <summary>成就页视图模式：卡片/列表。持久化，重启后保持。</summary>
    [ObservableProperty]
    private string _selectedViewMode = "卡片";

    public IReadOnlyList<string> ViewModeOptions { get; } = ["卡片模式", "列表模式"];

    public AchievementViewModel(ISteamAchievementService achievementService, ISteamApiService steamApiService, ISettingsService settingsService)
    {
        _achievementService = achievementService;
        _steamApiService = steamApiService;
        _settingsService = settingsService;
        var saved = settingsService.Load().AchievementViewMode;
        _selectedViewMode = saved is "列表" or "列表模式" ? "列表模式" : "卡片模式";
    }

    partial void OnSelectedViewModeChanged(string value)
    {
        var settings = _settingsService.Load();
        settings.AchievementViewMode = value;
        _settingsService.Save(settings);
    }

    /// <summary>切页时调用：数据已加载则直接返回，避免每次进入都重建列表（卡片/封面保留）。</summary>
    public Task EnsureLoadedAsync()
    {
        if (_allGames.Count > 0) return Task.CompletedTask;
        return LoadGamesAsync();
    }

    async partial void OnSelectedSortOptionChanged(string value) => await ApplyFilterAndSortAsync();

    [RelayCommand]
    private async Task SearchAsync() => await ApplyFilterAndSortAsync();

    private async Task ApplyFilterAndSortAsync()
    {
        var version = ++_fillVersion;

        IEnumerable<AchievementGameInfo> items = _allGames;

        var query = SearchText.Trim();
        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(g =>
                g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                g.AppId.ToString().Contains(query));
        }

        items = SelectedSortOption switch
        {
            "名称 Z-A" => items.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase),
            "AppID 升序" => items.OrderBy(g => g.AppId),
            "AppID 降序" => items.OrderByDescending(g => g.AppId),
            _ => items.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
        };

        // 分批填充，避免一次实例化全部卡片导致 UI 卡顿
        var list = items.ToList();
        Games.Clear();
        const int chunk = 40;
        for (int i = 0; i < list.Count; i += chunk)
        {
            if (version != _fillVersion) return;
            for (int j = i; j < Math.Min(i + chunk, list.Count); j++)
            {
                Games.Add(list[j]);
            }
            if (i + chunk < list.Count)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        GameCountText = $"共 {Games.Count} 款游戏";
    }

    [RelayCommand]
    private async Task LoadGamesAsync()
    {
        if (IsLoadingGames) return;

        IsLoadingGames = true;
        StatusMessage = "";
        try
        {
            if (!_achievementService.IsConnected && !_achievementService.Connect())
            {
                StatusMessage = _achievementService.LastError ?? "连接 Steam 失败";
                return;
            }

            var candidates = await _achievementService.ParseOwnedGamesAsync();
            var games = _achievementService.FilterSubscribed(candidates);
            _allGames = games;

            foreach (var game in _allGames)
                game.CoverUrl = string.Join("|", _steamApiService.GetCoverUrls((int)game.AppId));

            await ApplyFilterAndSortAsync();

            if (Games.Count == 0)
            {
                StatusMessage = "未找到账号拥有的游戏，请确认 Steam 已登录";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载游戏列表失败:{ex.Message}";
        }
        finally
        {
            IsLoadingGames = false;
        }
    }
}
