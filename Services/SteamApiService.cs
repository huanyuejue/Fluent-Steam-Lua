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
	// 元数据与封面下载分开限流：名字/Store API 走 8 并发，封面下载走 6 并发；
	// 社区页回退单独 2 并发——8 并发打过去必吃 429（已在现网复现）
	private readonly SemaphoreSlim _metaGate = new(8, 8);
	private readonly SemaphoreSlim _coverGate = new(6, 6);
	private readonly SemaphoreSlim _communityGate = new(2, 2);
	private readonly object _saveCacheLock = new();
	private int _completedSinceSave;
	private readonly ISettingsService _settingsService;
	private int _selectedCdnIndex;
	private int _selectedCdnFailCount;
	public event Action<int>? CdnAutoSwitched;

	public int SelectedCdnIndex => _selectedCdnIndex;

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

	/// <summary>
	/// 按与主页封面获取一致的节点顺序（选中 CDN → 其余图片 CDN → Steam 官方兜底）返回封面 URL 列表，
	/// 跟随设置中的封面节点。仅返回地址，不落盘。
	/// </summary>
	public List<string> GetCoverUrls(int appId)
	{
		var urls = BuildCdnUrlChain(appId);
		urls.Add($"https://cdn.steamstatic.com/steam/apps/{appId}/header.jpg");
		return urls;
	}

	/// <summary>按选中图片节点优先 → 其余图片节点的顺序构建 CDN 封面 URL 链（不含官方兜底）。</summary>
	private List<string> BuildCdnUrlChain(int appId)
	{
		var endpoints = CdnEndpoint.Defaults;
		var urls = new List<string>();

		if (_selectedCdnIndex > 0 && _selectedCdnIndex < endpoints.Count && endpoints[_selectedCdnIndex].IsImageEndpoint)
			urls.Add(string.Format(endpoints[_selectedCdnIndex].UrlTemplate, appId));

		for (int i = 1; i < endpoints.Count; i++)
		{
			if (i != _selectedCdnIndex && endpoints[i].IsImageEndpoint)
				urls.Add(string.Format(endpoints[i].UrlTemplate, appId));
		}

		return urls;
	}

	/// <summary>实时查询 Store API 的真实封面 URL（header_image 字段）：schinese 优先，失败回退 english。</summary>
	public async Task<string?> ResolveHeaderUrlAsync(int appId, CancellationToken cancellationToken = default)
	{
		var result = await TryStoreApi(appId, "schinese", cancellationToken);
		if (string.IsNullOrEmpty(result.HeaderUrl))
			result = await TryStoreApi(appId, "english", cancellationToken);
		return result.HeaderUrl;
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
				using var response = await _httpClientProvider.SendWithProxyRetryAsync(
					"steam-api-test",
					TimeSpan.FromSeconds(10),
					client => client.GetAsync(url),
					HttpHeaderHelper.ConfigureBrowserJson);
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
		catch (Exception ex)
		{
			LogService.Warn("名称缓存", $"加载缓存失败: {ex.Message}");
			_nameCache = new();
		}
	}

	private void SaveCache()
	{
		// 增量落盘与批量结束可能并发触发，串行化避免写坏 gameinfo.json
		lock (_saveCacheLock)
		{
			try
			{
				var json = JsonSerializer.Serialize(_nameCache, new JsonSerializerOptions { WriteIndented = true });
				File.WriteAllText(_cacheFilePath, json);
			}
			catch (Exception ex) { LogService.Warn("名称缓存", $"保存缓存失败: {ex.Message}"); }
		}
	}

	public void PopulateFromCache(List<GameInfo> games)
	{
		Directory.CreateDirectory(_coversDir);
		foreach (var game in games)
		{
			// 缓存里的毒名字（历史版本误存的 "Error" 等）不采用，留给正文重取
			if (_nameCache.TryGetValue(game.AppId, out var name) && !name.StartsWith("AppID:") && !IsJunkGameName(name))
				game.GameName = name;

			var coverPath = Path.Combine(_coversDir, $"{game.AppId}.jpg");
			if (IsValidCoverFile(coverPath))
				game.CoverImagePath = coverPath;
			else if (File.Exists(coverPath))
				DeleteInvalidCover(coverPath, game);
		}
	}

	public async Task RefreshGameInfoAsync(List<GameInfo> games, CancellationToken cancellationToken = default, bool fetchCover = true)
	{
		Directory.CreateDirectory(_coversDir);
		_selectedCdnFailCount = 0;

		// 过滤含同步文件 IO（Exists + 读头 12 字节），N 个游戏时扔后台线程，不占 UI；
		// ConfigureAwait(false) 让后续扇出与回调用都留在池线程，整文件序列化落盘也不回 UI
		var needInfo = await Task.Run(() => games.Where(g =>
			string.IsNullOrEmpty(g.GameName) ||
			g.GameName == $"AppID: {g.AppId}" ||
			IsJunkGameName(g.GameName) ||
			(fetchCover && !IsValidCoverFile(Path.Combine(_coversDir, $"{g.AppId}.jpg"))))
			.ToList(), cancellationToken).ConfigureAwait(false);

		if (needInfo.Count == 0) return;

		var tasks = needInfo.Select(game => RefreshOneGameAsync(game, cancellationToken, fetchCover));
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

		if (string.IsNullOrEmpty(game.GameName) || game.GameName == $"AppID: {game.AppId}" || IsJunkGameName(game.GameName))
		{
			if (!string.IsNullOrEmpty(oldName) && !oldName.StartsWith("AppID:") && !IsJunkGameName(oldName))
			{
				game.GameName = oldName;
				_nameCache[game.AppId] = oldName;
			}
		}

		SaveCache();
	}

	private async Task RefreshOneGameAsync(GameInfo game, CancellationToken cancellationToken, bool fetchCover = true)
	{
		try
		{
			game.IsLoading = true;

			var needName = string.IsNullOrEmpty(game.GameName) || game.GameName == $"AppID: {game.AppId}" || IsJunkGameName(game.GameName);
			var coverPath = Path.Combine(_coversDir, $"{game.AppId}.jpg");
			var needCover = fetchCover && !IsValidCoverFile(coverPath);
			if (needCover && File.Exists(coverPath))
				DeleteInvalidCover(coverPath, game);
			string? headerUrl = null;

			// 1. 优先通过 Store API 获取 header_image URL（同时获取名称）
			if (needCover || needName)
			{
				headerUrl = await FetchMetaSectionAsync(game, needName, needCover, headerUrl, cancellationToken);
			}

			// 2. 封面下载：选中 CDN → Store API header_image → 其余 CDN（独立闸门）
			if (needCover)
			{
				var cover = await DownloadCoverChainAsync(game.AppId, headerUrl, cancellationToken);
				if (string.IsNullOrEmpty(cover))
				{
					// 第一轮扫空：闸门外等 2 秒再扫一轮，等待期间不占并发槽
					try { await Task.Delay(2000, cancellationToken); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
					if (!cancellationToken.IsCancellationRequested)
						cover = await DownloadCoverChainAsync(game.AppId, headerUrl, cancellationToken);
				}
				if (!string.IsNullOrEmpty(cover))
					game.CoverImagePath = cover;
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception ex)
		{
			LogService.Warn("封面", $"刷新游戏信息失败 (AppID {game.AppId}): {ex.Message}");
		}
		finally
		{
			game.IsLoading = false;
			// 增量落盘：崩溃/取消也不丢已拿到的名字，下次接着补
			if (Interlocked.Increment(ref _completedSinceSave) % 50 == 0)
				SaveCache();
		}
	}

	// 元数据段（Store API + 后备名字源）：独立闸门，进出配对释放
	private async Task<string?> FetchMetaSectionAsync(GameInfo game, bool needName, bool needCover, string? headerUrl, CancellationToken cancellationToken)
	{
		await _metaGate.WaitAsync(cancellationToken);
		try
		{
				var storeResult = await TryStoreApi(game.AppId, "schinese", cancellationToken);
				if (needName && storeResult.Name != null)
				{
					game.GameName = storeResult.Name;
					_nameCache[game.AppId] = storeResult.Name;
				}
				if (needCover)
					headerUrl = storeResult.HeaderUrl;

				var triedEnglish = false;
				if (needName && storeResult.Name == null)
				{
					storeResult = await TryStoreApi(game.AppId, "english", cancellationToken);
					triedEnglish = true;
					if (storeResult.Name != null)
					{
						game.GameName = storeResult.Name;
						_nameCache[game.AppId] = storeResult.Name;
					}
					if (needCover && headerUrl == null)
						headerUrl = storeResult.HeaderUrl;
				}

				if (needCover && headerUrl == null && !triedEnglish)
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
		finally
		{
			_metaGate.Release();
		}
		return headerUrl;
	}

	// 封面链下载：闸门只罩住真实请求，两轮之间的 2 秒等待不占槽

	private async Task<string?> DownloadCoverChainAsync(int appId, string? headerUrl, CancellationToken cancellationToken)
	{
		await _coverGate.WaitAsync(cancellationToken);
		try
		{
			string? cover = null;

			// 用户选中了某个图片 CDN（非 Store API）→ 优先尝试
			var cdnChain = BuildCdnUrlChain(appId);
			if (cdnChain.Count > 0)
			{
				var (first, nodeFailed) = await DownloadCoverFromUrl(cdnChain[0], appId, cancellationToken);
				cover = first;
				if (string.IsNullOrEmpty(cover))
				{
					if (nodeFailed && TryCountCdnFailure())
						AutoSwitchCdn();
				}
				else
				{
					_selectedCdnFailCount = 0;
				}
			}

			// Store API header_image 第二顺位（官方直链，失败不计入切源）
			if (string.IsNullOrEmpty(cover) && headerUrl != null)
				(cover, _) = await DownloadCoverFromUrl(headerUrl, appId, cancellationToken);

			// 其余 CDN 轮询（含选中 CDN 的重试）
			if (string.IsNullOrEmpty(cover))
				cover = await SweepCoverAsync(appId, cdnChain, cancellationToken);
			return cover;
		}
		finally
		{
			_coverGate.Release();
		}
	}

	// 其余 CDN 单轮扫一遍；扫空且调用方还想重试时，由调用方在闸门外等待后再次调用
	private async Task<string?> SweepCoverAsync(int appId, List<string> ordered, CancellationToken cancellationToken)
	{
		foreach (var url in ordered)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (local, _) = await DownloadCoverFromUrl(url, appId, cancellationToken);
			if (!string.IsNullOrEmpty(local))
				return local;
		}
		return null;
	}

	// 转完一轮回到 Store API 后 10 分钟内不再自动切换：节点全灭时多半是网络整体问题，
	// 无冷却会每个批次都转一圈，日志刷屏且反复写设置
	private DateTime _autoSwitchCooldownUntil = DateTime.MinValue;

	private bool TryCountCdnFailure()
	{
		// 冷却期内不清零计数会让攒的老数加速下次切换，直接清掉重算
		if (DateTime.UtcNow < _autoSwitchCooldownUntil)
		{
			_selectedCdnFailCount = 0;
			return false;
		}
		return Interlocked.Increment(ref _selectedCdnFailCount) >= 3;
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
				LogService.Warn("封面", $"选中 CDN 连续失败，自动切换到节点 {endpoints[i].Name}");
				return;
			}
		}
		// 没有更多可用节点，重置到 Store API 并冷却
		_selectedCdnIndex = 0;
		_selectedCdnFailCount = 0;
		_autoSwitchCooldownUntil = DateTime.UtcNow.AddMinutes(10);
		var s = _settingsService.Load();
		s.SelectedCdnIndex = 0;
		_settingsService.Save(s);
		CdnAutoSwitched?.Invoke(0);
		LogService.Warn("封面", $"所有 CDN 节点均不可用，已重置为 Store API（10 分钟内不再自动切换）");
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
		catch (Exception ex) { LogService.Warn("封面清理", $"删除无效封面失败 {path}: {ex.Message}"); }
		if (game != null)
			game.CoverImagePath = string.Empty;
	}

	// appdetails 有时会以重定向后的 AppID 做 key（如 3669870 返回的 key 是 4760190）；
	// 精确 key 优先，缺席时取首个 success 项，但要求 data.steam_appid 对得上才认，防止串 game
	private static bool TryGetAppNode(JsonElement root, int appId, out JsonElement node)
	{
		node = default;
		if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
		if (root.TryGetProperty(appId.ToString(), out var exact))
		{
			node = exact;
			return true;
		}
		foreach (var prop in root.EnumerateObject())
		{
			var v = prop.Value;
			if (v.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
			if (!v.TryGetProperty("success", out var s) || !s.GetBoolean()) continue;
			if (!v.TryGetProperty("data", out var data)) continue;
			if (data.TryGetProperty("steam_appid", out var id) && id.GetInt32() != appId) continue;
			node = v;
			return true;
		}
		return false;
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
				HttpHeaderHelper.ConfigureBrowserJson);
			using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
			var root = doc.RootElement;

			if (TryGetAppNode(root, appId, out var app) &&
				app.TryGetProperty("success", out var ok) && ok.GetBoolean() &&
				app.TryGetProperty("data", out var data))
			{
				var name = data.TryGetProperty("name", out var n) ? n.GetString() : null;
				var header = data.TryGetProperty("header_image", out var h) ? h.GetString() : null;
				return (name, header);
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			LogService.Warn("Steam API", $"appdetails 请求超时 (AppID {appId})");
		}
		catch (Exception ex) { LogService.Warn("Steam API", $"appdetails 请求失败 (AppID {appId}): {ex.Message}"); }
		return (null, null);
	}

	/// <summary>
	/// 查询游戏是否尚未发售（Store API 的 release_date.coming_soon）。
	/// 返回 null 表示查询失败无法判定，调用方需与 true/false 区分处理。
	/// </summary>
	public async Task<bool?> IsComingSoonAsync(int appId, CancellationToken cancellationToken = default)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(TimeSpan.FromSeconds(5));
		try
		{
			var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=schinese";
			await using var stream = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-json",
				TimeSpan.FromSeconds(8),
				client => client.GetStreamAsync(url, cts.Token),
				HttpHeaderHelper.ConfigureBrowserJson);
			using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
			var root = doc.RootElement;

			if (TryGetAppNode(root, appId, out var app) &&
				app.TryGetProperty("success", out var ok) && ok.GetBoolean() &&
				app.TryGetProperty("data", out var data) &&
				data.TryGetProperty("release_date", out var releaseDate) &&
				releaseDate.TryGetProperty("coming_soon", out var comingSoon))
			{
				return comingSoon.GetBoolean();
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			LogService.Warn("Steam API", $"coming_soon 查询超时 (AppID {appId})");
		}
		catch (Exception ex)
		{
			LogService.Warn("Steam API", $"coming_soon 查询失败 (AppID {appId}): {ex.Message}");
		}
		return null;
	}

	public async Task<string?> GetFallbackGameNameAsync(int appId, CancellationToken cancellationToken = default)
	{
		return await TrySteamSpy(appId, cancellationToken)
			?? await TrySteamCommunity(appId, cancellationToken);
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
				HttpHeaderHelper.ConfigureBrowserJson);
			using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
			if (doc.RootElement.TryGetProperty("name", out var name))
				return name.GetString();
		}
		catch (Exception ex) { LogService.Warn("Steam API", $"steamspy 请求失败 (AppID {appId}): {ex.Message}"); }
		return null;
	}

	// 错误页/挑战页标题不是游戏名，直接拒掉，免得 "Error" 之类写入缓存 anden 出现在卡片上
	private static bool IsJunkGameName(string? name) =>
		string.IsNullOrWhiteSpace(name) ||
		name.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
		name.Equals("Steam Community", StringComparison.OrdinalIgnoreCase) ||
		name.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
		name.Contains("Access Denied", StringComparison.OrdinalIgnoreCase) ||
		name.Contains("Attention Required", StringComparison.OrdinalIgnoreCase);

	private async Task<string?> TrySteamCommunity(int appId, CancellationToken cancellationToken)
	{
		await _communityGate.WaitAsync(cancellationToken);
		try
		{
			var url = $"https://steamcommunity.com/app/{appId}?l=english";
			var html = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-json",
				TimeSpan.FromSeconds(8),
				client => client.GetStringAsync(url, cancellationToken),
				HttpHeaderHelper.ConfigureBrowserJson);

			var tag = "<title>";
			var start = html.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
			if (start < 0) return null;
			start += tag.Length;

			var end = html.IndexOf("</title>", start, StringComparison.OrdinalIgnoreCase);
			if (end < 0) return null;

			var title = html[start..end];
			// 社区游戏页标题固定为 "Steam Community :: 游戏名"，取 :: 之后才是真名；
			// 之前取之前会把所有游戏都解析成 "Steam Community"
			string? name = null;
			var sep = title.IndexOf(" :: ", StringComparison.OrdinalIgnoreCase);
			if (sep >= 0)
				name = title[(sep + 4)..].Trim();

			if (string.IsNullOrEmpty(name))
			{
				sep = title.LastIndexOf(" - ", StringComparison.OrdinalIgnoreCase);
				name = sep > 0 ? title[..sep].Trim() : title.Trim();
			}
			return IsJunkGameName(name) ? null : name;
		}
		catch (Exception ex)
		{
			LogService.Warn("Steam API", $"steamcommunity 请求失败 (AppID {appId}): {ex.Message}");
			return null;
		}
		finally
		{
			_communityGate.Release();
		}
	}

	// 返回（落盘路径，是否节点级失败）：超时/连接异常/5xx/429/403 算节点问题，计入自动切源；
	// 404/400/内容非法算该游戏缺图，不怪节点——老模板 URL 对新老游戏普遍 404，
	// 不区分的话几个游戏就能把计数器顶满，无限切源
	private async Task<(string? Path, bool NodeFailed)> DownloadCoverFromUrl(string url, int appId, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(_coversDir);
		var localPath = Path.Combine(_coversDir, $"{appId}.jpg");
		if (IsValidCoverFile(localPath))
			return (localPath, false);
		if (File.Exists(localPath))
			DeleteInvalidCover(localPath);

		try
		{
			using var response = await _httpClientProvider.SendWithProxyRetryAsync(
				"steam-api-cover",
				TimeSpan.FromSeconds(15),
				client => client.GetAsync(url, cancellationToken),
				HttpHeaderHelper.ConfigureBrowserJson);
			if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
				response.StatusCode == System.Net.HttpStatusCode.BadRequest)
				return (null, false);
			if (!response.IsSuccessStatusCode)
				return (null, true);

			var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
			if (!IsValidImageBytes(bytes)) return (null, false);

			await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
			return (localPath, false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (Exception ex)
		{
			LogService.Warn("封面", $"下载封面失败 (AppID {appId}): {ex.Message}");
			return (null, true);
		}
	}

}
