using System.Net;
using System.Net.Http;
using System.IO;

namespace SteamLuaManager.Services;

public record ManifestHubResult(bool Success, bool KeyInvalid, string? PlacedPath, string? Error, bool NotFound = false, int? StatusCode = null);

public interface IManifestHubService
{
    Task<ManifestHubResult> DownloadAsync(int depotId, string manifestId, string apiKey, CancellationToken ct = default, Action<string>? progress = null);
}

public class ManifestHubService : IManifestHubService
{
    private const string ApiUrl = "https://api.manifesthub2.filegear-sg.me/manifest";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    // 限流重试：免费 Key 并发一高就 429，退避几轮基本能过；终端状态不重试
    private const int MaxDownloadRetries = 3;
    private static readonly TimeSpan[] RetryBackoffs = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;

    public ManifestHubService(IHttpClientProvider httpClientProvider, ISteamPathService steamPathService)
    {
        _httpClientProvider = httpClientProvider;
        _steamPathService = steamPathService;
    }

    // ManifestHub 回的是无压缩裸 manifest，不做解析以求最快；
    // 落盘用临时文件 + 同卷原子改名，避免 Steam 读到半截文件
    public async Task<ManifestHubResult> DownloadAsync(int depotId, string manifestId, string apiKey, CancellationToken ct = default, Action<string>? progress = null)
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            return new ManifestHubResult(false, false, null, "未检测到 Steam 路径");
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ManifestHubResult(false, true, null, "未配置 API Key");

        var url = $"{ApiUrl}?apikey={Uri.EscapeDataString(apiKey)}&depotid={depotId}&manifestid={Uri.EscapeDataString(manifestId)}";
        ManifestHubResult? last = null;
        TimeSpan? retryAfter = null;
        for (var attempt = 0; attempt <= MaxDownloadRetries; attempt++)
        {
            if (attempt > 0)
            {
                // 重试等待放 try 之外：取消时直接抛 OCE 由调用方处理，不会被误判为超时
                var wait = ClampRetryDelay(retryAfter, attempt);
                progress?.Invoke($"Depot {depotId} 触发限流（{DescribeStatus(last)}），{wait.TotalSeconds:F0} 秒后重试（第 {attempt}/{MaxDownloadRetries} 次）");
                await Task.Delay(wait, ct);
            }
            bool transient;
            (last, retryAfter, transient) = await TryDownloadOnceAsync(url, steamPath, depotId, manifestId, ct);
            if (!transient) return last;
            if (attempt == MaxDownloadRetries)
                return last with { Error = $"{last.Error}，已重试 {MaxDownloadRetries} 次仍失败" };
        }
        return last!;
    }

    // 值得重试的只有瞬时故障：限流、网关类 5xx、超时/传输异常；
    // Key 无效、无 manifest、落盘失败、未知异常一律终端返回，不烧接口配额
    private static bool IsRetryableStatus(int? statusCode) =>
        statusCode is 429 or 500 or 502 or 503 or 504;

    private static string DescribeStatus(ManifestHubResult? r) =>
        r?.StatusCode?.ToString() ?? (r?.Error == "请求超时" ? "超时" : "网络异常");

    // 服务端 Retry-After 优先（钳制 1~30 秒），否则按 2/5/10 秒退避，均加半秒内抖动错开并发槽
    private static TimeSpan ClampRetryDelay(TimeSpan? retryAfter, int attempt)
    {
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
        if (retryAfter.HasValue)
        {
            var clamped = retryAfter.Value < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
                : retryAfter.Value > MaxRetryAfter ? MaxRetryAfter : retryAfter.Value;
            return clamped + jitter;
        }
        var backoff = attempt - 1 < RetryBackoffs.Length ? RetryBackoffs[attempt - 1] : RetryBackoffs[^1];
        return backoff + jitter;
    }

    private async Task<(ManifestHubResult Result, TimeSpan? RetryAfter, bool Transient)> TryDownloadOnceAsync(
        string url, string steamPath, int depotId, string manifestId, CancellationToken ct)
    {
        byte[] bytes;
        int statusCode;
        try
        {
            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                "manifest-hub", Timeout,
                client => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct),
                HttpHeaderHelper.ConfigureBrowser);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                return (new ManifestHubResult(false, true, null, "API Key 无效或已过期，请重新获取", false, (int)response.StatusCode), null, false);
            // 我把 404/400 单独判为"确认没有"：调用方据此进 DLC 移除流程；
            // 其余非成功不抛异常直接返回，免得异常链把真实状态码弄丢，调用方无法区分"没有"和"限流"。
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.BadRequest)
                return (new ManifestHubResult(false, false, null, $"该 depot 暂无可用 manifest（{(int)response.StatusCode}）", true, (int)response.StatusCode), null, false);
            if (!response.IsSuccessStatusCode)
                return (new ManifestHubResult(false, false, null, $"接口返回 {(int)response.StatusCode} {response.ReasonPhrase}".Trim(), false, (int)response.StatusCode), ReadRetryAfter(response), IsRetryableStatus((int)response.StatusCode));
            bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
                return (new ManifestHubResult(false, false, null, "接口返回空内容", false, (int)response.StatusCode), null, false);
            statusCode = (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // 超时（非用户取消）归入瞬时故障参与退避重试
        catch (OperationCanceledException)
        {
            return (new ManifestHubResult(false, false, null, "请求超时"), null, true);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.BadRequest)
        {
            return (new ManifestHubResult(false, false, null, "该 depot 暂无可用 manifest", true, (int?)ex.StatusCode), null, false);
        }
        catch (HttpRequestException ex)
        {
            return (new ManifestHubResult(false, false, null, ex.Message, false, (int?)ex.StatusCode), null, true);
        }
        catch (Exception ex)
        {
            return (new ManifestHubResult(false, false, null, ex.Message), null, false);
        }

        // 落盘是纯本地 IO：失败重试只会浪费接口配额，直接判终端失败
        var depotCacheDir = Path.Combine(steamPath, "depotcache");
        var destPath = Path.Combine(depotCacheDir, $"{depotId}_{manifestId}.manifest");
        var tmpPath = destPath + $".tmp_{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(depotCacheDir);
            await File.WriteAllBytesAsync(tmpPath, bytes, ct);
            File.Move(tmpPath, destPath, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (new ManifestHubResult(false, false, null, $"写入 depotcache 失败：{ex.Message}", false, statusCode), null, false);
        }
        finally
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
        return (new ManifestHubResult(true, false, destPath, null), null, false);
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        try
        {
            var value = response.Headers.RetryAfter;
            if (value == null) return null;
            if (value.Delta.HasValue) return value.Delta.Value;
            if (value.Date.HasValue)
            {
                var wait = value.Date.Value - DateTimeOffset.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }
        catch { }
        return null;
    }
}
