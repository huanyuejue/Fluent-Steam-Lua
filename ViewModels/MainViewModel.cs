using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
	private List<GameInfo> _allGames = new();
	private CancellationTokenSource? _refreshCts;
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

	[ObservableProperty]
	private string _selectedSortOption = "名称 A-Z";

	[ObservableProperty]
	private string _selectedViewMode = "卡片";

	[ObservableProperty]
	private bool _isRefreshSlow;

	[ObservableProperty]
	private bool _isBackgroundRefreshing;

	[ObservableProperty]
	private string _refreshProgressText = string.Empty;

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
		ISteamManifestService steamManifestService)
	{
		_steamPathService = steamPathService;
		_luaFileManager = luaFileManager;
		_steamApiService = steamApiService;
		_settingsService = settingsService;
		_steamManifestService = steamManifestService;

		_luaFileManager.FilesChanged += OnFilesChanged;

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
		_searchDebounceTimer?.Stop();
		_searchDebounceTimer = null;
		_luaFileManager.FilesChanged -= OnFilesChanged;
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

		_refreshCts?.Cancel();
		_refreshCts?.Dispose();
		_refreshCts = new CancellationTokenSource();
		var token = _refreshCts.Token;

		IsRefreshSlow = false;
		RefreshProgressText = $"正在获取... | {GetCurrentCdnName()}";

		try
		{
			IsRefreshing = true;

			_ = SlowTimerAsync(token);

			_allGames = await _luaFileManager.ScanLuaFilesAsync();
			_steamApiService.PopulateFromCache(_allGames);
			ApplyFilter();
			UpdateStatus();
		await _steamApiService.RefreshGameInfoAsync(_allGames, token);
		if (!token.IsCancellationRequested)
		{
			ApplyFilter();
			UpdateStatus();
		}

		foreach (var game in _allGames)
		{
			var refreshed = await _luaFileManager.ParseLuaFileAsync(game.AppId);
			if (refreshed != null)
			{
				game.IsManifestPinned = refreshed.IsManifestPinned;
				game.Token = refreshed.Token;
			}
		}
		ApplyFilter();
	}
		catch (Exception ex) { StatusText = $"刷新失败: {ex.Message}"; }
		finally
		{
			IsRefreshing = false;
			IsBackgroundRefreshing = false;
			StopProgressTimer();
			var wasCancelled = token.IsCancellationRequested;
			if (_refreshCts != null)
			{
				_refreshCts.Cancel();
				_refreshCts.Dispose();
				_refreshCts = null;
			}
			if (wasCancelled)
				StatusText = "已取消刷新";
			IsRefreshSlow = false;
			if (!wasCancelled)
				RefreshProgressText = $"共 {_allGames.Count} 个游戏";
		}
	}

	[RelayCommand]
	private void CancelRefresh()
	{
		_refreshCts?.Cancel();
	}

	[RelayCommand]
	private void DismissSlowOverlay()
	{
		IsRefreshSlow = false;
		IsBackgroundRefreshing = true;
		IsRefreshing = false;
	}

	private async Task SlowTimerAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(20000, token);
			if (IsRefreshing)
			{
				await Application.Current.Dispatcher.InvokeAsync(() =>
				{
					IsRefreshSlow = true;
					StartProgressTimer();
				});
			}
		}
		catch (OperationCanceledException) { }
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
		RefreshProgressText = $"{done} / {_allGames.Count} 个游戏已获取 | {GetCurrentCdnName()}";
	}

	private async Task QuickRefreshAsync()
	{
		if (IsRefreshing) return;
		try
		{
			var newGames = await _luaFileManager.ScanLuaFilesAsync();
			_allGames = newGames;
			_steamApiService.PopulateFromCache(_allGames);
			ApplyFilter();
			UpdateStatus();
			_ = _steamApiService.RefreshGameInfoAsync(_allGames).ContinueWith(_ =>
			{
				Application.Current.Dispatcher.Invoke(() =>
				{
					ApplyFilter();
					UpdateStatus();
				});
			});
		}
		catch (Exception ex) { StatusText = $"刷新失败: {ex.Message}"; }
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
	partial void OnSelectedViewModeChanged(string value)
	{
		var settings = _settingsService.Load();
		settings.SelectedViewMode = value;
		_settingsService.Save(settings);
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

		filtered = SelectedSortOption switch
		{
			"名称 Z-A" => filtered.OrderByDescending(g => g.GameName),
			"AppID 升序" => filtered.OrderBy(g => g.AppId),
			"AppID 降序" => filtered.OrderByDescending(g => g.AppId),
			"入库时间升序" => filtered.OrderBy(g => g.LuaFileTime),
			"入库时间降序" => filtered.OrderByDescending(g => g.LuaFileTime),
			_ => filtered.OrderBy(g => g.GameName)
		};

		Games = new ObservableCollection<GameInfo>(filtered);
	}

	private void UpdateStatus() => StatusText = $"共 {Games.Count} 个游戏";

	private static async Task ShowModernDialogAsync(string title, string message)
	{
		var dialog = new ContentDialog
		{
			Title = title,
			Content = new TextBlock
			{
				Text = message,
				TextWrapping = TextWrapping.Wrap,
				MaxWidth = 420
			},
			CloseButtonText = "确定",
			DefaultButton = ContentDialogButton.Close
		};
		await dialog.ShowAsync();
	}

	private static async Task<bool> ShowModernConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消")
	{
		var dialog = new ContentDialog
		{
			Title = title,
			Content = new TextBlock
			{
				Text = message,
				TextWrapping = TextWrapping.Wrap,
				MaxWidth = 420
			},
			PrimaryButtonText = primaryText,
			CloseButtonText = closeText,
			DefaultButton = ContentDialogButton.Primary
		};
		return await dialog.ShowAsync() == ContentDialogResult.Primary;
	}

	private async void OnFilesChanged(object? sender, EventArgs e)
	{
		await Application.Current.Dispatcher.InvokeAsync(async () => await QuickRefreshAsync());
	}

	[RelayCommand]
	private async Task AddFilesAsync()
	{
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "Lua文件 (*.lua)|*.lua",
			Multiselect = true,
			Title = "选择Lua文件"
		};

		if (dialog.ShowDialog() == true)
		{
			foreach (var file in dialog.FileNames)
			{
				try { await _luaFileManager.AddLuaFileAsync(file); }
				catch (Exception ex) { StatusText = $"添加失败: {ex.Message}"; }
			}
			await QuickRefreshAsync();
		}
	}

	[RelayCommand]
	private async Task DeleteGameAsync(GameInfo? game)
	{
		if (game == null) return;
		var confirmed = await ShowModernConfirmAsync(
			"确认删除",
			$"确定要删除 {game.GameName} ({game.AppId}) 的Lua文件吗？",
			"删除");

		if (confirmed)
		{
			try { await _luaFileManager.DeleteLuaFileAsync(game.AppId); await QuickRefreshAsync(); }
			catch (Exception ex) { StatusText = $"删除失败: {ex.Message}"; }
		}
	}

	[RelayCommand]
	private void EditGame(GameInfo? game)
	{
		if (game == null) return;
		var luaFolder = _steamPathService.GetLuaFolder();
		if (string.IsNullOrEmpty(luaFolder)) return;
		var filePath = Path.Combine(luaFolder, $"{game.AppId}.lua");
		if (File.Exists(filePath))
		{
			try { Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true }); }
			catch (Exception ex) { StatusText = $"打开失败: {ex.Message}"; }
		}
	}

	[RelayCommand]
	private void OpenLuaFolder()
	{
		var luaFolder = _steamPathService.GetLuaFolder();
		if (!string.IsNullOrEmpty(luaFolder) && Directory.Exists(luaFolder))
			Process.Start(new ProcessStartInfo { FileName = luaFolder, UseShellExecute = true });
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

	public async Task HandleDropAsync(string[] files)
	{
		foreach (var file in files)
		{
			if (file.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
			{
				try { await _luaFileManager.AddLuaFileAsync(file); }
				catch (Exception ex) { StatusText = $"拖拽添加失败: {ex.Message}"; }
			}
		}
		await QuickRefreshAsync();
	}
}
