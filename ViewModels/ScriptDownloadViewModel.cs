using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class ScriptDownloadViewModel : ObservableObject, IDisposable
{
    private readonly ISteamPathService _steamPathService;
    private readonly ISteamDepotService _depotService;
    private readonly ISettingsService _settingsService;
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamApiService _steamApiService;
    private readonly IDialogService _dialogService;
    private string _currentDownloadMode = "DepotKey";
    private bool _disposed;
    private CancellationTokenSource? _downloadCts;

    // Chinese search retains its English fallback; English requests English names.
    private static (string Cc, string Lang)[] StoreSearchLocales =>
        LocalizationService.Instance.CurrentLanguage == "zh"
            ? new[] { ("cn", "schinese"), ("us", "english") }
            : new[] { ("us", "english") };

    [ObservableProperty]
    private string _gameId = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private bool _isSearchEmptyVisible;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusMessage = L.Get("Ruku_Ready");

    [ObservableProperty]
    private bool _includeDlc = true;

    [ObservableProperty]
    private bool _pinManifest;

    [ObservableProperty]
    private bool _fetchNameComments;

    public bool IsLocalCacheMode => _currentDownloadMode == "DepotKey";
    public string CurrentDataSourceLabel => _currentDownloadMode switch
    {
        "DepotKey" => L.Get("Ruku_LocalCache"),
        _ => L.Get("Ruku_RemoteManifest")
    };

    /// <summary>上次更新本地缓存的时间；仅本地缓存仓库模式显示，远程仓库为空。</summary>
    public string LastUpdateTimeText
    {
        get
        {
            if (!IsLocalCacheMode) return "";
            var time = _depotService.GetLastUpdateTime(_currentDownloadMode);
            return time == null ? L.Get("Ruku_CacheNotUpdated") : L.Get("Ruku_LastUpdated", time.Value.ToString("MM-dd HH:mm"));
        }
    }

    public ObservableCollection<FoundGame> SearchResults { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    // 小黑盒热门榜单：会话内临时展示，封面名字不落盘（加载成功失败都不写日志文件）
    public ObservableCollection<RankGame> PopularRanking { get; } = new();
    public ObservableCollection<RankGame> RecommendRanking { get; } = new();

    [ObservableProperty]
    private bool _showRanking = true;

    [ObservableProperty]
    private bool _isRankingLoading;

    private CancellationTokenSource? _rankingCts;

    // 榜单与日志区互斥：开着开关、日志为空、任一榜单有数据时才展示榜单区
    public bool IsRankingVisible => ShowRanking && LogLines.Count == 0 &&
        (PopularRanking.Count > 0 || RecommendRanking.Count > 0);

    public ScriptDownloadViewModel(ISteamPathService steamPathService, ISteamDepotService depotService, ISettingsService settingsService, IHttpClientProvider httpClientProvider, ISteamApiService steamApiService, IDialogService dialogService)
    {
        _steamPathService = steamPathService;
        _depotService = depotService;
        _settingsService = settingsService;
        _httpClientProvider = httpClientProvider;
        _steamApiService = steamApiService;
        _dialogService = dialogService;
        var bootSettings = _settingsService.Load();
        _currentDownloadMode = bootSettings.DownloadMode;
        ShowRanking = bootSettings.ShowRanking;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _depotService.AllSourcesUpdated += OnAllSourcesUpdated;
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        SearchResults.CollectionChanged += (_, _) => RefreshSearchEmptyVisible();
        LogLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsRankingVisible));
        PopularRanking.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsRankingVisible));
        RecommendRanking.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsRankingVisible));
        OnPropertyChanged(nameof(LastUpdateTimeText));
    }

    // 搜过但无结果才显示空提示；切页清空由 MainWindow 调 ResetSearchState 复位
    public void ResetSearchState()
    {
        HasSearched = false;
        RefreshSearchEmptyVisible();
    }

    private void RefreshSearchEmptyVisible()
        => IsSearchEmptyVisible = HasSearched && SearchResults.Count == 0 && !IsSearching;

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_currentDownloadMode == settings.DownloadMode && ShowRanking == settings.ShowRanking) return;
        _currentDownloadMode = settings.DownloadMode;
        ShowRanking = settings.ShowRanking;
        OnPropertyChanged(nameof(IsLocalCacheMode));
        OnPropertyChanged(nameof(CurrentDataSourceLabel));
        OnPropertyChanged(nameof(LastUpdateTimeText));
    }

    partial void OnShowRankingChanged(bool value) => OnPropertyChanged(nameof(IsRankingVisible));

    // 进页刷新榜单：首次进页（或之前没拉到）且开着开关时懒加载；切页离开 MainWindow 会清空日志，回来自动复现
    public void OnNavigatedTo()
    {
        OnPropertyChanged(nameof(IsRankingVisible));
        if (ShowRanking && (PopularRanking.Count == 0 || RecommendRanking.Count == 0) && !IsRankingLoading)
            _ = LoadRankingAsync();
    }

    private async Task LoadRankingAsync()
    {
        try { _rankingCts?.Cancel(); } catch { }
        try { _rankingCts?.Dispose(); } catch { }
        var cts = new CancellationTokenSource();
        _rankingCts = cts;
        IsRankingLoading = true;
        try
        {
            // 热门池按页拉满后切分：头部直出热门榜单，剩余洗牌抽取为你推荐，两排不重样
            var pool = await XiaoHeiHeService.GetPopularPoolAsync(120, cts.Token);
            PopularRanking.Clear();
            foreach (var g in pool.Take(10))
                PopularRanking.Add(g);
            var rest = pool.Skip(PopularRanking.Count).ToList();
            var rng = Random.Shared;
            for (var i = rest.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (rest[i], rest[j]) = (rest[j], rest[i]);
            }
            RecommendRanking.Clear();
            foreach (var g in rest.Take(10))
                RecommendRanking.Add(g);
            // 只进 app.log 不进 LogLines：写 UI 日志会触发榜单隐藏规则
            LogService.Info(L.Get("LogCat_Home"), L.Get("Ruku_RankLoadedLog", PopularRanking.Count, RecommendRanking.Count));
            if (PopularRanking.Count == 0 && RecommendRanking.Count == 0)
                StatusMessage = L.Get("Ruku_RankEmpty");
        }
        catch (OperationCanceledException)
        {
            // 切页/退出打断，不管
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Ruku_RankLoadFailed", ex.Message);
            LogService.Warn(L.Get("LogCat_Home"), L.Get("Ruku_RankLoadFailed", ex.Message));
        }
        finally
        {
            IsRankingLoading = false;
            if (ReferenceEquals(_rankingCts, cts))
            {
                try { _rankingCts?.Dispose(); } catch { }
                _rankingCts = null;
            }
            else
            {
                try { cts.Dispose(); } catch { }
            }
        }
    }

    // 榜单卡片一键入库：和搜索结果走同一个入库流程；入库开始即清日志写日志，榜单自动隐藏
    [RelayCommand]
    private async Task DownloadRankGameAsync(RankGame? game)
    {
        if (game == null || IsDownloading) return;
        await ExecuteDownloadAsync(game.AppId.ToString());
    }

    private void OnAllSourcesUpdated()
    {
        Application.Current.Dispatcher.Invoke(() => OnPropertyChanged(nameof(LastUpdateTimeText)));
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentDataSourceLabel));
        OnPropertyChanged(nameof(LastUpdateTimeText));
        RefreshRankingTexts();
    }

    // 榜单项是不可变 record，原位重赋触发集合 Replace 通知让关注数文案重新取值
    private void RefreshRankingTexts()
    {
        for (var i = 0; i < PopularRanking.Count; i++) PopularRanking[i] = PopularRanking[i];
        for (var i = 0; i < RecommendRanking.Count; i++) RecommendRanking[i] = RecommendRanking[i];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _depotService.AllSourcesUpdated -= OnAllSourcesUpdated;
        LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;
        try { _downloadCts?.Cancel(); } catch { }
        _downloadCts?.Dispose();
        _downloadCts = null;
        try { _rankingCts?.Cancel(); } catch { }
        try { _rankingCts?.Dispose(); } catch { }
        _rankingCts = null;
    }

    // 切页离开时由 MainWindow 调用：在飞的入库任务取消，UI 已清空不再有幽灵进度
    public void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { }
    }

    public record FoundGame(int AppId, string Name, string CoverUrl, List<string>? CoverCandidates = null, string ReleaseDate = "")
    {
        // 副标题：有发售日期拼后面，无则只显示 AppID
        public string DisplaySubtitle => string.IsNullOrEmpty(ReleaseDate)
            ? $"AppID: {AppId}"
            : L.Get("Ruku_SubtitleWithDate", AppId, ReleaseDate);
    }

    // 封面候选去重：首选失败时 UI 按序切换；老模板垫底（新游戏已 404，失败即停）
    private static List<string> CoverCandidates(int appId, params string?[] urls)
    {
        var list = new List<string>();
        foreach (var u in urls)
        {
            if (!string.IsNullOrWhiteSpace(u) && !list.Contains(u))
                list.Add(u);
        }
        var fallback = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
        if (!list.Contains(fallback))
            list.Add(fallback);
        return list;
    }

    private static void ConfigureSteamStoreHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsSearching) return;

        var query = GameId?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            StatusMessage = L.Get("Ruku_EnterQuery");
            return;
        }

        IsSearching = true;
        SearchResults.Clear();
        LogLines.Clear();
        AddLog(L.Get("Ruku_Searching", query));

        // Localized Store and fallback sources share a 15-second timeout.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            // 全链 AppId 都是 int，超 int.MaxValue 的输入无法入库，直接明示而非当游戏名搜
            if (uint.TryParse(query, out var bigId) && bigId > int.MaxValue)
            {
                AddLog(L.Get("Ruku_AppIdOutOfRange", query, int.MaxValue));
                StatusMessage = L.Get("Ruku_AppIdOutOfRangeShort", int.MaxValue);
                return;
            }
            if (int.TryParse(query, out var appId) && appId <= 0)
            {
                AddLog(L.Get("Ruku_AppIdInvalidLog", query));
                StatusMessage = L.Get("Ruku_AppIdMustBePositive");
                return;
            }
            if (appId > 0)
            {
                // Heybox supplies Chinese names; English uses the localized Steam Store.
                (string? name, string? headerImage, string releaseDate) = (null, null, "");
                if (LocalizationService.Instance.CurrentLanguage == "zh")
                    (name, headerImage, releaseDate) = await XiaoHeiHeService.GetGameDetailAsync(appId, cts.Token);
                if (name == null)
                    (name, headerImage, releaseDate) = await GetAppNameAsync(appId, cts.Token);
                // 商店下架的游戏 appdetails 无数据，用备用源再捞一次名字
                if (name == null)
                {
                    name = await _steamApiService.GetFallbackGameNameAsync(appId, cts.Token);
                    if (name != null)
                        AddLog(L.Get("Ruku_FallbackName", name));
                }
                if (name != null)
                {
                    // header_image 为哈希 CDN 完整 URL；缺失时回退老模板（对早期游戏仍有效）
                    var coverUrl = !string.IsNullOrWhiteSpace(headerImage)
                        ? headerImage
                        : $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
                    SearchResults.Add(new FoundGame(appId, name, coverUrl,
                        CoverCandidates(appId, coverUrl), releaseDate));
                    AddLog(L.Get("Ruku_FoundLog", name, appId));
                    StatusMessage = L.Get("Ruku_FoundStatus", name);
                }
                else
                {
                    // 所有名源都查不到（如已下架且社区页也被清）：名字只是展示用，
                    // 仓库密钥走独立接口，用 AppID 占位放行，不挡入库
                    AddLog(L.Get("Ruku_NoNameLog", appId));
                    SearchResults.Add(new FoundGame(
                        appId,
                        $"AppID: {appId}",
                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
                        CoverCandidates(appId)));
                    StatusMessage = L.Get("Ruku_NoNameStatus");
                }
            }
            else
            {
                await SearchByNameAsync(query, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AddLog(L.Get("Ruku_SearchTimeoutLog"));
            AddLog(L.Get("Ruku_SearchTimeoutTip"));
            StatusMessage = L.Get("Ruku_SearchTimeoutStatus");
        }
        catch (Exception ex)
        {
            AddLog(L.Get("Ruku_SearchError", ex.Message));
            StatusMessage = L.Get("Ruku_SearchError", ex.Message);
        }
        finally
        {
            IsSearching = false;
            HasSearched = true;
            RefreshSearchEmptyVisible();
        }
    }

    private async Task<(string? Name, string? HeaderImage, string ReleaseDate)> GetAppNameAsync(int appId, CancellationToken ct = default)
    {
        // Follow the app language, with English fallback for Chinese requests.
        // Steam 会对合服/改 ID 的游戏返回重定向后的 AppID 做 key，
        // 精确 key 命中失败时取首个 success 项（如 3669870 返回的 key 是 4760190）
        foreach (var lang in LocalizationService.Instance.CurrentLanguage == "zh"
                     ? new[] { "schinese", "english" } : new[] { "english" })
        {
            try
            {
                var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={lang}";
                var json = await _httpClientProvider.SendWithProxyRetryAsync(
                    "script-steam-store",
                    TimeSpan.FromSeconds(10),
                    client => client.GetStringAsync(url, ct),
                    ConfigureSteamStoreHeaders);

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // 精确 key 优先；精确 key 缺席时才采纳其他 success 项（纯重定向场景），
                    // 避免精确 key 存在但无数据时串到别的游戏
                    var candidates = new List<JsonElement>();
                    if (doc.RootElement.TryGetProperty(appId.ToString(), out var exact))
                    {
                        candidates.Add(exact);
                    }
                    else
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.Object &&
                                prop.Value.TryGetProperty("success", out var s) && s.GetBoolean())
                                candidates.Add(prop.Value);
                        }
                    }
                    foreach (var root in candidates)
                    {
                        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("name", out var name))
                            continue;
                        var headerImage = data.TryGetProperty("header_image", out var img)
                            ? img.GetString()
                            : null;
                        var releaseDate = data.TryGetProperty("release_date", out var rd) &&
                            rd.ValueKind == JsonValueKind.Object && rd.TryGetProperty("date", out var dt) &&
                            dt.ValueKind == JsonValueKind.String
                            ? dt.GetString() ?? ""
                            : "";
                        var gameName = name.GetString();
                        if (!string.IsNullOrWhiteSpace(gameName))
                            return (gameName, headerImage, releaseDate.Trim());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Warn(L.Get("LogCat_Home"), L.Get("Ruku_StoreApiFailed", appId, lang, ex.Message));
            }
        }
        return (null, null, "");
    }

    private async Task SearchByNameAsync(string name, CancellationToken ct)
    {
        // Heybox results contain Chinese names, so only use them in Chinese mode.
        var heiHe = LocalizationService.Instance.CurrentLanguage == "zh"
            ? await XiaoHeiHeService.SearchByNameAsync(name, ct) : new List<HeiHeGame>();
        if (heiHe.Count > 0)
        {
            var heiCount = Math.Min(heiHe.Count, 10);
            for (var i = 0; i < heiCount; i++)
                SearchResults.Add(new FoundGame(heiHe[i].AppId, heiHe[i].Name, heiHe[i].CoverUrl, heiHe[i].CoverCandidates, heiHe[i].ReleaseDate));
            AddLog(L.Get("Ruku_MatchCount", heiCount));
            StatusMessage = L.Get("Ruku_MatchCount", heiCount);
            return;
        }

        // Query locales in the order selected for the current app language.
        foreach (var (cc, lang) in StoreSearchLocales)
        {
            var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(name)}&cc={cc}&l={lang}";
            string json;
            try
            {
                json = await _httpClientProvider.SendWithProxyRetryAsync(
                    "script-steam-store",
                    TimeSpan.FromSeconds(10),
                    client => client.GetStringAsync(url, ct),
                    ConfigureSteamStoreHeaders);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单组请求失败则尝试下一组参数
                continue;
            }

            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items");

            if (items.GetArrayLength() == 0)
                continue;

            int count = Math.Min(items.GetArrayLength(), 10);
            for (int i = 0; i < count; i++)
            {
                var item = items[i];
                var appId = item.GetProperty("id").GetInt32();
                var gameName = item.GetProperty("name").GetString() ?? name;

                string coverUrl;
                // tiny_image 为哈希 CDN 完整 URL（storesearch 实际返回的封面字段，新游戏必需）
                if (item.TryGetProperty("tiny_image", out var tinyImg) && !string.IsNullOrEmpty(tinyImg.GetString()))
                {
                    coverUrl = tinyImg.GetString()!;
                }
                else if (item.TryGetProperty("large_image", out var largeImg) && !string.IsNullOrEmpty(largeImg.GetString()))
                {
                    coverUrl = largeImg.GetString()!;
                }
                else if (item.TryGetProperty("small_image", out var smallImg) && !string.IsNullOrEmpty(smallImg.GetString()))
                {
                    coverUrl = smallImg.GetString()!;
                }
                else
                {
                    coverUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
                }

                SearchResults.Add(new FoundGame(appId, gameName, coverUrl,
                    CoverCandidates(appId, coverUrl)));
            }

            AddLog(L.Get("Ruku_MatchCount", count));
            StatusMessage = L.Get("Ruku_MatchCount", count);
            return;
        }

        AddLog(L.Get("Ruku_NoMatch"));
        StatusMessage = L.Get("Ruku_NoMatch");
    }

    [RelayCommand]
    private async Task DownloadGameAsync(FoundGame game)
    {
        if (game == null || IsDownloading) return;
        await ExecuteDownloadAsync(game.AppId.ToString());
    }

    private Task<bool> ShowModernConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null)
        => _dialogService.ShowConfirmAsync(title, message, primaryText ?? L.Get("Common_Confirm"), closeText ?? L.Get("Common_Cancel"));

    private async Task ExecuteDownloadAsync(string gameId)
    {
        // 新下载先取消旧的（IsDownloading 门控下旧任务理论上已结束，这里防幽灵任务）
        CancelDownload();
        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        var ct = cts.Token;
        IsDownloading = true;
        LogLines.Clear();
        AddLog(L.Get("Ruku_ProcessStart", gameId));
        AddLog(L.Get("Ruku_CurrentSource", CurrentDataSourceLabel));
        AddLog("=".PadRight(50, '='));

        try
        {
            if (uint.TryParse(gameId, out var bigId) && bigId > int.MaxValue)
            {
                AddLog(L.Get("Ruku_GameIdOutOfRange", gameId, int.MaxValue));
                StatusMessage = L.Get("Ruku_GameIdOutOfRangeShort", int.MaxValue);
                return;
            }
            if (!int.TryParse(gameId, out int appId) || appId <= 0)
            {
                AddLog(L.Get("Ruku_InvalidGameId"));
                StatusMessage = L.Get("Ruku_InvalidGameId");
                return;
            }

            var luaFolder = _steamPathService.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder))
            {
                AddLog(L.Get("Ruku_NoSteamPathLog"));
                StatusMessage = L.Get("Ruku_NoSteamPathStatus");
                return;
            }

            if (!Directory.Exists(luaFolder))
            {
                Directory.CreateDirectory(luaFolder);
                AddLog(L.Get("Ruku_DirCreated", luaFolder));
            }
            else
            {
                AddLog(L.Get("Ruku_TargetDir", luaFolder));
            }

            if (IsLocalCacheMode)
            {
                // 本地生成目标固定：已存在则先确认，避免耗时查询后直接覆盖旧档
                var targetLua = Path.Combine(luaFolder, $"{appId}.lua");
                if (File.Exists(targetLua))
                {
                    var overwrite = await ShowModernConfirmAsync(
                        L.Get("Ruku_OverwriteTitle"),
                        L.Get("Ruku_OverwriteBody", appId),
                        L.Get("Ruku_Overwrite"));
                    if (!overwrite)
                    {
                        AddLog(L.Get("Ruku_CancelledKeep"));
                        StatusMessage = L.Get("Ruku_CancelledStatus");
                        return;
                    }
                }
                await ExecuteDepotKeyDownloadAsync(appId, ct);
            }
            else
            {
                await ExecuteRemoteDownloadAsync(gameId, luaFolder, ct);
            }
        }
        catch (OperationCanceledException)
        {
            AddLog(L.Get("Ruku_CancelledStatus"));
            StatusMessage = L.Get("Ruku_CancelledStatus");
        }
        catch (Exception ex)
        {
            AddLog(L.Get("Ruku_TaskError", ex.Message));
            StatusMessage = L.Get("Ruku_ErrorStatus", ex.Message);
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

    // 主仓库为空时的成因区分提示：未发售 / 暂无数据 / 查询失败（查询失败返回 null）
    private async Task<string> GetEmptyDepotsReasonAsync(int appId)
    {
        return await _steamApiService.IsComingSoonAsync(appId) switch
        {
            true => L.Get("Ruku_NotReleased"),
            false => L.Get("Ruku_NoDepotData"),
            _ => L.Get("Ruku_QueryDepotFailed")
        };
    }

    private async Task ExecuteDepotKeyDownloadAsync(int appId, CancellationToken ct)
    {
        _depotService.UseDataSource(_currentDownloadMode);

        AddLog(L.Get("Ruku_QueryingDepots"));
        DepotQueryResult? queryResult = null;
        try
        {
            queryResult = await _depotService.QueryAppAsync(appId, ct);
        }
        catch (TimeoutException ex)
        {
            // 只有超时才补一次：用户取消走下面的 OCE 分支直接停，不再浪费一次查询
            AddLog(L.Get("Ruku_QueryRetry", ex.Message));
            try
            {
                queryResult = await _depotService.QueryAppAsync(appId, ct);
            }
            catch (TimeoutException ex2)
            {
                AddLog(L.Get("Ruku_QueryFailedLog", ex2.Message));
                StatusMessage = L.Get("Ruku_QueryTimeoutStatus");
                return;
            }
            catch (OperationCanceledException)
            {
                // 重试中用户取消：外层 ExecuteDownloadAsync 会报"入库已取消"，这里直接透出
                throw;
            }
            catch (Exception ex2)
            {
                AddLog(L.Get("Ruku_QueryFailedLog", ex2.InnerException?.Message ?? ex2.Message));
                StatusMessage = L.Get("Ruku_QueryFailedStatus");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            AddLog(L.Get("Ruku_QueryCancelled"));
            StatusMessage = L.Get("Ruku_QueryCancelled");
            return;
        }
        catch (Exception ex)
        {
            AddLog(L.Get("Ruku_QueryErrorLog", ex.InnerException?.Message ?? ex.Message));
            StatusMessage = L.Get("Ruku_QueryFailedStatus");
            return;
        }

        if (queryResult == null)
        {
            var reason = await GetEmptyDepotsReasonAsync(appId);
            AddLog(L.Get("Ruku_QueryFailedLog", reason));
            StatusMessage = L.Get("Ruku_QueryFailedStatus");
            return;
        }
        AddLog(L.Get("Ruku_QueryDoneLog", queryResult.AppName));
        AddLog(L.Get("Ruku_MainDepots", queryResult.GameDepots.Count));
        AddLog(L.Get("Ruku_TotalDlc", queryResult.DlcAppIds.Count));

        // SteamKit2 兜底后主仓库仍为空：token 保护已在兜底中处理，
        // 剩余成因为未发售或数据暂缺，继续生成只会得到无密钥的空清单，直接终止
        if (queryResult.GameDepots.Count == 0)
        {
            var reason = await GetEmptyDepotsReasonAsync(appId);
            AddLog(L.Get("Ruku_GenAborted", reason));
            StatusMessage = L.Get("Ruku_GenFailed");
            return;
        }

        AddLog(L.Get("Ruku_DownloadingKeys"));
        var keyReady = await _depotService.EnsureKeyFilesAsync(ct);
        if (!keyReady)
        {
            AddLog(L.Get("Ruku_KeyDownloadFailed"));
            StatusMessage = L.Get("Ruku_KeyDownloadFailed");
            return;
        }
        AddLog(L.Get("Ruku_KeysReady"));

        AddLog(L.Get("Ruku_GeneratingLua"));
        string? luaPath;
        int includedDlc = -1;
        try
        {
            if (IncludeDlc && queryResult.DlcAppIds.Count > 0)
            {
                // DLC 信息获取进度（每 5 个一报，收尾必报），长等待不再零日志
                var dlcProgress = new Progress<(int Done, int Total)>(p =>
                {
                    if (p.Done == p.Total || p.Done % 5 == 0)
                        AddLog(L.Get("Ruku_DlcProgress", p.Done, p.Total));
                });
                luaPath = await _depotService.GenerateLuaWithDlcAsync(appId, ct, pinManifest: PinManifest, dlcProgress: dlcProgress, fetchNames: FetchNameComments);
                if (!string.IsNullOrEmpty(luaPath) && File.Exists(luaPath))
                {
                    var content = await File.ReadAllTextAsync(luaPath);
                    var included = queryResult.DlcAppIds.Count(id =>
                        Regex.IsMatch(content, $@"\badd(?:app|token)id\(\s*{id}\s*[,\)]", RegexOptions.IgnoreCase));
                    includedDlc = included;
                    AddLog(L.Get("Ruku_DlcIncluded", included, queryResult.DlcAppIds.Count));
                    if (included < queryResult.DlcAppIds.Count)
                        AddLog(L.Get("Ruku_DlcMissingKeys", queryResult.DlcAppIds.Count - included));
                }
                else
                {
                    AddLog(L.Get("Ruku_DlcIncluded", queryResult.DlcAppIds.Count, queryResult.DlcAppIds.Count));
                }
            }
            else
            {
                if (!IncludeDlc && queryResult.DlcAppIds.Count > 0)
                    AddLog(L.Get("Ruku_DlcSkipped", queryResult.DlcAppIds.Count));
                luaPath = await _depotService.GenerateLuaAsync(appId, ct, pinManifest: PinManifest, fetchNames: FetchNameComments);
            }
        }
        catch (InvalidOperationException ex)
        {
            AddLog(ex.Message);
            StatusMessage = L.Get("Ruku_KeyNotRecorded");
            return;
        }

        if (string.IsNullOrEmpty(luaPath))
        {
            AddLog(L.Get("Ruku_GenLuaFailed"));
            StatusMessage = L.Get("Ruku_GenFailed");
            return;
        }

        AddLog(L.Get("Ruku_LuaSaved", luaPath));
        AddLog(L.Get("Ruku_ImportSuccessLog", Path.GetFileName(luaPath)));
        StatusMessage = includedDlc >= 0 && includedDlc < queryResult.DlcAppIds.Count
            ? L.Get("Ruku_ImportSuccessPartial", queryResult.AppName, includedDlc, queryResult.DlcAppIds.Count)
            : L.Get("Ruku_ImportSuccess", queryResult.AppName);
    }

    private async Task ExecuteRemoteDownloadAsync(string gameId, string luaFolder, CancellationToken ct)
    {
        AddLog(L.Get("Ruku_FetchingUrl"));
        var shortCode = await GetShortCodeAsync(gameId, ct);
        if (string.IsNullOrEmpty(shortCode))
        {
            AddLog(L.Get("Ruku_ShortCodeFailed"));
            StatusMessage = L.Get("Ruku_ShortCodeFailed");
            return;
        }
        AddLog(L.Get("Ruku_GotShortCode", shortCode));

        AddLog(L.Get("Ruku_StartDownload"));
        // 固定名 zip 会被并发/残留互覆盖，Guid 隔离；finally 必删
        var zipPath = Path.Combine(Path.GetTempPath(), $"steam_lua_{gameId}_{Guid.NewGuid():N}.zip");
        var luaCount = 0;
        try
        {
            var success = await DownloadFileAsync(shortCode, zipPath, ct);
            if (!success)
            {
                AddLog(L.Get("Ruku_DownloadFailed"));
                StatusMessage = L.Get("Ruku_DownloadFailed");
                return;
            }
            AddLog(L.Get("Ruku_DownloadDone"));

            AddLog(L.Get("Ruku_Extracting"));
            luaCount = ExtractLuaFiles(zipPath, luaFolder);
            AddLog(L.Get("Ruku_CleanTemp"));
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }

        if (luaCount > 0)
        {
            AddLog(L.Get("Ruku_ImportDoneLog", luaCount));
            StatusMessage = L.Get("Ruku_ImportDoneStatus", luaCount);
        }
        else
        {
            AddLog(L.Get("Ruku_NoLuaFoundLog"));
            StatusMessage = L.Get("Ruku_NoLuaFoundStatus");
        }
    }

    private async Task<string?> GetShortCodeAsync(string gameId, CancellationToken ct)
    {
        var targetUrl = $"https://steamgames554.s3.us-east-1.amazonaws.com/{gameId}.zip";
        var payload = new Dictionary<string, string> { { "url", targetUrl } };

        return await RetryAsync(async () =>
        {
            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                "script-remote-download",
                TimeSpan.FromSeconds(30),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://short.walftech.com/api_create_link.php")
                    {
                        Content = new FormUrlEncodedContent(payload)
                    };
                    request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
                    request.Headers.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36 Edg/148.0.0.0");
                    request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"148\", \"Microsoft Edge\";v=\"148\", \"Not/A)Brand\";v=\"99\"");
                    request.Headers.TryAddWithoutValidation("accept", "*/*");
                    request.Headers.TryAddWithoutValidation("origin", "https://remlua.com");
                    request.Headers.TryAddWithoutValidation("referer", "https://remlua.com/");
                    return await client.SendAsync(request);
                });
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("short_code").GetString();
            }
            catch (Exception ex)
            {
                AddLog(L.Get("Ruku_ShortCodeParseFailed", ex.GetType().Name, ex.Message));
                AddLog(L.Get("Ruku_ResponsePreview", json[..Math.Min(json.Length, 500)]));
                return null;
            }
        }, L.Get("Ruku_StepShortCode"), ct);
    }

    private async Task<bool> DownloadFileAsync(string shortCode, string savePath, CancellationToken ct)
    {
        var proxyUrl = $"https://short.walftech.com/proxy.php?short={shortCode}";
        return await RetryAsync(async () =>
        {
            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                "script-remote-download",
                TimeSpan.FromSeconds(60),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, proxyUrl);
                    request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"148\", \"Microsoft Edge\";v=\"148\", \"Not/A)Brand\";v=\"99\"");
                    request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
                    request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
                    request.Headers.TryAddWithoutValidation("upgrade-insecure-requests", "1");
                    request.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
                    request.Headers.TryAddWithoutValidation("sec-fetch-mode", "navigate");
                    request.Headers.TryAddWithoutValidation("sec-fetch-user", "?1");
                    request.Headers.TryAddWithoutValidation("sec-fetch-dest", "document");
                    request.Headers.TryAddWithoutValidation("referer", $"https://short.walftech.com/?id={shortCode}");
                    request.Headers.TryAddWithoutValidation("accept-language", "zh-CN,zh;q=0.9");
                    request.Headers.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36 Edg/148.0.0.0");
                    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                });
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
            await contentStream.CopyToAsync(fileStream);
            fileStream.Flush(true);
            return true;
        }, L.Get("Ruku_StepDownload"), ct);
    }

    // 只管退避等待与 attempt 间检查：在飞请求仍靠原有 60s/15s 超时，不动代理 helper；
    // 取消异常必须直接透出，否则取消会变成重试
    private async Task<T> RetryAsync<T>(Func<Task<T>> action, string stepName, CancellationToken ct, int maxRetries = 3)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await action();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 只有用户取消才透出；在飞请求自身超时（TaskCanceledException）走下面重试
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt >= maxRetries)
                    break;

                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                AddLog(L.Get("Ruku_RetryLog", stepName, attempt, ex.GetType().Name, ex.Message, delay.TotalSeconds));
                await Task.Delay(delay, ct);
            }
        }
        AddLog(L.Get("Ruku_RetryGaveUp", stepName, maxRetries));
        throw new HttpRequestException(L.Get("Ruku_RetryThrow", stepName), lastException);
    }

    private int ExtractLuaFiles(string zipPath, string targetDir)
    {
        int count = 0;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                {
                    var destPath = Path.Combine(targetDir, Path.GetFileName(entry.FullName));
                    entry.ExtractToFile(destPath, overwrite: true);
                    count++;
                    AddLog(L.Get("Ruku_ImportedEntry", Path.GetFileName(entry.FullName)));
                }
            }
        }
        File.Delete(zipPath);
        return count;
    }

    private const int MaxLogLines = 500;

    private void AddLog(string message)
    {
        LogService.Info(L.Get("LogCat_Home"), message);
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        // 异步投递不阻塞工作线程；顺序与调用序一致
        _ = Application.Current.Dispatcher.InvokeAsync(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > MaxLogLines)
                LogLines.RemoveAt(0);
        });
    }

    [RelayCommand]
    private async Task RefreshKeyCacheAsync()
    {
        if (IsDownloading) return;
        try
        {
            IsDownloading = true;
            _depotService.UseDataSource(_currentDownloadMode);
            AddLog(L.Get("Ruku_UpdatingKeys", CurrentDataSourceLabel));
            var updateResult = await _depotService.UpdateKeyFilesAsync();

            if (updateResult.Success)
            {
                var depotDelta = updateResult.DepotKeysNewCount - updateResult.DepotKeysOldCount;
                var tokenDelta = updateResult.TokenKeysNewCount - updateResult.TokenKeysOldCount;

                AddLog(L.Get("Ruku_SourceStatusTitle", CurrentDataSourceLabel));
                AddLog(L.Get("Ruku_DepotKeysUpdated", updateResult.DepotKeysOldCount, updateResult.DepotKeysNewCount, depotDelta >= 0 ? "+" : "", depotDelta));
                AddLog(L.Get("Ruku_TokenKeysUpdated", updateResult.TokenKeysOldCount, updateResult.TokenKeysNewCount, tokenDelta >= 0 ? "+" : "", tokenDelta));
                StatusMessage = L.Get("Ruku_KeysUpdatedStatus", CurrentDataSourceLabel);
                OnPropertyChanged(nameof(LastUpdateTimeText));
            }
            else
            {
                AddLog(L.Get("Ruku_UpdateFailedLog"));
                StatusMessage = L.Get("Ruku_UpdateFailedStatus");
            }
        }
        catch (Exception ex)
        {
            AddLog(L.Get("Ruku_UpdateErrorLog", ex.Message));
            StatusMessage = L.Get("Ruku_UpdateFailedStatus");
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
        SearchResults.Clear();
        // 搜过标记也要复位，否则“未找到匹配”空提示会赖着不走
        ResetSearchState();
        StatusMessage = L.Get("Ruku_LogCleared");
    }
}
