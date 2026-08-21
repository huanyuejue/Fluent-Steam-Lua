using System.Net.Http;

namespace SteamLuaManager.Services;

/// <summary>统一 Http 请求头配置，避免各 Service 重复定义 UserAgent。</summary>
internal static class HttpHeaderHelper
{
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

    /// <summary>浏览器 UA（Steam API / Depot / Manifest 等通用）。</summary>
    public static void ConfigureBrowser(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
    }

    /// <summary>浏览器 UA + JSON Accept（Store API 等）。</summary>
    public static void ConfigureBrowserJson(HttpClient client)
    {
        ConfigureBrowser(client);
        if (!client.DefaultRequestHeaders.Accept.Any())
            client.DefaultRequestHeaders.Add("Accept", "application/json");
    }
}
