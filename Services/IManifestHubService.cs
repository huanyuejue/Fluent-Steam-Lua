using System.Net;
using System.Net.Http;
using System.IO;

namespace SteamLuaManager.Services;

public record ManifestHubResult(bool Success, bool KeyInvalid, string? PlacedPath, string? Error, bool NotFound = false, int? StatusCode = null);

public interface IManifestHubService
{
    Task<ManifestHubResult> DownloadAsync(int depotId, string manifestId, string apiKey, CancellationToken ct = default);
}

public class ManifestHubService : IManifestHubService
{
    private const string ApiUrl = "https://api.manifesthub2.filegear-sg.me/manifest";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;

    public ManifestHubService(IHttpClientProvider httpClientProvider, ISteamPathService steamPathService)
    {
        _httpClientProvider = httpClientProvider;
        _steamPathService = steamPathService;
    }

    // ManifestHub 回的是无压缩裸 manifest，我只做一次 GET，不做解析以求最快；
    // 落盘用临时文件 + 同卷原子改名，避免 Steam 读到半截文件
    public async Task<ManifestHubResult> DownloadAsync(int depotId, string manifestId, string apiKey, CancellationToken ct = default)
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            return new ManifestHubResult(false, false, null, "未检测到 Steam 路径");
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ManifestHubResult(false, true, null, "未配置 API Key");

        var url = $"{ApiUrl}?apikey={Uri.EscapeDataString(apiKey)}&depotid={depotId}&manifestid={Uri.EscapeDataString(manifestId)}";
        try
        {
            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                "manifest-hub", Timeout,
                client => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct),
                HttpHeaderHelper.ConfigureBrowser);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                return new ManifestHubResult(false, true, null, "API Key 无效或已过期，请重新获取", false, (int)response.StatusCode);
            // 我把 404/400 单独判为"确认没有"：调用方据此进 DLC 移除流程；
            // 429/5xx/超时等一律进未知桶（可重试），不诱导删行。状态码直接取 response，不走抛异常，
            // 免得异常链把真实状态码弄丢，调用方无法区分"没有"和"限流"。
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.BadRequest)
                return new ManifestHubResult(false, false, null, $"该 depot 暂无可用 manifest（{(int)response.StatusCode}）", true, (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
                return new ManifestHubResult(false, false, null, $"接口返回 {(int)response.StatusCode} {response.ReasonPhrase}".Trim(), false, (int)response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
                return new ManifestHubResult(false, false, null, "接口返回空内容", false, (int)response.StatusCode);

            var depotCacheDir = Path.Combine(steamPath, "depotcache");
            Directory.CreateDirectory(depotCacheDir);
            var destPath = Path.Combine(depotCacheDir, $"{depotId}_{manifestId}.manifest");
            var tmpPath = destPath + $".tmp_{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllBytesAsync(tmpPath, bytes, ct);
                File.Move(tmpPath, destPath, true);
            }
            finally
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            }
            return new ManifestHubResult(true, false, destPath, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // 超时（非用户取消）状态码记空，调用方按瞬时失败重试
        catch (OperationCanceledException)
        {
            return new ManifestHubResult(false, false, null, "请求超时");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.BadRequest)
        {
            return new ManifestHubResult(false, false, null, "该 depot 暂无可用 manifest", true, (int?)ex.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return new ManifestHubResult(false, false, null, ex.Message, false, (int?)ex.StatusCode);
        }
        catch (Exception ex)
        {
            return new ManifestHubResult(false, false, null, ex.Message);
        }
    }
}
