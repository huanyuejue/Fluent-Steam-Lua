using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class SteamApiService : ISteamApiService
{
	private readonly IHttpClientProvider _httpClientProvider;
	private readonly string _cacheDir;
	private readonly string _coversDir;
	private readonly string _cacheFilePath;
	private ConcurrentDictionary<int, string> _nameCache = new();
	private readonly SemaphoreSlim _apiGate = new(4, 4);
	private readonly ISettingsService _settingsService;
	private int _selectedCdnIndex;
	private int _selectedCdnFailCount;
	public event Action<int>? CdnAutoSwitched;

	public int SelectedCdnIndex => _selectedCdnIndex;

	private static void ConfigureBasicHeaders(HttpClient client)
	{
		if (!client.DefaultRequestHeaders.UserAgent.Any())
			client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
		if (!client.DefaultRequestHeaders.Accept.Any())
			client.DefaultRequestHeaders.Add("Accept", "application/json");
	}

	private static void ConfigureCoverHeaders(HttpClient client)
	{
		if (!client.DefaultRequestHeaders.UserAgent.Any())
			client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
	}

	public SteamApiService(ISettingsService settingsService, IHttpClientProvider httpClientProvider)
	{
		_settingsService = settingsService;
		_httpClientProvider = httpClientProvider;
		_selectedCdnIndex = _settingsService.Load().SelectedCdnIndex;

		_cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
		_coversDir = Path.Combine(_cacheDir, "covers");
		_cacheFilePath = Path.Combine(_cacheDir, "gameinfo.json");

		Directory.CreateDirectory(_coversDir);
		LoadCache();
	}

	public void UpdateCdnPreference(int selectedIndex)
	{
		_selectedCdnIndex = selectedIndex;
		_selectedCdnFailCount = 0;
	}

	public async Task<List<(string Name, long LatencyMs, bool IsSuccess)>> TestCdnSpeedAsync(
		IProgress<(string Name, long LatencyMs, bool IsSuccess)>? progress = null)
	{
		const int testAppId = 730;

		var taskList = CdnEndpoint.Defaults.Select(async cdn =>
		{
			var url = string.Format(cdn.UrlTemplate, testAppId);
			var sw = System.Diagnostics.Stopwatch.StartNew();
			try
			{
				var response = await _httpClientProvider.SendWithProxyRetryAsync(
					"steam-api-test",
					TimeSpan.FromSeconds(10),
					client => client.GetAsync(url),
					ConfigureBasicHeaders);
				sw.Stop();
				return (cdn.Name, sw.ElapsedMilliseconds, response.IsSuccessStatusCode);
			}
			catch
			{
				sw.Stop();
				return (cdn.Name, sw.ElapsedMilliseconds, false);
			}
		}).Select(async task =>
		{
			var result = await task;
			progress?.Report(result);
			return result;
		}).ToList();

		var completedTasks = new List<Task<(string Name, long LatencyMs, bool IsSuccess)>>(taskList);
		var results = new List<(string Name, long LatencyMs, bool IsSuccess)>();

		while (completedTasks.Count > 0)
		{
			var done = await Task.WhenAny(completedTasks);
			completedTasks.Remove(done);
			results.Add(await done);
		}

		return results;
	}

	private void LoadCache()
	{
		try
		{
			if (File.Exists(_cacheFilePath))
			{
				var json = File.ReadAllText(_cacheFilePath);
				_nameCache = JsonSerializer.Deserialize<ConcurrentDictionary<int, string>>(json) ?? new();
			}
		}
		catch { _nameCache = new(); }
	}

	private void SaveCache()
	{
		try
		{
			var json = JsonSerializer.Serialize(_nameCache, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(_cacheFilePath, json);
		}
		catch { }
	}

	public void PopulateFromCache(List<GameInfo> games)
	{
		Directory.CreateDirectory(_coversDir);
		foreach (var game in games)
		{
			if (_nameCache.TryGetValue(game.AppId, out var name) && !name.StartsWith("AppID:"))
				game.GameName = name;

			var coverPath = Path.Combine(_coversDir, $"{game.AppId}.jpg");
			if (IsValidCoverFile(coverPath))
				game.CoverImagePath = coverPath;
			else if (File.Exists(coverPath))
				DeleteInvalidCover(coverPath, game);
		}
	}

	public async Task RefreshGameInfoAsync(List<GameInfo> games, CancellationToken cancellationToken = default)
	{
		Directory.CreateDirectory(_coversDir);
		_selectedCdnFailCount = 0;

		var needInfo = games.Where(g =>
			string.IsNullOrEmpty(g.GameName) ||
			g.GameName == $"AppID: {g.AppId}" ||
			!IsValidCoverFile(Path.Combine(_coversDir, $"{g.AppId}.jpg")))
			.ToList();

		if (needInfo.Count == 0) return;

		var tasks = needInfo.Select(game => RefreshOneGameAsync(game, cancellationToken));
		await Task.WhenAll(tasks);

		SaveCache();
	}

	public async Task RefreshSingleGameAsync(GameInfo game, CancellationToken cancellationToken = default)
	{
		Directory.CreateDirectory(_coversDir);
		var coverPath = Path.Combine(_coversDir, $"{game.AppId}.jpg");
		if (File.Exists(coverPath))
			File.Delete(coverPath);

		var oldName = game.GameName;
		_nameCache.TryRemove(game.AppId, out _);

		game.GameName = string.Empty;
		game.CoverImagePath = string.Empty;

		await RefreshOneGameAsync(game, cancellationToken);

		if (string.IsNullOrEmpty(game.GameName) || game.GameName == $"AppID: {game.AppId}")
		{
			if (!string.IsNullOrEmpty(oldName) && !oldName.StartsWith("AppID:"))
			{
				game.GameName = oldName;
				_nameCache[game.AppId] = oldName;
			}
		}

		SaveCache();
	}

	private async Task RefreshOneGameAsync(GameInfo game, CancellationToken cancellationToken)
	{
		try
		{
			await _apiGate.WaitAsync(cancellationToken);
		}
		catch
		{
			game.IsLoading = false;
			return;
		}

		try
		{
			game.IsLoading = true;

			var needName = string.IsNullOrEmpty(game.GameName) || game.GameName == $"AppID: {game.AppId}";
			var coverPath = Path.Combine(_coversDir, $"{game.AppId}.jpg");
			var needCover = !IsValidCoverFile(coverPath);
			if (needCover && File.Exists(coverPath))
				DeleteInvalidCover(coverPath, game);
			string? headerUrl = null;

			// 1. 优先通过 Store API 获取 header_image URL（同时获取名称）
			if (needCover || needName)
			{
				var storeResult = await TryStoreApi(game.AppId, "schinese", cancellationToken);
				if (needName && storeResult.Name != null)
				{
					game.GameName = storeResult.Name;
					_nameCache[game.AppId] = storeResult.Name;
				}
				if (needCover)
					headerUrl = storeResult.HeaderUrl;

				if (needName && storeResult.Name == null)
				{
					storeResult = await TryStoreApi(game.AppId, "english", cancellationToken);
					if (storeResult.Name != null)
					{
						game.GameName = storeResult.Name;
						_nameCache[game.AppId] = storeResult.Name;
					}
					if (needCover && headerUrl == null)
						headerUrl = storeResult.HeaderUrl;
				}

				if (needCover && headerUrl == null)
				{
					storeResult = await TryStoreApi(game.AppId, "english", cancellationToken);
					headerUrl = storeResult.HeaderUrl;
				}

				// 名称后备来源（SteamSpy / SteamCommunity）
				if (needName && string.IsNullOrEmpty(game.GameName))
				{
					var spyName = await TrySteamSpy(game.AppId, cancellationToken);
					if (spyName != null)
					{
						game.GameName = spyName;
						_nameCache[game.AppId] = spyName;
					}
					else
					{
						var communityName = await TrySteamCommunity(game.AppId, cancellationToken);
						if (communityName != null)
						{
							game.GameName = communityName;
							_nameCache[game.AppId] = communityName;
						}
					}
				}
			}

			// 2. 封面下载：选中 CDN → Store API header_image → 其余 CDN
			if (needCover)
			{
				string? cover = null;
				var endpoints = CdnEndpoint.Defaults;

				// 用户选中了某个图片 CDN（非 Store API）→ 优先尝试
				if (_selectedCdnIndex > 0 && _selectedCdnIndex < endpoints.Count)
				{
					var selected = endpoints[_selectedCdnIndex];
					if (selected.IsImageEndpoint)
					{
						var cdnUrl = string.Format(selected.UrlTemplate, game.AppId);
						cover = await DownloadCoverFromUrl(cdnUrl, game.AppId, cancellationToken);
						if (string.IsNullOrEmpty(cover))
						{
							_selectedCdnFailCount++;
							if (_selectedCdnFailCount >= 3)
								AutoSwitchCdn();
						}
						else
						{
							_selectedCdnFailCount = 0;
						}
					}
				}

				// Store API header_image 第二顺位
				if (string.IsNullOrEmpty(cover) && headerUrl != null)
					cover = await DownloadCoverFromUrl(headerUrl, game.AppId, cancellationToken);

				// 其余 CDN 轮询（含选中 CDN 的重试）
				if (string.IsNullOrEmpty(cover))
				{
					cover = await FetchCoverAsync(game.AppId, cancellationToken);
					if (!string.IsNullOrEmpty(cover))
						game.CoverImagePath = cover;
				}
				else
				{
					game.CoverImagePath = cover;
				}
			}
		}
		catch { }
		finally
		{
			game.IsLoading = false;
			_apiGate.Release();
		}
	}

	private void AutoSwitchCdn()
	{
		var endpoints = CdnEndpoint.Defaults;
		for (int i = _selectedCdnIndex + 1; i < endpoints.Count; i++)
		{
			if (endpoints[i].IsImageEndpoint)
			{
				_selectedCdnIndex = i;
				_selectedCdnFailCount = 0;
				var settings = _settingsService.Load();
				settings.SelectedCdnIndex = i;
				_settingsService.Save(settings);
				CdnAutoSwitched?.Invoke(i);
				return;
			}
		}
		// 没有更多可用节点，重置到 Store API
		_selectedCdnIndex = 0;
		_selectedCdnFailCount = 0;
		var s = _settingsService.Load();
		s.SelectedCdnIndex = 0;
		_settingsService.Save(s);
		CdnAutoSwitched?.Invoke(0);
	}

	private static bool IsValidCoverFile(string path)
	{
		try
		{
			if (!File.Exists(path)) return false;
			var info = new FileInfo(path);
			if (info.Length <= 1000) return false;

			Span<byte> header = stackalloc byte[12];
			using var stream = File.OpenRead(path);
			var read = stream.Read(header);
			return IsValidImageHeader(header[..read]);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsValidImageBytes(byte[] bytes)
	{
		return bytes.Length > 1000 && IsValidImageHeader(bytes.AsSpan(0, Math.Min(bytes.Length, 12)));
	}

	private static bool IsValidImageHeader(ReadOnlySpan<byte> header)
	{
		if (header.Length < 4) return false;
		var isJpeg = header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
		var isPng = header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
		            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
		var isWebp = header.Length >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
		             header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50;
		return isJpeg || isPng || isWebp;
	}

	private static void DeleteInvalidCover(string path, GameInfo? game = null)
	{
		try { File.Delete(path); }
		catch { }
		if (game != null)
			game.CoverImagePath = string.Empty;
	}

	private async Task<(string? Name, string? HeaderUrl)> TryStoreApi(int appId, string lang, CancellationToken cancellationToken)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(TimeSpan.FromSeconds(5));
		try
		{
			var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={lang}";
			await using var stream = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-json",
				TimeSpan.FromSeconds(8),
				client => client.GetStreamAsync(url, cts.Token),
				ConfigureBasicHeaders);
			using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
			var root = doc.RootElement;

			if (root.TryGetProperty(appId.ToString(), out var app) &&
				app.TryGetProperty("success", out var ok) && ok.GetBoolean() &&
				app.TryGetProperty("data", out var data))
			{
				var name = data.TryGetProperty("name", out var n) ? n.GetString() : null;
				var header = data.TryGetProperty("header_image", out var h) ? h.GetString() : null;
				return (name, header);
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
		catch { }
		return (null, null);
	}

	private async Task<string?> TrySteamSpy(int appId, CancellationToken cancellationToken)
	{
		try
		{
			var url = $"https://steamspy.com/api.php?request=appdetails&appid={appId}";
			await using var stream = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-json",
				TimeSpan.FromSeconds(8),
				client => client.GetStreamAsync(url, cancellationToken),
				ConfigureBasicHeaders);
			using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
			if (doc.RootElement.TryGetProperty("name", out var name))
				return name.GetString();
		}
		catch { }
		return null;
	}

	private async Task<string?> TrySteamCommunity(int appId, CancellationToken cancellationToken)
	{
		try
		{
			var url = $"https://steamcommunity.com/app/{appId}?l=english";
			var html = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-json",
				TimeSpan.FromSeconds(8),
				client => client.GetStringAsync(url, cancellationToken),
				ConfigureBasicHeaders);

			var tag = "<title>";
			var start = html.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
			if (start < 0) return null;
			start += tag.Length;

			var end = html.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
			if (end < 0) return null;

			var title = html[start..end];
			var sep = title.IndexOf(" :: ", StringComparison.OrdinalIgnoreCase);
			if (sep > 0) return title[..sep].Trim();

			sep = title.LastIndexOf(" - ", StringComparison.OrdinalIgnoreCase);
			if (sep > 0) return title[..sep].Trim();

			return title.Trim();
		}
		catch { }
		return null;
	}

	private async Task<string?> DownloadCoverFromUrl(string url, int appId, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(_coversDir);
		var localPath = Path.Combine(_coversDir, $"{appId}.jpg");
		if (IsValidCoverFile(localPath))
			return localPath;
		if (File.Exists(localPath))
			DeleteInvalidCover(localPath);

		try
		{
			using var response = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-cover",
				TimeSpan.FromSeconds(15),
				client => client.GetAsync(url, cancellationToken),
				ConfigureCoverHeaders);
			if (!response.IsSuccessStatusCode) return null;

			var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
			if (!IsValidImageBytes(bytes)) return null;

			await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
			return localPath;
		}
		catch { }
		return null;
	}

	private async Task<string?> FetchCoverAsync(int appId, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(_coversDir);
		var localPath = Path.Combine(_coversDir, $"{appId}.jpg");
		if (IsValidCoverFile(localPath))
			return localPath;
		if (File.Exists(localPath))
			DeleteInvalidCover(localPath);

		var endpoints = CdnEndpoint.Defaults.ToList();
		var ordered = new List<string>();
		if (_selectedCdnIndex >= 0 && _selectedCdnIndex < endpoints.Count)
		{
			var selected = endpoints[_selectedCdnIndex];
			if (selected.IsImageEndpoint)
				ordered.Add(string.Format(selected.UrlTemplate, appId));
		}
		for (int i = 0; i < endpoints.Count; i++)
		{
			if (i != _selectedCdnIndex && endpoints[i].IsImageEndpoint)
				ordered.Add(string.Format(endpoints[i].UrlTemplate, appId));
		}

		for (int attempt = 0; attempt < 2; attempt++)
		{
			foreach (var url in ordered)
			{
				try
				{
					using var response = await _httpClientProvider.SendWithProxyRetryAsync(
						"steam-api-cover",
						TimeSpan.FromSeconds(15),
						client => client.GetAsync(url, cancellationToken),
						ConfigureCoverHeaders);
					if (!response.IsSuccessStatusCode) continue;

					var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
					if (!IsValidImageBytes(bytes)) continue;

					await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
					return localPath;
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
				catch { }
			}
			if (attempt == 0)
				await Task.Delay(2000, cancellationToken);
		}
		return null;
	}
}
