namespace SteamLuaManager.Services;

// 清单抓取策略抽象：ManifestHub 直下文件 与 Code 取码+CDN 下载共用同一套监听引擎。
public sealed record ManifestFetchResult(
    bool Success,
    bool KeyInvalid,
    string? Error,
    bool NotFound = false,
    string? SourceName = null);

public interface IManifestFetcher
{
    Task<ManifestFetchResult> FetchAsync(int depotId, string manifestId, CancellationToken ct, Action<string>? progress = null);
}

// Hub 适配：把现有 ManifestHubService 包装成 fetcher，Key 每次现取现用。
public sealed class HubManifestFetcher : IManifestFetcher
{
    private readonly IManifestHubService _hubService;
    private readonly IManifestHubKeyService _keyService;

    public HubManifestFetcher(IManifestHubService hubService, IManifestHubKeyService keyService)
    {
        _hubService = hubService;
        _keyService = keyService;
    }

    public async Task<ManifestFetchResult> FetchAsync(int depotId, string manifestId, CancellationToken ct, Action<string>? progress = null)
    {
        var key = _keyService.LoadKey();
        if (string.IsNullOrWhiteSpace(key))
            return new ManifestFetchResult(false, true, "未配置 API Key");
        var r = await _hubService.DownloadAsync(depotId, manifestId, key, ct, progress);
        return new ManifestFetchResult(r.Success, r.KeyInvalid, r.Error, r.NotFound,
            r.Success ? "ManifestHub" : null);
    }
}
