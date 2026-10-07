using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace SteamLuaManager.Services;

// Steam 官方内容服务器列表：匿名可调，按 weighted_load 排序轮询。
// cell 取 Steam 客户端自己的 CellID（读不到则用 0 让服务端按 IP 就近）。
public sealed class CdnServerListService
{
    private const string ServerListUrlTemplate = "https://api.steampowered.com/IContentServerDirectoryService/GetServersForSteamPipe/v1/?cell_id={0}";
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;
    private readonly object _lock = new();
    private List<string> _cachedHosts = new();
    private DateTime _cachedAt = DateTime.MinValue;

    public CdnServerListService(IHttpClientProvider httpClientProvider, ISteamPathService steamPathService)
    {
        _httpClientProvider = httpClientProvider;
        _steamPathService = steamPathService;
    }

    public async Task<List<string>> GetHostsAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_cachedHosts.Count > 0 && DateTime.UtcNow - _cachedAt < CacheLifetime)
                return new List<string>(_cachedHosts);
        }
        var hosts = await FetchHostsAsync(ct);
        lock (_lock)
        {
            if (hosts.Count > 0)
            {
                _cachedHosts = hosts;
                _cachedAt = DateTime.UtcNow;
            }
            return new List<string>(_cachedHosts);
        }
    }

    private async Task<List<string>> FetchHostsAsync(CancellationToken ct)
    {
        // Steam 客户端自己的 CellID（config.vdf 的 CellIDServerOverride），读不到就用 0 让服务端按 IP 就近
        var cellId = ResolveCellId();
        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, ServerListUrlTemplate, cellId);
        try
        {
            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                "steam-cdn-list", ListTimeout,
                client => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct),
                HttpHeaderHelper.ConfigureBrowser,
                maxAttempts: 1);
            if (!response.IsSuccessStatusCode) return new List<string>();
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("response", out var resp) ||
                !resp.TryGetProperty("servers", out var servers))
                return new List<string>();

            var scored = new List<(double Load, string Host)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in servers.EnumerateArray())
            {
                string? host;
                try
                {
                    // 单个条目异常只跳过该条目，不连累整个列表
                    if (!s.TryGetProperty("host", out var hostEl)) continue;
                    host = hostEl.GetString();
                }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(host) || !seen.Add(host)) continue;
                // OpenCache/代理劫持类节点不支持直下，跳过
                if (s.TryGetProperty("use_as_proxy", out var proxy) &&
                    proxy.ValueKind == JsonValueKind.True)
                    continue;
                if (s.TryGetProperty("type", out var typeEl) &&
                    string.Equals(typeEl.GetString(), "OpenCache", StringComparison.OrdinalIgnoreCase))
                    continue;
                // 不支持 https 的节点跳过（只发 https 请求）
                if (s.TryGetProperty("https_support", out var httpsEl) &&
                    string.Equals(httpsEl.GetString(), "unavailable", StringComparison.OrdinalIgnoreCase))
                    continue;
                double load = double.MaxValue;
                if (s.TryGetProperty("weighted_load", out var loadEl) &&
                    loadEl.ValueKind == JsonValueKind.Number &&
                    loadEl.TryGetDouble(out var v))
                    load = v;
                scored.Add((load, host));
            }
            scored.Sort((a, b) => a.Load.CompareTo(b.Load));
            return scored.Select(x => x.Host).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return new List<string>();
        }
    }

    private int ResolveCellId()
    {
        try
        {
            var steamPath = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(steamPath)) return 0;
            var configPath = Path.Combine(steamPath, "config", "config.vdf");
            if (!File.Exists(configPath)) return 0;
            // 文件小（几十 KB），直接全文正则；只要 CellIDServerOverride，不做地区名映射
            var text = File.ReadAllText(configPath);
            var m = System.Text.RegularExpressions.Regex.Match(text, @"CellIDServerOverride""\s+""(\d+)""");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var cell) && cell >= 0)
                return cell;
        }
        catch { }
        return 0;
    }
}
