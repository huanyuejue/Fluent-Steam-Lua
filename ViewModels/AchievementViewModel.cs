using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

/// <summary>成就管理页 VM：游戏列表展示、搜索、排序筛选；成就编辑在独立窗口（AchievementEditWindow）中进行。</summary>
public partial class AchievementViewModel : ObservableObject, IDisposable
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

    // 选项列表永不重建：显示名每次按当前语言计算，切语言只通知刷新文本，
    // 列表实例与选择器选中项因此始终保持稳定
    public sealed class ViewOption : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly string _displayKey;
        public string Value { get; }

        public ViewOption(string displayKey, string value)
        {
            _displayKey = displayKey;
            Value = value;
        }

        public string Display => L.Get(_displayKey);

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void RefreshDisplay() =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Display)));
    }

    public List<ViewOption> SortOptions { get; } = new()
    {
        new("Home_SortNameAsc", "NameAsc"),
        new("Home_SortNameDesc", "NameDesc"),
        new("Home_SortAppIdAsc", "AppIdAsc"),
        new("Home_SortAppIdDesc", "AppIdDesc"),
    };

    public List<ViewOption> ViewModeOptions { get; } = new()
    {
        new("Ach_ViewCardMode", "Card"),
        new("Ach_ViewListMode", "List"),
    };

    // 下拉用对象引用绑定：列表实例稳定后选择器选中项不会因列表变化被清空
    public ViewOption? SelectedViewModeOption
    {
        get => ViewModeOptions.FirstOrDefault(o => o.Value == SelectedViewMode);
        set { if (value != null) SelectedViewMode = value.Value; }
    }

    public ViewOption? SelectedSortOptionItem
    {
        get => SortOptions.FirstOrDefault(o => o.Value == SelectedSortOption);
        set { if (value != null) SelectedSortOption = value.Value; }
    }

    private void RefreshLocalizedOptions()
    {
        // 选项列表保持同一实例，只刷新显示名；列表不重建，选择器选中项不受影响
        foreach (var o in SortOptions) o.RefreshDisplay();
        foreach (var o in ViewModeOptions) o.RefreshDisplay();
        GameCountText = L.Get("Ach_GameCount", _filtered.Count);
    }

    [ObservableProperty]
    private bool _isLoadingGames;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _gameCountText = "";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSortOptionItem))]
    private string _selectedSortOption = "NameAsc";

    /// <summary>成就页视图模式：Card/List 码值持久化，重启后保持；入库旧存档中文值迁移。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedViewModeOption))]
    private string _selectedViewMode = "Card";

    public AchievementViewModel(ISteamAchievementService achievementService, ISteamApiService steamApiService, ISettingsService settingsService)
    {
        _achievementService = achievementService;
        _steamApiService = steamApiService;
        _settingsService = settingsService;
        var settings = settingsService.Load();
        var saved = settings.AchievementViewMode;
        _selectedViewMode = saved is "列表" or "列表模式" or "List" ? "List" : "Card";
        // 历史存档可能残留空值，归一化后就近落盘，避免以后每次启动重复修正
        if (settings.AchievementViewMode != _selectedViewMode)
        {
            settings.AchievementViewMode = _selectedViewMode;
            settingsService.Save(settings);
        }
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        RefreshLocalizedOptions();

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

    private DispatcherTimer? _statusTimer;
    private bool _disposed;

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
        if (_statusTimer != null)
        {
            _statusTimer.Stop();
            _statusTimer.Tick -= StatusTimer_Tick;
            _statusTimer = null;
        }
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
            "NameDesc" => items.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase),
            "AppIdAsc" => items.OrderBy(g => g.AppId),
            "AppIdDesc" => items.OrderByDescending(g => g.AppId),
            _ => items.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
        };

        // 只填充首页（PageSize 张卡片），其余在滚动接近底部时由 LoadMoreAsync 增量追加，
        // 避免游戏数量较多（数百款）时一次性实例化全部卡片导致 UI 卡顿
        var list = items.ToList();
        _filtered = list;
        _visibleCount = Math.Min(PageSize, list.Count);

        Games.Clear();
        for (var i = 0; i < _visibleCount; i++) Games.Add(list[i]);

        GameCountText = L.Get("Ach_GameCount", list.Count);
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

    /// <summary>封面 URL 链（按命中速度排序）：本地已下载封面 → CDN 模板链 → storeapi 实时兜底标记。</summary>
    private string BuildCoverUrl(AchievementGameInfo game)
    {
        var appId = (int)game.AppId;
        var parts = new List<string>();

        var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "covers", $"{appId}.jpg");
        if (File.Exists(local))
            parts.Add(new Uri(local).AbsoluteUri);

        parts.AddRange(_steamApiService.GetCoverUrls(appId));
        parts.Add($"storeapi://{appId}");

        return string.Join("|", parts.Distinct());
    }

    [RelayCommand]
    private async Task LoadGamesAsync()
    {
        if (IsLoadingGames) return;

        IsLoadingGames = true;
        StatusMessage = L.Get("Ach_Loading");
        try
        {
            if (!_achievementService.IsConnected && !_achievementService.Connect())
            {
                var error = _achievementService.LastError ?? L.Get("Ach_ConnectFail");
                StatusMessage = error;
                LogService.Error(L.Get("LogCat_Achieve"), error);
                return;
            }

            var candidates = await _achievementService.ParseOwnedGamesAsync();
            var games = _achievementService.FilterSubscribed(candidates);
            _allGames = games;
            LogService.Info(L.Get("LogCat_Achieve"), L.Get("Ach_LogLoaded", candidates.Count, games.Count));

            foreach (var game in _allGames)
                game.CoverUrl = BuildCoverUrl(game);

            ApplyFilterAndSort();

            if (Games.Count == 0)
            {
                StatusMessage = L.Get("Ach_NoGames");
                LogService.Warn(L.Get("LogCat_Achieve"), L.Get("Ach_NoGames"));
            }
            else
            {
                StatusMessage = L.Get("Ach_Refreshed", games.Count);
                LogService.Info(L.Get("LogCat_Achieve"), L.Get("Ach_Refreshed", games.Count));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Ach_LoadFailed", ex.Message);
            LogService.Error(L.Get("LogCat_Achieve"), L.Get("Ach_LoadFailed", ex));
        }
        finally
        {
            IsLoadingGames = false;
        }
    }
}
