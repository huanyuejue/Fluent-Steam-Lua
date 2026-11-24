using System.Net;
using System.Text.Json;
using SteamLuaManager.Services;

namespace FluentLuaBridge;

/// <summary>Resolve the window header in the app language, independently of the Store page.</summary>
internal sealed class BridgeGameNameResolver
{
    private readonly IHttpClientProvider _http;

    public BridgeGameNameResolver(IHttpClientProvider http) => _http = http;

    public async Task<string> ResolveAsync(int appId, string? fallback, CancellationToken ct)
    {
        var language = BridgeLocalization.Language;
        try
        {
            var steamLanguage = language == "zh" ? "schinese" : "english";
            var country = language == "zh" ? "cn" : "us";
            var client = _http.GetClient("BridgeNames-" + language, TimeSpan.FromSeconds(8), c =>
            {
                c.DefaultRequestHeaders.AcceptLanguage.Clear();
                c.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language == "zh" ? "zh-CN,zh;q=0.9" : "en-US,en;q=0.9");
            });
            using var response = await client.GetAsync(
                $"https://store.steampowered.com/api/appdetails?appids={appId}&l={steamLanguage}&cc={country}&filters=basic", ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty(appId.ToString(), out var app)
                && app.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
                && app.TryGetProperty("data", out var data)
                && data.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && Validate(name.GetString(), language) is { } resolved)
                return resolved;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // Name lookup should not prevent an otherwise valid import.
        }
        return Validate(fallback, language) ?? $"AppID {appId}";
    }

    internal static string? Validate(string? name, string language)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = WebUtility.HtmlDecode(name).Trim();
        if (name.Equals("Error", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("AppID", StringComparison.OrdinalIgnoreCase)
            || (language == "en" && name.Any(c => c is >= '\u3400' and <= '\u9fff')))
            return null;
        return name;
    }
}
