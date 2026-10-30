using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

	public partial class MainViewModel : ObservableObject, IDisposable
{

	private readonly ISteamPathService _steamPathService;
	private readonly ILuaFileManager _luaFileManager;
	private readonly ISteamApiService _steamApiService;
	private readonly ISettingsService _settingsService;
	private readonly ISteamManifestService _steamManifestService;
	private readonly ISteamDepotService _steamDepotService;
	private readonly IHttpClientProvider _httpClientProvider;
	private readonly IDialogService _dialogService;
	private readonly IArchiveImportService _archiveImport;
	private List<GameInfo> _allGames = new();
	private CancellationTokenSource? _refreshCts;
	private CancellationTokenSource? _dlcQueryCts;
	private DispatcherTimer? _progressTimer;
	private DispatcherTimer? _searchDebounceTimer;

	[ObservableProperty]
	private ObservableCollection<GameInfo> _games = new();

	[ObservableProperty]
	private string _searchText = string.Empty;

	[ObservableProperty]
	private string _statusText = string.Empty;

	[ObservableProperty]
	private string _steamPath = string.Empty;

	[ObservableProperty]
	private string _openSteamToolStatus = string.Empty;

	[ObservableProperty]
	private bool _isAutoRefreshEnabled = true;

	[ObservableProperty]
	private bool _isCardRefreshVisible = true;

	[ObservableProperty]
	private bool _isRefreshing;

	// 后台正在获取封面/名字：不阻塞操作，仅驱动顶部内联提示与取消按钮
	[ObservableProperty]
	private bool _isFetchingInfo;

	[ObservableProperty]
	private string _selectedSortOption = "NameAsc";

	[ObservableProperty]
	private string _selectedDisableFilter = "All";

	[ObservableProperty]
	private string _selectedViewMode = "Card";

	// 入库旧存档里是中文值（卡片/全部游戏/名称 A-Z…），读出来归一化成英文码
	private static string NormalizeViewMode(string? v) => v switch
	{
		"列表" or "列表模式" or "List" => "List",
		"表格" or "Table" => "Table",
		_ => "Card"
	};

	[ObservableProperty]
	private bool _isRefreshSlow;

	[ObservableProperty]
	private string _refreshProgressText = string.Empty;

	[ObservableProperty]
	private string _selectionSummary = string.Empty;

	[ObservableProperty]
	private bool _isSelectionMode;

	[ObservableProperty]
	private bool _isDlcQueryOverlayVisible;

	[ObservableProperty]
	private string _dlcQueryOverlayText = string.Empty;

	[ObservableProperty]
	private string _statusMessage = string.Empty;

	[ObservableProperty]
	private bool _isKernelUpdateBannerVisible;

	[ObservableProperty]
	private string _kernelUpdateBannerText = string.Empty;

	private Timer? _statusMessageTimer;

	partial void OnStatusMessageChanged(string value)
	{
		_statusMessageTimer?.Dispose();
		if (!string.IsNullOrEmpty(value))
		{
			LogService.Info(L.Get("LogCat_Home"), value);
			_statusMessageTimer = new Timer(_ => Application.Current.Dispatcher.Invoke(() => StatusMessage = string.Empty),
				null, 3000, Timeout.Infinite);
		}
	}

	[RelayCommand]
	private void CancelDlcQuery()
	{
		_dlcQueryCts?.Cancel();
	}

	partial void OnIsSelectionModeChanged(bool value)
	{
		if (!value)
		{
			_batchSelecting = true;
			try
			{
				foreach (var game in _filteredCache)
					game.IsSelected = false;
			}
			finally { _batchSelecting = false; }
			NotifySelectionChanged();
		}
	}

	public void NotifySelectionChanged()
	{
		// 选中态挂在 GameInfo 上与 _filteredCache 同引用，计数按筛选全量而非当前页
		var count = _filteredCache.Count(g => g.IsSelected);
		SelectionSummary = L.Get("Home_SelectedCount", count, _filteredCache.Count);
	}

	// 手动勾选 CheckBox 只改 GameInfo 不经过 VM，订阅选中变化实时刷新计数；
	// 批量设置时屏蔽事件逐条触发（千级库 O(N²)），末尾手动 Notify 一次
	private bool _batchSelecting;

	private void ResubscribeSelectionTracking(List<GameInfo> oldGames, List<GameInfo> newGames)
	{
		if (!ReferenceEquals(oldGames, newGames))
		{
			foreach (var g in oldGames)
				g.PropertyChanged -= OnGameIsSelectedChanged;
		}
		foreach (var g in newGames)
		{
			g.PropertyChanged -= OnGameIsSelectedChanged;
			g.PropertyChanged += OnGameIsSelectedChanged;
		}
	}

	private void OnGameIsSelectedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (_batchSelecting) return;
		if (e.PropertyName == nameof(GameInfo.IsSelected))
			NotifySelectionChanged();
	}

	[RelayCommand]
	private void ToggleSelectionMode()
	{
		IsSelectionMode = !IsSelectionMode;
	}

	private string GetCurrentCdnName()
	{
		var index = _steamApiService.SelectedCdnIndex;
		var defaults = CdnEndpoint.Defaults;
		if (index >= 0 && index < defaults.Count)
			return L.Get(CdnEndpoint.GetDisplayKey(defaults[index].Name));
		return L.Get("Home_UnknownNode");
	}

	public MainViewModel(
		ISteamPathService steamPathService,
		ILuaFileManager luaFileManager,
		ISteamApiService steamApiService,
		ISettingsService settingsService,
		ISteamManifestService steamManifestService,
		ISteamDepotService steamDepotService,
		IHttpClientProvider httpClientProvider,
		IDialogService dialogService,
		IArchiveImportService archiveImport)
	{
		_steamPathService = steamPathService;
		_luaFileManager = luaFileManager;
		_steamApiService = steamApiService;
		_settingsService = settingsService;
		_steamManifestService = steamManifestService;
		_steamDepotService = steamDepotService;
		_httpClientProvider = httpClientProvider;

		_dialogService = dialogService;
		_archiveImport = archiveImport;
		_luaFileManager.FilesChanged += OnFilesChanged;
		LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
		WeakReferenceMessenger.Default.Register<LuaFolderChangedMessage>(this, (_, _) => OnRefreshRequested());
		WeakReferenceMessenger.Default.Register<KernelUpdateAvailableMessage>(this, (_, m) => OnKernelUpdateAvailable(m));

		var settings = settingsService.Load();
		IsAutoRefreshEnabled = settings.AutoRefreshEnabled;
		IsCardRefreshVisible = settings.IsCardRefreshVisible;
		SelectedViewMode = NormalizeViewMode(settings.SelectedViewMode);
		if (!string.IsNullOrEmpty(settings.SteamPath))
			steamPathService.SetCustomPath(settings.SteamPath);
	}

	public void Dispose()
	{
		var refreshCts = _refreshCts;
		_refreshCts = null;
		refreshCts?.Cancel();
		refreshCts?.Dispose();
		var filterCts = _filterCts;
		_filterCts = null;
		filterCts?.Cancel();
		filterCts?.Dispose();
		_progressTimer?.Stop();
		_progressTimer = null;
		_statusMessageTimer?.Dispose();
		_statusMessageTimer = null;
		_searchDebounceTimer?.Stop();
		_searchDebounceTimer = null;
		_luaFileManager.FilesChanged -= OnFilesChanged;
		LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;
		WeakReferenceMessenger.Default.Unregister<LuaFolderChangedMessage>(this);
		WeakReferenceMessenger.Default.Unregister<KernelUpdateAvailableMessage>(this);
	}

	private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(LocalizationService.CurrentLanguage))
			OnRefreshRequested();
	}

	private void OnRefreshRequested()
	{
		// fire-and-forget 不需要等 UI 执行完；同步 Invoke 在关闭期会堵池线程甚至抛，改异步投递
		_ = Application.Current.Dispatcher.InvokeAsync(() => { _ = RefreshGamesAsync(); });
	}

	// 检测仅在启动时跑一次，横幅关闭后本次启动不再显示
	private void OnKernelUpdateAvailable(KernelUpdateAvailableMessage msg)
	{
		Application.Current.Dispatcher.Invoke(() =>
		{
			KernelUpdateBannerText = L.Get("Home_KernelUpdate", msg.RemoteVersion, msg.LocalVersion);
			IsKernelUpdateBannerVisible = true;
		});
	}

	[RelayCommand]
	private void DismissKernelUpdateBanner()
	{
		IsKernelUpdateBannerVisible = false;
	}

	[RelayCommand]
	private async Task LoadedAsync()
	{
		var settings = _settingsService.Load();
		var detectedPath = _steamPathService.DetectSteamPath();
		SteamPath = !string.IsNullOrEmpty(settings.SteamPath)
			? settings.SteamPath
			: detectedPath ?? L.Get("Home_SteamNotFound");
		OpenSteamToolStatus = _steamPathService.DetectSteamToolType() switch
		{
			SteamToolType.OpenSteamTool => L.Get("Home_KernelOST"),
			SteamToolType.SteamTools => L.Get("Home_KernelMismatch", nameof(SteamToolType.SteamTools)),
			SteamToolType.GreenLuma => L.Get("Home_KernelMismatch", nameof(SteamToolType.GreenLuma)),
			_ => L.Get("Home_KernelMissing")
		};
		await RefreshGamesAsync();
		if (IsAutoRefreshEnabled)
			_luaFileManager.StartWatching();
	}

	[RelayCommand]
	private async Task RefreshGamesAsync()
	{
		if (IsRefreshing) return;
		CancelFetch();

		IsRefreshing = true;
		var scanOk = false;
		try
		{
			var oldGames = _allGames;
			_allGames = await _luaFileManager.ScanLuaFilesAsync();
			ResubscribeSelectionTracking(oldGames, _allGames);
			await Task.Run(() => _steamApiService.PopulateFromCache(_allGames));
			await ApplyFilterAsync();
			UpdateStatus();
			scanOk = true;
		}
		catch (Exception ex) { StatusText = L.Get("Home_RefreshFailed", ex.Message); LogService.Error(L.Get("LogCat_Home"), L.Get("Home_LogRefreshFailed", ex)); }
		finally
		{
			IsRefreshing = false;
		}

		if (scanOk)
			_ = StartBackgroundFetchAsync();
	}

	// 封面/名字后台获取：首屏已绘制，本方法立即返回；按 AppId 快照工作，
	// 回填只写仍在列表中的对象，中途删游戏/新一轮刷新不会互相踩
	private async Task StartBackgroundFetchAsync()
	{
		// 大库首次询问：筛选全量超阈值且没问过时让用户选自动还是手动，只打扰一次；
		// 注意用 _filteredCache 而非 Games（Games 只是当前页，最多 20 个）
		var settings = _settingsService.Load();
		if (settings.AutoFetchCovers && !settings.CoverFetchAsked && _filteredCache.Count >= 500)
		{
			var auto = await ShowModernConfirmAsync(
				L.Get("Home_BigLibraryTitle"),
				L.Get("Home_BigLibraryBody", _filteredCache.Count, L.Get("Home_BigLibraryAuto"), L.Get("Home_BigLibraryManual")),
				L.Get("Home_BigLibraryAuto"), L.Get("Home_BigLibraryManual"));
			settings.CoverFetchAsked = true;
			if (!auto)
			{
				settings.AutoFetchCovers = false;
				StatusText = L.Get("Home_CoversManualOff");
				LogService.Info(L.Get("LogCat_Home"), L.Get("Home_LogManualCovers", _filteredCache.Count));
			}
			_settingsService.Save(settings);
		}
		StartBackgroundFetch();
	}

	private void StartBackgroundFetch()
	{
		CancelFetch();
		_refreshCts = new CancellationTokenSource();
		var token = _refreshCts.Token;
		var snapshot = _allGames.ToList();

		IsFetchingInfo = true;
		IsRefreshSlow = false;
		RefreshProgressText = L.Get("Home_FetchingInfo");
		StartProgressTimer();
		_ = SlowTimerAsync(token);
		_ = FetchGameInfoBackgroundAsync(snapshot, token);
	}

	private async Task FetchGameInfoBackgroundAsync(List<GameInfo> snapshot, CancellationToken token)
	{
		try
		{
			await _steamApiService.RefreshGameInfoAsync(snapshot, token, _settingsService.Load().AutoFetchCovers);
			if (token.IsCancellationRequested) return;

			var backfills = await Task.WhenAll(snapshot.Select(g => _luaFileManager.ParseLuaFileAsync(g.AppId)));
			if (token.IsCancellationRequested) return;
			foreach (var (game, refreshed) in snapshot.Zip(backfills))
			{
				if (refreshed != null)
				{
					game.IsManifestPinned = refreshed.IsManifestPinned;
					game.Token = refreshed.Token;
				}
			}
			// 不重建分页（回填走绑定逐项更新），仅刷新计数
			UpdateStatus();
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { StatusText = L.Get("Home_RefreshFailed", ex.Message); LogService.Error(L.Get("LogCat_Home"), L.Get("Home_BackgroundFetchFailed", ex)); }
		finally
		{
			if (_refreshCts?.Token == token)
			{
				var wasCancelled = token.IsCancellationRequested;
				CancelFetch();
				IsFetchingInfo = false;
				StopProgressTimer();
				IsRefreshSlow = false;
				RefreshProgressText = wasCancelled ? L.Get("Home_FetchCancelled") : L.Get("Home_FetchedTotal", _allGames.Count);
			}
		}
	}

	private void CancelFetch()
	{
		try { _refreshCts?.Cancel(); } catch { }
		try { _refreshCts?.Dispose(); } catch { }
		_refreshCts = null;
	}

	[RelayCommand]
	private void CancelRefresh()
	{
		_refreshCts?.Cancel();
	}

	private async Task SlowTimerAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(20000, token);
			if (IsFetchingInfo)
			{
				await Application.Current.Dispatcher.InvokeAsync(() =>
				{
					IsRefreshSlow = true;
					StartProgressTimer();
				});
			}
		}
		catch (OperationCanceledException) { }
		catch (Exception ex) { LogService.Warn(L.Get("LogCat_Home"), L.Get("Home_SlowTipError", ex.Message)); }
	}

	private void StartProgressTimer()
	{
		StopProgressTimer();
		UpdateProgressText();
		_progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		_progressTimer.Tick += (_, _) => UpdateProgressText();
		_progressTimer.Start();
	}

	private void StopProgressTimer()
	{
		if (_progressTimer != null)
		{
			_progressTimer.Stop();
			_progressTimer = null;
		}
	}

	private void UpdateProgressText()
	{
		if (_allGames.Count == 0)
		{
			if (!string.IsNullOrEmpty(RefreshProgressText))
				RefreshProgressText = L.Get("Home_Fetching", GetCurrentCdnName());
			return;
		}
		var done = _allGames.Count(g => !string.IsNullOrEmpty(g.CoverImagePath));
		RefreshProgressText = IsRefreshSlow
			? L.Get("Home_FetchedSlow", done, _allGames.Count)
			: L.Get("Home_FetchedProgress", done, _allGames.Count, GetCurrentCdnName());
	}

	private async Task QuickRefreshAsync()
	{
		if (IsRefreshing) return;
		CancelFetch();

		IsRefreshing = true;
		var scanOk = false;
		try
		{
			var newGames = await _luaFileManager.ScanLuaFilesAsync();
			var oldGames = _allGames;
			_allGames = newGames;
			ResubscribeSelectionTracking(oldGames, _allGames);
			await Task.Run(() => _steamApiService.PopulateFromCache(_allGames));
			await ApplyFilterAsync();
			UpdateStatus();
			scanOk = true;
		}
		catch (Exception ex) { StatusText = L.Get("Home_RefreshFailed", ex.Message); LogService.Error(L.Get("LogCat_Home"), L.Get("Home_LogQuickRefreshFailed", ex)); }
		finally
		{
			IsRefreshing = false;
		}

		if (scanOk)
			_ = StartBackgroundFetchAsync();
	}

	partial void OnSearchTextChanged(string value)
	{
		_searchDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
		_searchDebounceTimer.Stop();
		_searchDebounceTimer.Tick -= SearchDebounceTimer_Tick;
		_searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
		_searchDebounceTimer.Start();
	}

	private void SearchDebounceTimer_Tick(object? sender, EventArgs e)
	{
		_searchDebounceTimer?.Stop();
		_ = ApplyFilterAsync();
	}

	partial void OnSelectedSortOptionChanged(string value) { _ = ApplyFilterAsync(); }
	partial void OnSelectedDisableFilterChanged(string value) { _ = ApplyFilterAsync(); }
	partial void OnSelectedViewModeChanged(string value)
	{
		var settings = _settingsService.Load();
		settings.SelectedViewMode = value;
		_settingsService.Save(settings);
	}

	// 分页渐进：固定每页 20 个，首屏不满视口时由视图多次补足直到填满；
	// 滚动到底部前追加下一页，避免千级列表一次性实例化卡死 UI
	private const int GamesPageSize = 20;
	private List<GameInfo> _filteredCache = new();

	public void LoadMoreGames()
	{
		if (Games.Count >= _filteredCache.Count) return;
		foreach (var game in _filteredCache.Skip(Games.Count).Take(GamesPageSize))
			Games.Add(game);
	}

	private CancellationTokenSource? _filterCts;
	private int _filterVersion;

	// 过滤排序放池线程：快照参数+版本号+源引用三重 guard，过期结果直接丢弃；
	// 集合替换与状态通知仍回 UI 线程，GameInfo 跨线程读与现有后台回填一致
	private async Task ApplyFilterAsync()
	{
		var version = Interlocked.Increment(ref _filterVersion);
		_filterCts?.Cancel();
		_filterCts?.Dispose();
		_filterCts = new CancellationTokenSource();
		var ct = _filterCts.Token;
		var query = SearchText?.Trim() ?? string.Empty;
		var disableFilter = SelectedDisableFilter;
		var sortOption = SelectedSortOption;
		var source = _allGames;
		List<GameInfo> filtered;
		try
		{
			filtered = await Task.Run(() =>
			{
				IEnumerable<GameInfo> q = string.IsNullOrWhiteSpace(query)
					? source
					: source.Where(g =>
					{
						var nameMatch = g.GameName.Contains(query, StringComparison.OrdinalIgnoreCase);
						var idMatch = g.AppId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
						return nameMatch || idMatch;
					});

				q = disableFilter switch
				{
					"Enabled" => q.Where(g => !g.IsDisabled),
					"Disabled" => q.Where(g => g.IsDisabled),
					_ => q
				};

				q = sortOption switch
				{
					"NameDesc" => q.OrderByDescending(g => g.GameName),
					"AppIdAsc" => q.OrderBy(g => g.AppId),
					"AppIdDesc" => q.OrderByDescending(g => g.AppId),
					"TimeAsc" => q.OrderBy(g => g.LuaFileTime),
					"TimeDesc" => q.OrderByDescending(g => g.LuaFileTime),
					_ => q.OrderBy(g => g.GameName)
				};

				return q.ToList();
			}, ct);
		}
		catch (OperationCanceledException) { return; }
		if (version != _filterVersion || !ReferenceEquals(_allGames, source)) return;
		_filteredCache = filtered;
		Games = new ObservableCollection<GameInfo>(_filteredCache.Take(GamesPageSize));
		NotifySelectionChanged();
		UpdateStatus();
	}

	private void UpdateStatus() => StatusText = L.Get("Home_GameCount", _filteredCache.Count);

	private Task ShowModernDialogAsync(string title, string message)
		=> _dialogService.ShowAlertAsync(title, message);

	private Task<bool> ShowModernConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null)
		=> _dialogService.ShowConfirmAsync(title, message, primaryText ?? L.Get("Common_Confirm"), closeText ?? L.Get("Common_Cancel"));

	private async void OnFilesChanged(object? sender, EventArgs e)
	{
		await Application.Current.Dispatcher.InvokeAsync(async () => await QuickRefreshAsync());
	}

	[RelayCommand]
	private async Task AddFilesAsync()
	{
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = L.Get("Home_FileDialogFilter"),
			Multiselect = true,
			Title = L.Get("Home_FileDialogTitle")
		};

		if (dialog.ShowDialog() == true)
		{
			var luaCount = 0;
			var binCount = 0;
			var manifestCount = 0;
			var msgs = new List<string>();
			foreach (var file in dialog.FileNames)
			{
				if (_archiveImport.IsArchive(file))
				{
					msgs.AddRange(await ImportArchiveFileAsync(file));
					continue;
				}
				try
				{
					if (file.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
					{
						await _luaFileManager.AddLuaFileAsync(file);
						luaCount++;
					}
					else if (file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
					{
						await _luaFileManager.AddBinFileAsync(file);
						binCount++;
					}
					else if (file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
					{
						await _luaFileManager.AddManifestFileAsync(file);
						manifestCount++;
					}
				}
				catch (Exception ex) { StatusText = L.Get("Home_AddFailed", ex.Message); LogService.Error(L.Get("LogCat_Home"), L.Get("Home_LogAddFailed", ex)); }
			}
		if (luaCount > 0) msgs.Add(L.Get("Home_ImportGameOk", luaCount));
		if (binCount > 0) msgs.Add(L.Get("Home_ImportBinOk", binCount));
		if (manifestCount > 0) msgs.Add(L.Get("Home_ImportManifestOk", manifestCount));
		if (msgs.Count > 0)
		{
			StatusMessage = string.Join("，", msgs);
			LogService.Info(L.Get("LogCat_Home"), string.Join("，", msgs));
		}
			await QuickRefreshAsync();
		}
	}

	// 压缩包：解压后按内容计入三类；同名跳过与单文件失败如实报告
	private async Task<List<string>> ImportArchiveFileAsync(string file)
	{
		var lines = new List<string>();
		try
		{
			var r = await _archiveImport.ImportAsync(file, null);
		if (r.LuaCount > 0) lines.Add(L.Get("Home_ImportGameOk", r.LuaCount));
		if (r.BinCount > 0) lines.Add(L.Get("Home_ImportBinOk", r.BinCount));
		if (r.ManifestCount > 0) lines.Add(L.Get("Home_ImportManifestOk", r.ManifestCount));
		if (r.SkippedCount > 0) lines.Add(L.Get("Home_ImportSkip", r.SkippedCount, string.Join("、", r.SkippedFiles.Take(5)), r.SkippedFiles.Count > 5 ? "…" : ""));
		foreach (var f in r.FailedFiles) lines.Add(L.Get("Home_ImportFailFile", f));
		if (lines.Count == 0) lines.Add(L.Get("Home_ImportArchiveEmpty"));
	}
	catch (Exception ex) { StatusText = L.Get("Home_ArchiveImportFailed", ex.Message); LogService.Error(L.Get("LogCat_Home"), L.Get("Home_ArchiveImportFailed", ex)); }
		return lines;
	}

	[RelayCommand]
	private async Task DeleteGameAsync(GameInfo? game)
	{
		if (game == null) return;

		if (game.IsDisabled)
		{
			await ShowModernDialogAsync(L.Get("Home_OpBlocked"), L.Get("Home_DisabledDeleteWarn"));
			return;
		}

		var confirmed = await ShowModernConfirmAsync(
			L.Get("Home_ConfirmDelete"),
			L.Get("Home_ConfirmDeleteBody", game.GameName, game.AppId),
			L.Get("Home_Delete"));

		if (confirmed)
		{
			try { await _luaFileManager.DeleteLuaFileAsync(game.AppId); await QuickRefreshAsync(); }
			catch (Exception ex) { StatusText = L.Get("Home_DeleteFailed", ex.Message); LogService.Error(L.Get("LogCat_Home"), L.Get("Home_LogDeleteFailed", game.GameName, game.AppId, ex)); }
		}
	}

	[RelayCommand]
	private async Task ToggleGameDisableAsync(GameInfo? game)
	{
		if (game == null) return;

		if (game.IsDisabled)
			await _luaFileManager.EnableGameAsync(game.AppId);
		else
			await _luaFileManager.DisableGameAsync(game.AppId);

		await QuickRefreshAsync();
	}

	// 手动获取已下线：统一引导去「清单监听」，成功率高还不用操心版本
	[RelayCommand]
	private async Task FetchManifestsAsync(GameInfo? game)
	{
		if (game == null) return;
		var go = await ShowModernConfirmAsync(
			L.Get("Home_RecommendMonitor"),
			L.Get("Home_MonitorRetiredBody", L.Get("Nav_Manifest"), L.Get("Home_MonitorTab")),
			L.Get("Home_GoMonitor"), L.Get("Home_GotIt"));
		if (!go) return;
		try
		{
			if (Application.Current.MainWindow is Views.MainWindow main)
				main.NavigateTo("Manifest");
		}
		catch (Exception ex) { LogService.Warn(L.Get("LogCat_Home"), L.Get("Home_NavMonitorFail", ex.Message)); }
	}

	[RelayCommand]
	private void SelectAll()
	{
		// 全选=筛选结果全量，不只是当前页
		_batchSelecting = true;
		try
		{
			foreach (var game in _filteredCache)
				game.IsSelected = true;
		}
		finally { _batchSelecting = false; }
		NotifySelectionChanged();
	}

	[RelayCommand]
	private void ClearSelection()
	{
		_batchSelecting = true;
		try
		{
			foreach (var game in _filteredCache)
				game.IsSelected = false;
		}
		finally { _batchSelecting = false; }
		NotifySelectionChanged();
	}

	[RelayCommand]
	private async Task BatchEnableAsync()
	{
		var selected = _filteredCache.Where(g => g.IsSelected && g.IsDisabled).ToList();
		if (selected.Count == 0)
		{
			await ShowModernDialogAsync(L.Get("Home_BatchEnableTitle"), L.Get("Home_BatchEnableEmpty"));
			return;
		}
		foreach (var game in selected)
			await _luaFileManager.EnableGameAsync(game.AppId);
		StatusText = L.Get("Home_BatchEnabled", selected.Count);
		LogService.Info(L.Get("LogCat_Home"), L.Get("Home_BatchEnabled", selected.Count));
		await QuickRefreshAsync();
		ClearSelection();
	}

	[RelayCommand]
	private async Task BatchDisableAsync()
	{
		var selected = _filteredCache.Where(g => g.IsSelected && !g.IsDisabled).ToList();
		if (selected.Count == 0)
		{
			await ShowModernDialogAsync(L.Get("Home_BatchDisableTitle"), L.Get("Home_BatchDisableEmpty"));
			return;
		}
		foreach (var game in selected)
			await _luaFileManager.DisableGameAsync(game.AppId);
		StatusText = L.Get("Home_BatchDisabled", selected.Count);
		LogService.Info(L.Get("LogCat_Home"), L.Get("Home_BatchDisabled", selected.Count));
		await QuickRefreshAsync();
		ClearSelection();
	}

	[RelayCommand]
	private async Task BatchDeleteAsync()
	{
		var selected = _filteredCache.Where(g => g.IsSelected && !g.IsDisabled).ToList();
		if (selected.Count == 0)
		{
			await ShowModernDialogAsync(L.Get("Home_BatchDeleteTitle"), L.Get("Home_BatchDeleteEmpty"));
			return;
		}
		var confirmed = await ShowModernConfirmAsync(
			L.Get("Home_BatchDeleteTitle"),
			L.Get("Home_BatchDeleteBody", selected.Count),
			L.Get("Home_Delete"));
		if (confirmed)
		{
			foreach (var game in selected)
				await _luaFileManager.DeleteLuaFileAsync(game.AppId);
			StatusText = L.Get("Home_BatchDeleted", selected.Count);
			LogService.Info(L.Get("LogCat_Home"), L.Get("Home_BatchDeleted", selected.Count));
			await QuickRefreshAsync();
			ClearSelection();
		}
	}

	[RelayCommand]
	private async Task EditGame(GameInfo? game)
	{
		if (game == null) return;

		if (game.IsDisabled)
		{
			await ShowModernDialogAsync(L.Get("Home_OpBlocked"), L.Get("Home_DisabledEditWarn"));
			return;
		}

		var luaFolder = _steamPathService.GetLuaFolder();
		if (string.IsNullOrEmpty(luaFolder)) return;
		var filePath = Path.Combine(luaFolder, $"{game.AppId}.lua");
		if (File.Exists(filePath))
		{
			try { Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true }); }
			catch (Exception ex) { StatusText = L.Get("Home_OpenFailed", ex.Message); LogService.Warn(L.Get("LogCat_Home"), L.Get("Home_LogOpenLuaFailed", game.GameName, ex.Message)); }
		}
	}

	[RelayCommand]
	private void OpenLuaFolder()
	{
		var luaFolder = _steamPathService.GetLuaFolder();
		if (string.IsNullOrEmpty(luaFolder) || !Directory.Exists(luaFolder))
		{
			StatusText = L.Get("Home_LuaFolderMissing");
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo { FileName = luaFolder, UseShellExecute = true });
		}
		catch (Exception ex)
		{
			StatusText = L.Get("Home_OpenFailed", ex.Message);
			LogService.Warn(L.Get("LogCat_Home"), L.Get("Home_LogOpenLuaFolderFailed", luaFolder, ex.Message));
		}
	}

	[RelayCommand]
	private async Task RefreshSingleGameAsync(GameInfo? game)
	{
		if (game == null) return;
		game.IsLoading = true;
		await _steamApiService.RefreshSingleGameAsync(game);
		game.IsLoading = false;
	}

	[RelayCommand]
	private async Task PinToLatestAsync(GameInfo? game)
	{
		if (game == null) return;

		if (game.IsDisabled)
		{
			await ShowModernDialogAsync(L.Get("Home_OpBlocked"), L.Get("Home_DisabledPinWarn"));
			return;
		}

		if (game.IsManifestPinned && game.ManifestSourceIndex == 1)
		{
			await PinUnpinAsync(game);
			return;
		}

		game.ManifestSourceIndex = 1;
		await PinUnpinAsync(game);
	}

	[RelayCommand]
	private async Task PinToCurrentAsync(GameInfo? game)
	{
		if (game == null) return;

		if (game.IsDisabled)
		{
			await ShowModernDialogAsync(L.Get("Home_OpBlocked"), L.Get("Home_DisabledPinWarn"));
			return;
		}

		if (game.IsManifestPinned && game.ManifestSourceIndex == 0)
		{
			await PinUnpinAsync(game);
			return;
		}

		var acfPath = _steamPathService.FindAppManifest(game.AppId);
		if (acfPath == null)
		{
			await ShowModernDialogAsync(L.Get("Home_PinNotInstalledTitle"), L.Get("Home_PinNotInstalledBody", game.GameName));
			return;
		}

		game.ManifestSourceIndex = 0;
		await PinUnpinAsync(game);
	}

	[RelayCommand]
	private async Task UnpinGameAsync(GameInfo? game)
	{
		if (game == null || !game.IsManifestPinned) return;
		await PinUnpinAsync(game);
	}

	private async Task PinUnpinAsync(GameInfo game)
	{
		if (game.IsManifestPinned)
		{
			await _luaFileManager.SetManifestPinAsync(game.AppId, false);
			game.ManifestSourceIndex = 0;
			StatusText = L.Get("Home_Unpinned", game.GameName);
			LogService.Info(L.Get("LogCat_Home"), L.Get("Home_Unpinned", game.GameName));
		}
		else
		{
			var manifestIds = new Dictionary<int, string>();
			var sourceName = game.ManifestSourceIndex == 0 ? L.Get("Home_SourceInstalled") : L.Get("Home_SourceLatest");

			foreach (var depot in game.Depots)
			{
				string? manifestId = null;

				if (game.ManifestSourceIndex == 0)
				{
					var acfPath = _steamPathService.FindAppManifest(game.AppId);
					if (acfPath != null)
					{
						var mounted = _steamManifestService.ParseMountedDepots(acfPath);
						mounted.TryGetValue(depot.DepotId, out manifestId);
					}
				}
				else
				{
					manifestId = await _steamManifestService.FetchLatestManifestIdAsync(game.AppId, depot.DepotId);
				}

				if (!string.IsNullOrEmpty(manifestId))
					manifestIds[depot.DepotId] = manifestId;
			}

			if (manifestIds.Count == 0)
			{
				await ShowModernDialogAsync(L.Get("Home_CannotPin"), L.Get("Home_PinNoManifest", game.GameName));
				return;
			}

			await _luaFileManager.SetManifestPinAsync(game.AppId, true, manifestIds);
			StatusText = L.Get("Home_PinnedTo", game.GameName, sourceName);
			LogService.Info(L.Get("LogCat_Home"), L.Get("Home_PinnedTo", game.GameName, sourceName) + $" (manifest: {string.Join(", ", manifestIds.Select(kv => $"{kv.Key}={kv.Value}"))})");
		}

		var refreshed = await _luaFileManager.ParseLuaFileAsync(game.AppId);
		if (refreshed != null)
		{
			game.Depots.Clear();
			foreach (var d in refreshed.Depots)
				game.Depots.Add(d);
			game.IsManifestPinned = refreshed.IsManifestPinned;
			game.Token = refreshed.Token;
		}
	}

	[RelayCommand]
	private async Task QueryDlcAsync(GameInfo? game)
	{
		if (game == null) return;

		var luaFolder = _steamPathService.GetLuaFolder();
		if (string.IsNullOrEmpty(luaFolder)) return;

		IsDlcQueryOverlayVisible = true;

		try
		{
			_dlcQueryCts = new CancellationTokenSource();
			var ct = _dlcQueryCts.Token;

			DlcQueryOverlayText = L.Get("Home_DlcQuerying", game.GameName);
			var result = await _steamDepotService.QueryAppAsync(game.AppId, ct);
			if (result == null || result.DlcAppIds.Count == 0)
			{
				IsDlcQueryOverlayVisible = false;
				await ShowModernDialogAsync(L.Get("Home_DlcTitle"), L.Get("Home_DlcNone", game.GameName));
				return;
			}

			// 读取父游戏 Lua 文件内容，判断 DLC 是否已入库
			var gameLuaPath = game.IsDisabled
				? Path.Combine(luaFolder, "Disable", $"{game.AppId}.lua")
				: Path.Combine(luaFolder, $"{game.AppId}.lua");
			var gameLuaContent = File.Exists(gameLuaPath) ? await File.ReadAllTextAsync(gameLuaPath) : string.Empty;

			var totalDlcs = result.DlcAppIds.Count;
			var dlcList = new List<DlcInfo>();

			// HasDepot 修正：主游戏 depot 含该 ID 只是旧布局的充分条件（如 949230 主列表根本无 DLC depot 项）；
			// 以 DLC 自身 depot 查询为准，单项失败回退旧启发式
			Dictionary<int, DlcDepotStatus?> depotMap = new();
			try
			{
				DlcQueryOverlayText = L.Get("Home_DlcAnalyzing");
				var depotProgress = new Progress<(int Done, int Total)>(p =>
					DlcQueryOverlayText = L.Get("Home_DlcAnalyzingProgress", p.Done, p.Total));
				depotMap = await _steamDepotService.GetDlcHasDepotsAsync(result.DlcAppIds, depotProgress, ct);
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex)
			{
				LogService.Warn(L.Get("LogCat_Home"), L.Get("Home_DlcDepotFallback", ex.Message));
			}

			for (int i = 0; i < totalDlcs; i++)
			{
				ct.ThrowIfCancellationRequested();
				var dlcId = result.DlcAppIds[i];
				DlcQueryOverlayText = L.Get("Home_DlcAnalyzingInfo", i + 1, totalDlcs);

				var isImported = !string.IsNullOrEmpty(gameLuaContent) &&
					System.Text.RegularExpressions.Regex.IsMatch(gameLuaContent,
						$@"\badd(?:app|token)id\(\s*{dlcId}\s*[,\)]");

				var mainMatch = result.GameDepots.Any(d => d.DepotId == dlcId);
				// 任一来源说有仓库即算有：帝国时代布局（depot 在主游戏名下）靠 mainMatch，
				// 天际线布局（depot 在 DLC 自己名下）靠逐个查询；查询失败（null）回退 mainMatch
				var depotStatus = depotMap.TryGetValue(dlcId, out var queried) ? queried : null;
				var hasOwnDepot = mainMatch || (depotStatus?.HasDepots == true);

				dlcList.Add(new DlcInfo
				{
					AppId = dlcId,
					IsImported = isImported,
					HasDepot = hasOwnDepot
				});
			}

			// 并行获取 DLC 名称
				DlcQueryOverlayText = L.Get("Home_DlcFetchingNames");
			var slowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(5000, slowCts.Token);
					DlcQueryOverlayText += "\n" + L.Get("Home_DlcSlowHint");
				}
				catch (OperationCanceledException) { }
			});
			try
			{
				await Parallel.ForEachAsync(dlcList, new ParallelOptions { CancellationToken = ct }, async (dlc, innerCt) =>
				{
					try
					{
						// Chinese uses Heybox names; English retains the Steam metadata name.
						string? displayName = null;
						try
						{
							if (LocalizationService.Instance.CurrentLanguage == "zh")
							{
								var (heiName, _, _) = await XiaoHeiHeService.GetGameDetailAsync(dlc.AppId, innerCt);
								if (!string.IsNullOrWhiteSpace(heiName))
									displayName = heiName;
							}
						}
						catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
						catch { }
						if (string.IsNullOrWhiteSpace(displayName) &&
							depotMap.TryGetValue(dlc.AppId, out var st) && !string.IsNullOrWhiteSpace(st?.AppName))
							displayName = st!.AppName;
						dlc.Name = string.IsNullOrWhiteSpace(displayName) ? $"DLC {dlc.AppId}" : displayName;
					}
					catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
					catch
					{
						dlc.Name = $"DLC {dlc.AppId}";
					}
				});
			}
			finally
			{
				slowCts.Cancel();
				slowCts.Dispose();
			}

			var imported = dlcList.Count(d => d.IsImported);
			IsDlcQueryOverlayVisible = false;

			var view = new Views.DlcQueryResultView(game.GameName, new ObservableCollection<DlcInfo>(dlcList), gameLuaPath, _steamDepotService, _settingsService.Load().SelectedBackdrop);
			view.ShowDialog();
		}
		catch (OperationCanceledException)
		{
			IsDlcQueryOverlayVisible = false;
		}
		catch (Exception ex)
		{
			IsDlcQueryOverlayVisible = false;
			await ShowModernDialogAsync(L.Get("Home_DlcQueryFailed"), L.Get("Home_DlcQueryError", ex.Message));
		}
		finally
		{
			_dlcQueryCts?.Cancel();
			_dlcQueryCts?.Dispose();
			_dlcQueryCts = null;
		}
	}

	public async Task HandleDropAsync(string[] files)
	{
		var luaCount = 0;
		var binCount = 0;
		var manifestCount = 0;
		var extraMsgs = new List<string>();
		foreach (var file in files)
		{
			if (_archiveImport.IsArchive(file))
			{
				extraMsgs.AddRange(await ImportArchiveFileAsync(file));
				continue;
			}
			if (file.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
			{
			try { await _luaFileManager.AddLuaFileAsync(file); luaCount++; }
			catch (Exception ex) { StatusText = L.Get("Home_DragAddFailed", "lua", ex.Message); LogService.Error(L.Get("LogCat_Home"), L.Get("Home_DragAddFailed", "lua", ex)); }
			}
			else if (file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
			{
			try { await _luaFileManager.AddBinFileAsync(file); binCount++; }
			catch (Exception ex) { StatusText = L.Get("Home_DragAddFailed", "bin", ex.Message); LogService.Error(L.Get("LogCat_Home"), L.Get("Home_DragAddFailed", "bin", ex)); }
			}
			else if (file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
			{
			try { await _luaFileManager.AddManifestFileAsync(file); manifestCount++; }
			catch (Exception ex) { StatusText = L.Get("Home_DragAddFailed", "manifest", ex.Message); LogService.Error(L.Get("LogCat_Home"), L.Get("Home_DragAddFailed", "manifest", ex)); }
			}
		}
		if (luaCount > 0 || binCount > 0 || manifestCount > 0 || extraMsgs.Count > 0)
		{
			var msgs = new List<string>();
		if (luaCount > 0) msgs.Add(L.Get("Home_ImportGameOk", luaCount));
		if (binCount > 0) msgs.Add(L.Get("Home_ImportBinOk", binCount));
		if (manifestCount > 0) msgs.Add(L.Get("Home_ImportManifestOk", manifestCount));
		msgs.AddRange(extraMsgs);
			StatusMessage = string.Join("，", msgs);
			LogService.Info(L.Get("LogCat_Home"), L.Get("Home_DragImportLog", string.Join("，", msgs)));
		}
		await QuickRefreshAsync();
	}
}
