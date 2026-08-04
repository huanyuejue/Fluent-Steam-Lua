using System.Net;
using System.Net.Http;
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

    public HttpClient GetClient(string name, TimeSpan timeout, Action<HttpClient>? configure = null)
    {
        var proxy = GetProxySnapshot();
        lock (_lock)
        {
            if (_clients.TryGetValue(name, out var entry) && entry.ProxySignature == proxy.Signature)
                return entry.Client;

            if (_clients.Remove(name, out entry))
                entry.Client.Dispose();

            var client = CreateClient(timeout, proxy);
            configure?.Invoke(client);
            _clients[name] = new ClientEntry(client, proxy.Signature);
            return client;
        }
    }

    public async Task<T> SendWithProxyRetryAsync<T>(string name, TimeSpan timeout, Func<HttpClient, Task<T>> sendAsync, Action<HttpClient>? configure = null)
    {
        try
        {
            return await sendAsync(GetClient(name, timeout, configure));
        }
        catch (Exception ex) when (ShouldRefreshClient(ex))
        {
            Reset(name);
            return await sendAsync(GetClient(name, timeout, configure));
        }
    }

    public async Task SendWithProxyRetryAsync(string name, TimeSpan timeout, Func<HttpClient, Task> sendAsync, Action<HttpClient>? configure = null)
    {
        try
        {
            await sendAsync(GetClient(name, timeout, configure));
        }
        catch (Exception ex) when (ShouldRefreshClient(ex))
        {
            Reset(name);
            await sendAsync(GetClient(name, timeout, configure));
        }
    }

    public void Reset(string? name = null)
    {
        lock (_lock)
        {
            if (name != null)
            {
                if (_clients.Remove(name, out var entry))
                    entry.Client.Dispose();
                return;
            }

            foreach (var entry in _clients.Values)
                entry.Client.Dispose();
            _clients.Clear();
        }
    }

    public void Dispose()
    {
        Reset();
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
        return ex is HttpRequestException or TaskCanceledException ||
               ex.InnerException is HttpRequestException or WebException;
    }
}
