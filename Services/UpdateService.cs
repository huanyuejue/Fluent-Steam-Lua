using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SteamLuaManager.Services;

public sealed record UpdateCheckResult(bool HasUpdate, Version CurrentVersion, Version LatestVersion, string TagName, string ReleaseUrl);

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default);
}

public class UpdateService : IUpdateService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private const string ProjectUrl = "https://github.com/huanyuejue/Fluent-Steam-Lua";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/huanyuejue/Fluent-Steam-Lua/releases/latest";

    public UpdateService(IHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var json = await _httpClientProvider.SendWithProxyRetryAsync(
            "app-update-check",
            TimeSpan.FromSeconds(20),
            client => client.GetStringAsync(LatestReleaseApiUrl, ct),
            ConfigureHeaders);

        using var doc = JsonDocument.Parse(json);
        var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        var releaseUrl = doc.RootElement.GetProperty("html_url").GetString()
            ?? $"{ProjectUrl}/releases/latest";

        var latestVersion = ParseReleaseVersion(tagName)
            ?? throw new InvalidOperationException($"无法识别最新版本号：{tagName}");
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        var currentVersion = new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);

        return new UpdateCheckResult(latestVersion > currentVersion, currentVersion, latestVersion, tagName, releaseUrl);
    }

    private static void ConfigureHeaders(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FluentSteamLuaManager/1.0");
    }

    private static Version? ParseReleaseVersion(string tagName)
    {
        var versionText = tagName.Trim().TrimStart('v', 'V');
        return Version.TryParse(versionText, out var version) ? version : null;
    }
}
