using System.Net.Http;

namespace SteamLuaManager.Services;

/// <summary>统一 Http 请求头配置，避免各 Service 重复定义 UserAgent。</summary>
internal static class HttpHeaderHelper
{
    // 与 HttpClientProvider.CreateClient 保持一致的完整浏览器 UA
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    private const string AppUserAgent = "FluentSteamLuaManager/1.0";

    /// <summary>浏览器 UA（Steam API / Depot / Manifest 等通用）。</summary>
    public static void ConfigureBrowser(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
    }

    /// <summary>应用自身 UA（更新检查等非浏览器请求）。</summary>
    public static void ConfigureApp(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppUserAgent);
    }

    /// <summary>浏览器 UA + JSON Accept（Store API 等）。</summary>
    public static void ConfigureBrowserJson(HttpClient client)
    {
        ConfigureBrowser(client);
        if (!client.DefaultRequestHeaders.Accept.Any())
            client.DefaultRequestHeaders.Add("Accept", "application/json");
    }
}
