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
    /// <summary>游戏列表增量渲染页大小：初始只填一页，触底后每页追加。</summary>
    private const int PageSize = 70;

    private readonly ISteamAchievementService _achievementService;
    private readonly ISteamApiService _steamApiService;
    private readonly ISettingsService _settingsService;
    private List<AchievementGameInfo> _allGames = new();
    private List<AchievementGameInfo> _filtered = new();
    private int _visibleCount;
    private bool _isLoadingMore;
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

    partial void OnSelectedSortOptionChanged(string value) => ApplyFilterAndSort();

    [RelayCommand]
    private void Search()
    {
        ApplyFilterAndSort();
    }

    private void ApplyFilterAndSort()
    {
        // 使进行中的 LoadMoreAsync 旧批次失效
        _fillVersion++;

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

        // 只填充首页（PageSize 张卡片），其余在滚动接近底部时由 LoadMoreAsync 增量追加，
        // 避免游戏数量较多（数百款）时一次性实例化全部卡片导致 UI 卡顿
        var list = items.ToList();
        _filtered = list;
        _visibleCount = Math.Min(PageSize, list.Count);

        Games.Clear();
        for (var i = 0; i < _visibleCount; i++) Games.Add(list[i]);

        GameCountText = $"共 {list.Count} 款游戏";
    }

    /// <summary>滚动接近底部时增量追加一页（PageSize 张）卡片；搜索/排序后 _fillVersion 变化会自动中止旧批次。</summary>
    public async Task LoadMoreAsync()
    {
        if (_isLoadingMore || _visibleCount >= _filtered.Count) return;
        _isLoadingMore = true;
        try
        {
            var version = _fillVersion;
            var start = _visibleCount;
            var target = Math.Min(start + PageSize, _filtered.Count);
            for (var i = start; i < target; i++)
            {
                if (version != _fillVersion) return;
                Games.Add(_filtered[i]);
                if ((i - start + 1) % 20 == 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
            _visibleCount = target;
        }
        finally
        {
            _isLoadingMore = false;
        }
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
                var error = _achievementService.LastError ?? "连接 Steam 失败";
                StatusMessage = error;
                LogService.Error("成就", $"连接 Steam 失败: {error}");
                return;
            }

            var candidates = await _achievementService.ParseOwnedGamesAsync();
            var games = _achievementService.FilterSubscribed(candidates);
            _allGames = games;
            LogService.Info("成就", $"游戏列表加载完成: 解析 {candidates.Count} 款，筛选后 {games.Count} 款");

            foreach (var game in _allGames)
                game.CoverUrl = string.Join("|", _steamApiService.GetCoverUrls((int)game.AppId));

            ApplyFilterAndSort();

            if (Games.Count == 0)
            {
                StatusMessage = "未找到账号拥有的游戏，请确认 Steam 已登录";
                LogService.Warn("成就", "未找到账号拥有的游戏，请确认 Steam 已登录");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载游戏列表失败:{ex.Message}";
            LogService.Error("成就", $"加载游戏列表失败: {ex}");
        }
        finally
        {
            IsLoadingGames = false;
        }
    }
}
