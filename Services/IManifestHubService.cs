using System.Net;
using System.Net.Http;
using System.IO;

namespace SteamLuaManager.Services;

public record ManifestHubResult(bool Success, bool KeyInvalid, string? PlacedPath, string? Error);

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
                return new ManifestHubResult(false, true, null, "API Key 无效或已过期，请重新获取");
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
                return new ManifestHubResult(false, false, null, "接口返回空内容");

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
        catch (Exception ex)
        {
            return new ManifestHubResult(false, false, null, ex.Message);
        }
    }
}
