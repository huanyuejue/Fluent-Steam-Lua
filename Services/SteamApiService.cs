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
		_selectedCdnIndex = Math.Clamp(_settingsService.Load().SelectedCdnIndex, 0, CdnEndpoint.Defaults.Count - 1);

		_cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
		_coversDir = Path.Combine(_cacheDir, "covers");
		_cacheFilePath = Path.Combine(_cacheDir, "gameinfo.json");

		Directory.CreateDirectory(_coversDir);
		LoadCache();
	}

	public void UpdateCdnPreference(int selectedIndex)
	{
		// 旧配置的下标可能超出当前节点表，入口钳制（使用处虽有边界检查，脏值仍会存回去污染配置）
		_selectedCdnIndex = Math.Clamp(selectedIndex, 0, CdnEndpoint.Defaults.Count - 1);
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

	/// <summary>按选中图片节点优先 → 其余图片节点的顺序构建 CDN 封面 URL 链（不含官方兜底）；接口型源不参与拼接。</summary>
	private List<string> BuildCdnUrlChain(int appId)
	{
		var endpoints = CdnEndpoint.Defaults;
		var urls = new List<string>();

		if (_selectedCdnIndex > 0 && _selectedCdnIndex < endpoints.Count &&
			endpoints[_selectedCdnIndex].IsImageEndpoint && !endpoints[_selectedCdnIndex].IsApiLookup)
			urls.Add(string.Format(endpoints[_selectedCdnIndex].UrlTemplate, appId));

		for (int i = 1; i < endpoints.Count; i++)
		{
			if (i != _selectedCdnIndex && endpoints[i].IsImageEndpoint && !endpoints[i].IsApiLookup)
				urls.Add(string.Format(endpoints[i].UrlTemplate, appId));
		}

		return urls;
	}

	/// <summary>当前选中的是否为接口型源（Heybox）；是则封面走接口直链优先，不参与模板拼接。</summary>
	private bool IsHeyboxSelected()
	{
		var endpoints = CdnEndpoint.Defaults;
		return _selectedCdnIndex >= 0 && _selectedCdnIndex < endpoints.Count && endpoints[_selectedCdnIndex].IsApiLookup;
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
			// 接口型源无直链可测：用一次真实详情查询计时
			if (cdn.IsApiLookup)
			{
				var swApi = System.Diagnostics.Stopwatch.StartNew();
				try
				{
					var (apiName, _, _) = await XiaoHeiHeService.GetGameDetailAsync(testAppId);
					swApi.Stop();
					return (cdn.Name, swApi.ElapsedMilliseconds, !string.IsNullOrEmpty(apiName));
				}
				catch
				{
					swApi.Stop();
					return (cdn.Name, swApi.ElapsedMilliseconds, false);
				}
			}
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
			LogService.Warn(L.Get("LogCat_NameCache"), L.Get("Svc_ApiLoadCacheFailed", ex.Message));
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
			catch (Exception ex) { LogService.Warn(L.Get("LogCat_NameCache"), L.Get("Svc_ApiSaveCacheFailed", ex.Message)); }
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

			// 1. 元数据段：小黑盒优先（中文名 + 封面直链），Store API 与后备源补位
			string? headerUrl = null;
			string? heiheCover = null;
			if (needCover || needName)
			{
				(headerUrl, heiheCover) = await FetchMetaSectionAsync(game, needName, needCover, cancellationToken);
			}

			// 2. 封面下载：选中 CDN（Heybox 选中时走接口直链优先）→ 小黑盒 → Store header_image → 其余 CDN（独立闸门）
			if (needCover)
			{
				var cover = await DownloadCoverChainAsync(game.AppId, headerUrl, heiheCover, cancellationToken);
				if (string.IsNullOrEmpty(cover))
				{
					// 第一轮扫空：闸门外等 2 秒再扫一轮，等待期间不占并发槽
					try { await Task.Delay(2000, cancellationToken); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
					if (!cancellationToken.IsCancellationRequested)
						cover = await DownloadCoverChainAsync(game.AppId, headerUrl, heiheCover, cancellationToken);
				}
				if (!string.IsNullOrEmpty(cover))
					game.CoverImagePath = cover;
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
		catch (Exception ex)
		{
			LogService.Warn(L.Get("LogCat_Cover"), L.Get("Svc_ApiRefreshFailed", game.AppId, ex.Message));
		}
		finally
		{
			game.IsLoading = false;
			// 增量落盘：崩溃/取消也不丢已拿到的名字，下次接着补
			if (Interlocked.Increment(ref _completedSinceSave) % 50 == 0)
				SaveCache();
		}
	}

	// 元数据段（小黑盒 → Store API → 后备名字源）：独立闸门，进出配对释放
	private async Task<(string? HeaderUrl, string? HeiheCover)> FetchMetaSectionAsync(
		GameInfo game, bool needName, bool needCover, CancellationToken cancellationToken)
	{
		await _metaGate.WaitAsync(cancellationToken);
		string? headerUrl = null;
		string? heiheCover = null;
		try
		{
			// 0. 小黑盒国内源优先：中文名 + 封面直链；失败静默走原有链路
			if (needName || needCover)
			{
					var (heiName, heiCover, _) = await XiaoHeiHeService.GetGameDetailAsync(game.AppId, cancellationToken);
				if (!string.IsNullOrWhiteSpace(heiName))
				{
					game.GameName = heiName;
					_nameCache[game.AppId] = heiName;
				}
				if (!string.IsNullOrWhiteSpace(heiCover))
					heiheCover = heiCover;
			}

			// 小黑盒已补到真名后不再用 Store 覆盖；仍缺名才继续原有链路
			var stillNeedName = needName && (string.IsNullOrEmpty(game.GameName) ||
				game.GameName == $"AppID: {game.AppId}" || IsJunkGameName(game.GameName));

			// 小黑盒名+封面双全时直接跳过 Store API（Heybox 选中下的常见情形，不再刷超时日志）
			if (stillNeedName || (needCover && heiheCover == null))
			{
				var storeResult = await TryStoreApi(game.AppId, "schinese", cancellationToken);
				if (stillNeedName && storeResult.Name != null)
				{
					game.GameName = storeResult.Name;
					_nameCache[game.AppId] = storeResult.Name;
				}
				if (needCover)
					headerUrl = storeResult.HeaderUrl;

				var triedEnglish = false;
				if (stillNeedName && storeResult.Name == null)
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
			}

			// 名称后备来源（SteamSpy / SteamCommunity）
			if (stillNeedName && string.IsNullOrEmpty(game.GameName))
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
		return (headerUrl, heiheCover);
	}

	// 封面链下载：闸门只罩住真实请求，两轮之间的 2 秒等待不占槽

	private async Task<string?> DownloadCoverChainAsync(int appId, string? headerUrl, string? heiheUrl, CancellationToken cancellationToken)
	{
		await _coverGate.WaitAsync(cancellationToken);
		try
		{
			string? cover = null;
			var heyboxFirst = IsHeyboxSelected();

			// 用户选中了某个图片 CDN（非 Heybox）→ 优先尝试
			var cdnChain = BuildCdnUrlChain(appId);
			if (!heyboxFirst && cdnChain.Count > 0)
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

			// 小黑盒国内直链：Heybox 选中时第一顺位，否则排在选中 CDN 之后、Store 官方直链之前；
			// 接口失败不计入 CDN 切源（不同系统）
			if (string.IsNullOrEmpty(cover) && !string.IsNullOrEmpty(heiheUrl))
				(cover, _) = await DownloadCoverFromUrl(heiheUrl, appId, cancellationToken);

			// Store API header_image（官方直链，失败不计入切源）
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
				LogService.Warn(L.Get("LogCat_Cover"), L.Get("Svc_ApiCdnSwitched", endpoints[i].Name));
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
		LogService.Warn(L.Get("LogCat_Cover"), L.Get("Svc_ApiCdnAllDead"));
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
		catch (Exception ex) { LogService.Warn(L.Get("LogCat_CoverClean"), L.Get("Svc_ApiDelCoverFailed", path, ex.Message)); }
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
			LogService.Warn(L.Get("LogCat_SteamApi"), L.Get("Svc_ApiStoreTimeout", appId));
		}
		catch (Exception ex) { LogService.Warn(L.Get("LogCat_SteamApi"), L.Get("Svc_ApiStoreFailed", appId, ex.Message)); }
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
			LogService.Warn(L.Get("LogCat_SteamApi"), L.Get("Svc_ApiSoonTimeout", appId));
		}
		catch (Exception ex)
		{
			LogService.Warn(L.Get("LogCat_SteamApi"), L.Get("Svc_ApiSoonFailed", appId, ex.Message));
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
		catch (Exception ex) { LogService.Warn(L.Get("LogCat_SteamApi"), L.Get("Svc_ApiSpyFailed", appId, ex.Message)); }
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
			LogService.Warn(L.Get("LogCat_SteamApi"), L.Get("Svc_ApiCommunityFailed", appId, ex.Message));
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
			LogService.Warn(L.Get("LogCat_Cover"), L.Get("Svc_ApiCoverDownloadFailed", appId, ex.Message));
			return (null, true);
		}
	}

}
