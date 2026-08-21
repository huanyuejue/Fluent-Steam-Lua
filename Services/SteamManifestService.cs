using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public class SteamManifestService : ISteamManifestService
{
    private readonly IHttpClientProvider _httpClientProvider;

    public SteamManifestService(IHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    public Dictionary<int, string> ParseMountedDepots(string acfPath)
    {
        var result = new Dictionary<int, string>();
        try
        {
            var content = File.ReadAllText(acfPath);

            var depotMatches = Regex.Matches(content,
                @"""(\d+)""\s*\{\s*""manifest""\s+""(\d+)""",
                RegexOptions.Singleline);
            foreach (Match match in depotMatches)
            {
                if (int.TryParse(match.Groups[1].Value, out var depotId))
                    result[depotId] = match.Groups[2].Value;
            }

            if (result.Count == 0)
            {
                var mountSection = Regex.Match(content,
                    @"""MountedDepots""\s*\{([^}]*)\}",
                    RegexOptions.Singleline);
                if (mountSection.Success)
                {
                    var inner = mountSection.Groups[1].Value;
                    var pairs = Regex.Matches(inner, @"""(\d+)""\s+""(\d+)""");
                    foreach (Match pair in pairs)
                    {
                        if (int.TryParse(pair.Groups[1].Value, out var depotId))
                            result[depotId] = pair.Groups[2].Value;
                    }
                }
            }
        }
        catch (Exception ex) { LogService.Warn("版本固定", $"解析 MountedManifests 失败: {ex.Message}"); }
        return result;
    }

    public async Task<string?> FetchLatestManifestIdAsync(int appId, int depotId)
    {
        try
        {
            var url = $"https://api.steamcmd.net/v1/info/{appId}";
            var response = await _httpClientProvider.SendWithProxyRetryAsync(
                "steam-manifest",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync(url),
                HttpHeaderHelper.ConfigureBrowserJson);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data)) return null;
            if (!data.TryGetProperty(appId.ToString(), out var app)) return null;
            if (!app.TryGetProperty("depots", out var depots)) return null;
            if (!depots.TryGetProperty(depotId.ToString(), out var depot)) return null;
            if (!depot.TryGetProperty("manifests", out var manifests)) return null;
            if (!manifests.TryGetProperty("public", out var pub)) return null;
            if (!pub.TryGetProperty("gid", out var gid)) return null;

            return gid.GetString();
        }
        catch (Exception ex) { LogService.Warn("版本固定", $"查询最新清单 ID 失败 (AppID {appId}, Depot {depotId}): {ex.Message}"); }
        return null;
    }
}
