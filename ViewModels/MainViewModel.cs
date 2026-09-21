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
	private string _selectedSortOption = "名称 A-Z";

	[ObservableProperty]
	private string _selectedDisableFilter = "全部游戏";

	[ObservableProperty]
	private string _selectedViewMode = "卡片";

	[ObservableProperty]
	private bool _isRefreshSlow;

	[ObservableProperty]
	private string _refreshProgressText = string.Empty;

	[ObservableProperty]
	private int _selectedCount;

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
			LogService.Info("主页", value);
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
			foreach (var game in Games)
				game.IsSelected = false;
			NotifySelectionChanged();
		}
	}

	public void NotifySelectionChanged()
	{
		SelectedCount = Games.Count(g => g.IsSelected);
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
			return defaults[index].Name;
		return "未知节点";
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
		WeakReferenceMessenger.Default.Register<LuaFolderChangedMessage>(this, (_, _) => OnRefreshRequested());
		WeakReferenceMessenger.Default.Register<KernelUpdateAvailableMessage>(this, (_, m) => OnKernelUpdateAvailable(m));

		var settings = settingsService.Load();
		IsAutoRefreshEnabled = settings.AutoRefreshEnabled;
		IsCardRefreshVisible = settings.IsCardRefreshVisible;
		SelectedViewMode = settings.SelectedViewMode;
		if (!string.IsNullOrEmpty(settings.SteamPath))
			steamPathService.SetCustomPath(settings.SteamPath);
	}

	public void Dispose()
	{
		var refreshCts = _refreshCts;
		_refreshCts = null;
		refreshCts?.Cancel();
		refreshCts?.Dispose();
		_progressTimer?.Stop();
		_progressTimer = null;
		_statusMessageTimer?.Dispose();
		_statusMessageTimer = null;
		_searchDebounceTimer?.Stop();
		_searchDebounceTimer = null;
		_luaFileManager.FilesChanged -= OnFilesChanged;
		WeakReferenceMessenger.Default.Unregister<LuaFolderChangedMessage>(this);
		WeakReferenceMessenger.Default.Unregister<KernelUpdateAvailableMessage>(this);
	}

	private void OnRefreshRequested()
	{
		Application.Current.Dispatcher.Invoke(() => { _ = RefreshGamesAsync(); });
	}

	// 检测仅在启动时跑一次，横幅关闭后本次启动不再显示
	private void OnKernelUpdateAvailable(KernelUpdateAvailableMessage msg)
	{
		Application.Current.Dispatcher.Invoke(() =>
		{
			KernelUpdateBannerText = $"检测到 OST 内核新版本 {msg.RemoteVersion}（当前 {msg.LocalVersion}），请前往右下角工具栏更新";
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
			: detectedPath ?? "未检测到Steam";
		OpenSteamToolStatus = _steamPathService.DetectSteamToolType() switch
		{
			SteamToolType.OpenSteamTool => "使用 OpenSteamTool 内核",
			SteamToolType.SteamTools => "检测到不适配的 SteamTools",
			_ => "未安装 OpenSteamTool"
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
			_allGames = await _luaFileManager.ScanLuaFilesAsync();
			await Task.Run(() => _steamApiService.PopulateFromCache(_allGames));
			ApplyFilter();
			UpdateStatus();
			scanOk = true;
		}
		catch (Exception ex) { StatusText = $"刷新失败: {ex.Message}"; LogService.Error("主页", $"刷新游戏信息失败: {ex}"); }
		finally
		{
			IsRefreshing = false;
		}

		if (scanOk)
			StartBackgroundFetch();
	}

	// 封面/名字后台获取：首屏已绘制，本方法立即返回；按 AppId 快照工作，
	// 回填只写仍在列表中的对象，中途删游戏/新一轮刷新不会互相踩
	private void StartBackgroundFetch()
	{
		CancelFetch();
		_refreshCts = new CancellationTokenSource();
		var token = _refreshCts.Token;
		var snapshot = _allGames.ToList();

		IsFetchingInfo = true;
		IsRefreshSlow = false;
		RefreshProgressText = "正在获取游戏信息…";
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
		catch (Exception ex) { StatusText = $"刷新失败: {ex.Message}"; LogService.Error("主页", $"后台获取游戏信息失败: {ex}"); }
		finally
		{
			if (_refreshCts?.Token == token)
			{
				var wasCancelled = token.IsCancellationRequested;
				CancelFetch();
				IsFetchingInfo = false;
				StopProgressTimer();
				IsRefreshSlow = false;
				RefreshProgressText = wasCancelled ? "已取消获取" : $"共 {_allGames.Count} 个游戏";
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
		catch (Exception ex) { LogService.Warn("主页", $"慢速提示计时异常: {ex.Message}"); }
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
				RefreshProgressText = $"正在获取... | {GetCurrentCdnName()}";
			return;
		}
		var done = _allGames.Count(g => !string.IsNullOrEmpty(g.CoverImagePath));
		RefreshProgressText = IsRefreshSlow
			? $"{done} / {_allGames.Count} 个游戏已获取 | 获取较慢，可点取消"
			: $"{done} / {_allGames.Count} 个游戏已获取 | {GetCurrentCdnName()}";
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
			_allGames = newGames;
			await Task.Run(() => _steamApiService.PopulateFromCache(_allGames));
			ApplyFilter();
			UpdateStatus();
			scanOk = true;
		}
		catch (Exception ex) { StatusText = $"刷新失败: {ex.Message}"; LogService.Error("主页", $"快速刷新失败: {ex}"); }
		finally
		{
			IsRefreshing = false;
		}

		if (scanOk)
			StartBackgroundFetch();
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
		ApplyFilter();
		UpdateStatus();
	}

	partial void OnSelectedSortOptionChanged(string value) { ApplyFilter(); }
	partial void OnSelectedDisableFilterChanged(string value) { ApplyFilter(); }
	partial void OnSelectedViewModeChanged(string value)
	{
		var settings = _settingsService.Load();
		settings.SelectedViewMode = value;
		_settingsService.Save(settings);
	}

	// 分页渐进：首屏默认 20 张，视口能摆下更多时由视图按实际容量上调；
	// 滚动到底部再按页追加，避免千级列表一次性实例化卡死 UI
	private int _gamesPageSize = 20;
	private List<GameInfo> _filteredCache = new();

	public void LoadMoreGames()
	{
		if (Games.Count >= _filteredCache.Count) return;
		foreach (var game in _filteredCache.Skip(Games.Count).Take(_gamesPageSize))
			Games.Add(game);
	}

	// 视图按可视区域能摆下的卡片数上调首屏容量（只增不减），并立即补足
	public void EnsureFirstPageCapacity(int capacity)
	{
		if (capacity <= _gamesPageSize) return;
		_gamesPageSize = capacity;
		LoadMoreGames();
	}

	private void ApplyFilter()
	{
		var query = SearchText?.Trim() ?? string.Empty;
		IEnumerable<GameInfo> filtered = string.IsNullOrWhiteSpace(query)
			? _allGames
			: _allGames.Where(g =>
			{
				var nameMatch = g.GameName.Contains(query, StringComparison.OrdinalIgnoreCase);
				var idMatch = g.AppId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
				return nameMatch || idMatch;
			});

		filtered = SelectedDisableFilter switch
		{
			"已启用入库" => filtered.Where(g => !g.IsDisabled),
			"已禁用入库" => filtered.Where(g => g.IsDisabled),
			_ => filtered
		};

		filtered = SelectedSortOption switch
		{
			"名称 Z-A" => filtered.OrderByDescending(g => g.GameName),
			"AppID 升序" => filtered.OrderBy(g => g.AppId),
			"AppID 降序" => filtered.OrderByDescending(g => g.AppId),
			"入库时间升序" => filtered.OrderBy(g => g.LuaFileTime),
			"入库时间降序" => filtered.OrderByDescending(g => g.LuaFileTime),
			_ => filtered.OrderBy(g => g.GameName)
		};

		_filteredCache = filtered.ToList();
		Games = new ObservableCollection<GameInfo>(_filteredCache.Take(_gamesPageSize));
		NotifySelectionChanged();
	}

	private void UpdateStatus() => StatusText = $"共 {_filteredCache.Count} 个游戏";

	private Task ShowModernDialogAsync(string title, string message)
		=> _dialogService.ShowAlertAsync(title, message);

	private Task<bool> ShowModernConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消")
		=> _dialogService.ShowConfirmAsync(title, message, primaryText, closeText);

	private async void OnFilesChanged(object? sender, EventArgs e)
	{
		await Application.Current.Dispatcher.InvokeAsync(async () => await QuickRefreshAsync());
	}

	[RelayCommand]
	private async Task AddFilesAsync()
	{
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "游戏文件 (*.lua;*.bin;*.manifest;*.zip;*.tar;*.7z;*.rar)|*.lua;*.bin;*.manifest;*.zip;*.tar;*.7z;*.rar",
			Multiselect = true,
			Title = "选择游戏文件"
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
				catch (Exception ex) { StatusText = $"添加失败: {ex.Message}"; LogService.Error("主页", $"添加文件失败: {ex}"); }
			}
			if (luaCount > 0) msgs.Add($"导入游戏成功 ({luaCount})");
			if (binCount > 0) msgs.Add($"导入成就成功 ({binCount})");
			if (manifestCount > 0) msgs.Add($"导入清单成功 ({manifestCount})");
			if (msgs.Count > 0)
			{
				StatusMessage = string.Join("，", msgs);
				LogService.Info("主页", string.Join("，", msgs));
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
			if (r.LuaCount > 0) lines.Add($"导入游戏成功 ({r.LuaCount})");
			if (r.BinCount > 0) lines.Add($"导入成就成功 ({r.BinCount})");
			if (r.ManifestCount > 0) lines.Add($"导入清单成功 ({r.ManifestCount})");
			if (r.SkippedCount > 0) lines.Add($"跳过同名文件 ({r.SkippedCount}：{string.Join("、", r.SkippedFiles.Take(5))}{(r.SkippedFiles.Count > 5 ? "…" : "")})");
			foreach (var f in r.FailedFiles) lines.Add($"导入失败：{f}");
			if (lines.Count == 0) lines.Add("压缩包内没有可导入的游戏文件");
		}
		catch (Exception ex) { StatusText = $"压缩包导入失败: {ex.Message}"; LogService.Error("主页", $"压缩包导入失败: {ex}"); }
		return lines;
	}

	[RelayCommand]
	private async Task DeleteGameAsync(GameInfo? game)
	{
		if (game == null) return;

		if (game.IsDisabled)
		{
			await ShowModernDialogAsync("操作被阻止", $"该游戏已被禁用入库，请先启用后再删除。");
			return;
		}

		var confirmed = await ShowModernConfirmAsync(
			"确认删除",
			$"确定要删除 {game.GameName} ({game.AppId}) 的Lua文件吗？",
			"删除");

		if (confirmed)
		{
			try { await _luaFileManager.DeleteLuaFileAsync(game.AppId); await QuickRefreshAsync(); }
			catch (Exception ex) { StatusText = $"删除失败: {ex.Message}"; LogService.Error("主页", $"删除游戏失败 ({game.GameName} AppID {game.AppId}): {ex}"); }
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
			"推荐使用清单监听",
			"手动获取 Manifest 已下线，成功率不如自动方案。\n\n请前往「清单」→ 清单监听：填入 Key 并启动监听，然后去 Steam 触发下载即可自动获取，更好用。",
			"前往清单监听", "知道了");
		if (!go) return;
		try
		{
			if (Application.Current.MainWindow is Views.MainWindow main)
				main.NavigateTo("Manifest");
		}
		catch (Exception ex) { LogService.Warn("主页", $"跳转清单监听页失败: {ex.Message}"); }
	}

	[RelayCommand]
	private void SelectAll()
	{
		foreach (var game in Games)
			game.IsSelected = true;
		NotifySelectionChanged();
	}

	[RelayCommand]
	private void ClearSelection()
	{
		foreach (var game in Games)
			game.IsSelected = false;
		NotifySelectionChanged();
	}

	[RelayCommand]
	private async Task BatchEnableAsync()
	{
		var selected = Games.Where(g => g.IsSelected && g.IsDisabled).ToList();
		if (selected.Count == 0)
		{
			await ShowModernDialogAsync("批量启用", "没有选中的已禁用游戏。");
			return;
		}
		foreach (var game in selected)
			await _luaFileManager.EnableGameAsync(game.AppId);
		StatusText = $"已启用 {selected.Count} 个游戏";
		LogService.Info("主页", $"批量启用 {selected.Count} 个游戏");
		await QuickRefreshAsync();
		ClearSelection();
	}

	[RelayCommand]
	private async Task BatchDisableAsync()
	{
		var selected = Games.Where(g => g.IsSelected && !g.IsDisabled).ToList();
		if (selected.Count == 0)
		{
			await ShowModernDialogAsync("批量禁用", "没有选中的已启用游戏。");
			return;
		}
		foreach (var game in selected)
			await _luaFileManager.DisableGameAsync(game.AppId);
		StatusText = $"已禁用 {selected.Count} 个游戏";
		LogService.Info("主页", $"批量禁用 {selected.Count} 个游戏");
		await QuickRefreshAsync();
		ClearSelection();
	}

	[RelayCommand]
	private async Task BatchDeleteAsync()
	{
		var selected = Games.Where(g => g.IsSelected && !g.IsDisabled).ToList();
		if (selected.Count == 0)
		{
			await ShowModernDialogAsync("批量删除", "没有选中的已启用游戏。\n已禁用的游戏需先启用后再删除。");
			return;
		}
		var confirmed = await ShowModernConfirmAsync(
			"批量删除",
			$"确定要删除选中的 {selected.Count} 个游戏吗？\n此操作不可恢复！",
			"删除");
		if (confirmed)
		{
			foreach (var game in selected)
				await _luaFileManager.DeleteLuaFileAsync(game.AppId);
			StatusText = $"已删除 {selected.Count} 个游戏";
			LogService.Info("主页", $"批量删除 {selected.Count} 个游戏");
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
			await ShowModernDialogAsync("操作被阻止", $"该游戏已被禁用入库，请先启用后再编辑。");
			return;
		}

		var luaFolder = _steamPathService.GetLuaFolder();
		if (string.IsNullOrEmpty(luaFolder)) return;
		var filePath = Path.Combine(luaFolder, $"{game.AppId}.lua");
		if (File.Exists(filePath))
		{
			try { Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true }); }
			catch (Exception ex) { StatusText = $"打开失败: {ex.Message}"; LogService.Warn("主页", $"打开 Lua 文件失败 ({game.GameName}): {ex.Message}"); }
		}
	}

	[RelayCommand]
	private void OpenLuaFolder()
	{
		var luaFolder = _steamPathService.GetLuaFolder();
		if (string.IsNullOrEmpty(luaFolder) || !Directory.Exists(luaFolder))
		{
			StatusText = "Lua 文件夹不存在";
			return;
		}
		try
		{
			Process.Start(new ProcessStartInfo { FileName = luaFolder, UseShellExecute = true });
		}
		catch (Exception ex)
		{
			StatusText = $"打开失败: {ex.Message}";
			LogService.Warn("主页", $"打开 Lua 文件夹失败 ({luaFolder}): {ex.Message}");
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
			await ShowModernDialogAsync("操作被阻止", $"该游戏已被禁用入库，请先启用后再固定版本。");
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
			await ShowModernDialogAsync("操作被阻止", $"该游戏已被禁用入库，请先启用后再固定版本。");
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
			await ShowModernDialogAsync("无法固定版本", $"{game.GameName} 未在本地安装，无法固定到当前版本");
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
			StatusText = $"已解除 {game.GameName} 的版本固定";
			LogService.Info("主页", $"已解除 {game.GameName} 的版本固定");
		}
		else
		{
			var manifestIds = new Dictionary<int, string>();
			var sourceName = game.ManifestSourceIndex == 0 ? "当前安装版本" : "Steam 最新版本";

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
				await ShowModernDialogAsync("无法固定版本", $"无法获取 {game.GameName} 的 manifest 信息");
				return;
			}

			await _luaFileManager.SetManifestPinAsync(game.AppId, true, manifestIds);
			StatusText = $"已将 {game.GameName} 固定到{sourceName}";
			LogService.Info("主页", $"已将 {game.GameName} 固定到{sourceName} (manifest: {string.Join(", ", manifestIds.Select(kv => $"{kv.Key}={kv.Value}"))})");
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

			DlcQueryOverlayText = $"正在查询 {game.GameName} 的 DLC 信息...";
			var result = await _steamDepotService.QueryAppAsync(game.AppId, ct);
			if (result == null || result.DlcAppIds.Count == 0)
			{
				IsDlcQueryOverlayVisible = false;
				await ShowModernDialogAsync("DLC 查询", $"{game.GameName} 没有找到关联的 DLC。");
				return;
			}

			// 读取父游戏 Lua 文件内容，判断 DLC 是否已入库
			var gameLuaPath = game.IsDisabled
				? Path.Combine(luaFolder, "Disable", $"{game.AppId}.lua")
				: Path.Combine(luaFolder, $"{game.AppId}.lua");
			var gameLuaContent = File.Exists(gameLuaPath) ? await File.ReadAllTextAsync(gameLuaPath) : string.Empty;

			var totalDlcs = result.DlcAppIds.Count;
			var dlcList = new List<DlcInfo>();
			for (int i = 0; i < totalDlcs; i++)
			{
				ct.ThrowIfCancellationRequested();
				var dlcId = result.DlcAppIds[i];
				DlcQueryOverlayText = $"正在分析 DLC 信息... ({i + 1}/{totalDlcs})";

				var isImported = !string.IsNullOrEmpty(gameLuaContent) &&
					System.Text.RegularExpressions.Regex.IsMatch(gameLuaContent,
						$@"\badd(?:app|token)id\(\s*{dlcId}\s*[,\)]");

				var hasOwnDepot = result.GameDepots.Any(d => d.DepotId == dlcId);

				dlcList.Add(new DlcInfo
				{
					AppId = dlcId,
					IsImported = isImported,
					HasDepot = hasOwnDepot
				});
			}

			// 并行获取 DLC 名称
			DlcQueryOverlayText = "正在获取 DLC 名称...";
			var slowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(5000, slowCts.Token);
					DlcQueryOverlayText += "\n如获取缓慢可尝试开启代理或梯子";
				}
				catch (OperationCanceledException) { }
			});
			try
			{
				await Parallel.ForEachAsync(dlcList, new ParallelOptions { CancellationToken = ct }, async (dlc, innerCt) =>
				{
					try
					{
						var client = _httpClientProvider.GetClient("dlc-name", TimeSpan.FromSeconds(10));
						var json = await client.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={dlc.AppId}&l=schinese", innerCt);
						var doc = System.Text.Json.JsonDocument.Parse(json);
						if (doc.RootElement.TryGetProperty(dlc.AppId.ToString(), out var appData) &&
							appData.TryGetProperty("success", out var success) && success.GetBoolean() &&
							appData.TryGetProperty("data", out var data) &&
							data.TryGetProperty("name", out var name))
						{
							dlc.Name = name.GetString() ?? $"DLC {dlc.AppId}";
						}
						else
						{
							dlc.Name = $"DLC {dlc.AppId}";
						}
					}
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
			await ShowModernDialogAsync("查询失败", $"查询 DLC 信息时出错：{ex.Message}");
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
				catch (Exception ex) { StatusText = $"拖拽添加 lua 失败: {ex.Message}"; LogService.Error("主页", $"拖拽添加 lua 失败: {ex}"); }
			}
			else if (file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
			{
				try { await _luaFileManager.AddBinFileAsync(file); binCount++; }
				catch (Exception ex) { StatusText = $"拖拽添加 bin 失败: {ex.Message}"; LogService.Error("主页", $"拖拽添加 bin 失败: {ex}"); }
			}
			else if (file.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase))
			{
				try { await _luaFileManager.AddManifestFileAsync(file); manifestCount++; }
				catch (Exception ex) { StatusText = $"拖拽添加 manifest 失败: {ex.Message}"; LogService.Error("主页", $"拖拽添加 manifest 失败: {ex}"); }
			}
		}
		if (luaCount > 0 || binCount > 0 || manifestCount > 0 || extraMsgs.Count > 0)
		{
			var msgs = new List<string>();
			if (luaCount > 0) msgs.Add($"导入游戏成功 ({luaCount})");
			if (binCount > 0) msgs.Add($"导入成就成功 ({binCount})");
			if (manifestCount > 0) msgs.Add($"导入清单成功 ({manifestCount})");
			msgs.AddRange(extraMsgs);
			StatusMessage = string.Join("，", msgs);
			LogService.Info("主页", $"拖拽导入: {string.Join("，", msgs)}");
		}
		await QuickRefreshAsync();
	}
}
