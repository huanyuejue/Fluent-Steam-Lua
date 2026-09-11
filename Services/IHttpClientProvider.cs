using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Linq;
using Microsoft.Win32;

namespace SteamLuaManager.Services;

public interface IHttpClientProvider
{
    HttpClient GetClient(string name, TimeSpan timeout, Action<HttpClient>? configure = null);
    Task<T> SendWithProxyRetryAsync<T>(string name, TimeSpan timeout, Func<HttpClient, Task<T>> sendAsync, Action<HttpClient>? configure = null);
    Task SendWithProxyRetryAsync(string name, TimeSpan timeout, Func<HttpClient, Task> sendAsync, Action<HttpClient>? configure = null);
    void Reset(string? name = null);
}

public sealed class HttpClientProvider : IHttpClientProvider, IDisposable
{
    private sealed record ClientEntry(HttpClient Client, string ProxySignature);
    private sealed record ProxySnapshot(string Signature, IWebProxy? Proxy, bool UseProxy);

    private readonly object _lock = new();
    private readonly Dictionary<string, ClientEntry> _clients = new();
    // 代理快照缓存：注册表/PAC 探测代价高，避免每次 GetClient 都执行
    private ProxySnapshot? _proxySnapshot;
    private DateTime _proxySnapshotTime = DateTime.MinValue;
    private static readonly TimeSpan ProxySnapshotLifetime = TimeSpan.FromSeconds(10);

    public HttpClientProvider()
    {
        // 梯子开/关（TUN/路由/网卡变化）时丢弃连接池与代理快照，下次请求重建；
        // 注册表代理项变化无系统事件，靠短 TTL + 连接失败强制刷新兜底。
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        try { Reset(); } catch { }
    }

    public HttpClient GetClient(string name, TimeSpan timeout, Action<HttpClient>? configure = null)
    {
        var proxy = GetProxySnapshotCached();
        var key = CacheKey(name, timeout);
        lock (_lock)
        {
            return GetOrCreateLocked(key, timeout, configure, proxy);
        }
    }

    public async Task<T> SendWithProxyRetryAsync<T>(string name, TimeSpan timeout, Func<HttpClient, Task<T>> sendAsync, Action<HttpClient>? configure = null)
    {
        // 最多尝试 3 次：并发下旧实例被废弃是常态，靠“仅当字典仍持有才废弃”收敛，
        // 孤儿连接上的在飞请求不受影响
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var client = GetClient(name, timeout, configure);
            try
            {
                return await sendAsync(client);
            }
            catch (Exception ex) when (ShouldRefreshClient(ex))
            {
                lastError = ex;
                InvalidateIfCurrent(name, timeout, client);
                // 连接级失败（代理挂了/梯子刚切换）时不等 TTL，直接刷新快照让下次重试读到新代理
                if (IsConnectionFailure(ex))
                    RefreshProxySnapshot();
            }
        }
        throw lastError!;
    }

    public async Task SendWithProxyRetryAsync(string name, TimeSpan timeout, Func<HttpClient, Task> sendAsync, Action<HttpClient>? configure = null)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var client = GetClient(name, timeout, configure);
            try
            {
                await sendAsync(client);
                return;
            }
            catch (Exception ex) when (ShouldRefreshClient(ex))
            {
                lastError = ex;
                InvalidateIfCurrent(name, timeout, client);
                if (IsConnectionFailure(ex))
                    RefreshProxySnapshot();
            }
        }
        throw lastError!;
    }

    private static string CacheKey(string name, TimeSpan timeout) => $"{name}|{timeout.Ticks}";

    private HttpClient GetOrCreateLocked(string key, TimeSpan timeout, Action<HttpClient>? configure, ProxySnapshot proxy)
    {
        if (_clients.TryGetValue(key, out var entry) && entry.ProxySignature == proxy.Signature)
            return entry.Client;

        // 淘汰旧实例只摘除不 Dispose（理由同上），新请求用新实例
        _clients.Remove(key);

        var client = CreateClient(timeout, proxy);
        configure?.Invoke(client);
        _clients[key] = new ClientEntry(client, proxy.Signature);
        return client;
    }

    // 只有字典里仍是这个实例才废弃：只摘除不 Dispose——在飞请求拿着旧实例继续跑不受影响，
    // 旧实例靠 GC/终结器回收。之前这里直接 Dispose，是并发下载连环炸的根因。
    private void InvalidateIfCurrent(string name, TimeSpan timeout, HttpClient client)
    {
        var key = CacheKey(name, timeout);
        lock (_lock)
        {
            if (_clients.TryGetValue(key, out var entry) && ReferenceEquals(entry.Client, client))
                _clients.Remove(key);
        }
    }

    public void Reset(string? name = null)
    {
        lock (_lock)
        {
            // 同上：只摘除不清掉，避免误杀在飞请求；连接池 2 分钟空闲自回收 + GC 兜底
            if (name != null)
            {
                var prefix = name + "|";
                foreach (var key in _clients.Keys.Where(k => k == name || k.StartsWith(prefix)).ToList())
                    _clients.Remove(key);
                return;
            }

            _clients.Clear();
            _proxySnapshot = null;
            _proxySnapshotTime = DateTime.MinValue;
        }
    }

    private ProxySnapshot GetProxySnapshotCached()
    {
        lock (_lock)
        {
            if (_proxySnapshot != null && DateTime.UtcNow - _proxySnapshotTime < ProxySnapshotLifetime)
                return _proxySnapshot;
        }
        var snapshot = GetProxySnapshot();
        lock (_lock)
        {
            _proxySnapshot = snapshot;
            _proxySnapshotTime = DateTime.UtcNow;
        }
        return snapshot;
    }

    private void RefreshProxySnapshot()
    {
        lock (_lock)
        {
            _proxySnapshot = null;
            _proxySnapshotTime = DateTime.MinValue;
        }
    }

    // 顺着 InnerException 找连接级根因：直连被拒/代理失联都算，触发快照强制刷新
    private static bool IsConnectionFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException) return true;
            if (e is HttpRequestException { StatusCode: HttpStatusCode.ProxyAuthenticationRequired }) return true;
        }
        return false;
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        lock (_lock)
        {
            foreach (var entry in _clients.Values)
            {
                try { entry.Client.Dispose(); } catch { }
            }
            Reset();
        }
    }

    private static HttpClient CreateClient(TimeSpan timeout, ProxySnapshot proxy)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = proxy.UseProxy,
            Proxy = proxy.Proxy,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        if (handler.Proxy != null)
            handler.Proxy.Credentials = CredentialCache.DefaultCredentials;

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };

        // 带浏览器指纹头，避免 Cloudflare 等 CDN 将空 User-Agent 的无头请求判为 bot，
        // 向其下发"5 秒 JS 托管挑战"，导致请求从 ~2s 恶化到 5~6s 且响应无有效内容
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");

        return client;
    }

    private static ProxySnapshot GetProxySnapshot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var proxyEnable = key?.GetValue("ProxyEnable") is int enabled && enabled == 1;
            var proxyServer = key?.GetValue("ProxyServer") as string ?? string.Empty;
            var autoConfigUrl = key?.GetValue("AutoConfigURL") as string ?? string.Empty;

            if (proxyEnable && TryCreateExplicitProxy(proxyServer, out var explicitProxy, out var explicitSignature))
                return new ProxySnapshot($"explicit|{explicitSignature}|{autoConfigUrl}", explicitProxy, true);

            if (!string.IsNullOrWhiteSpace(autoConfigUrl))
            {
                var systemProxy = WebRequest.GetSystemWebProxy();
                systemProxy.Credentials = CredentialCache.DefaultCredentials;
                var http = systemProxy.GetProxy(new Uri("http://store.steampowered.com/"))?.ToString() ?? string.Empty;
                var https = systemProxy.GetProxy(new Uri("https://store.steampowered.com/"))?.ToString() ?? string.Empty;
                return new ProxySnapshot($"auto|{autoConfigUrl}|{http}|{https}", systemProxy, true);
            }

            return new ProxySnapshot("direct", null, false);
        }
        catch
        {
            return new ProxySnapshot("fallback-system", WebRequest.GetSystemWebProxy(), true);
        }
    }

    private static bool TryCreateExplicitProxy(string proxyServer, out IWebProxy? proxy, out string signature)
    {
        proxy = null;
        signature = string.Empty;
        if (string.IsNullOrWhiteSpace(proxyServer)) return false;

        var endpoint = proxyServer;
        if (proxyServer.Contains(';'))
        {
            var entries = proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            endpoint = entries
                .Select(entry => entry.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .OrderBy(parts => parts[0].Equals("https", StringComparison.OrdinalIgnoreCase) ? 0 :
                                  parts[0].Equals("http", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .Select(parts => parts[1])
                .FirstOrDefault() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        if (!endpoint.Contains("://", StringComparison.Ordinal))
            endpoint = "http://" + endpoint;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var proxyUri)) return false;

        proxy = new WebProxy(proxyUri)
        {
            Credentials = CredentialCache.DefaultCredentials
        };
        signature = proxyUri.ToString();
        return true;
    }

    private static bool ShouldRefreshClient(Exception ex)
    {
        // ObjectDisposed 表示共享连接被并发的 Reset 拆掉，重建一次即可自愈
        return ex is HttpRequestException or TaskCanceledException or ObjectDisposedException ||
                ex.InnerException is HttpRequestException or WebException;
    }
}
