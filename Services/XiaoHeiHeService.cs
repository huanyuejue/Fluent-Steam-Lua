using System.Net.Http;
using System.Text.Json;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

// 小黑盒 appid 查询源：入库与补名时按 AppID 取中文名、封面与发售日期。
// 搜索接口已加登录校验，名字搜索改由 SteamDbKeylolService 承担。
// 未公开接口，解析按缺字段即跳过处理；来源标注只写 app.log（LogService），不进窗口日志。
public sealed record HeiHeGame(int AppId, string Name, string CoverUrl, List<string> CoverCandidates, string ReleaseDate = "", string Type = "");

public static class XiaoHeiHeService
{
    private const string DetailUrl = "https://api.xiaoheihe.cn/game/web/get_game_detail/?appid={0}";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // appid 精确补位：只补中文名、封面与发售日期；result 为空对象判 miss
    public static async Task<(string? Name, string? CoverUrl, string ReleaseDate)> GetGameDetailAsync(int appId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(string.Format(DetailUrl, appId), ct);
            if (!resp.IsSuccessStatusCode)
            {
                LogService.Warn(L.Get("LogCat_Home"), L.Get("Svc_HhDetailFail", appId, (int)resp.StatusCode));
                return (null, null, "");
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var r) ||
                r.ValueKind != JsonValueKind.Object ||
                !r.TryGetProperty("name", out var n))
            {
                LogService.Info(L.Get("LogCat_Home"), L.Get("Svc_HhDetailNoData", appId));
                return (null, null, "");
            }
            var name = n.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                LogService.Info(L.Get("LogCat_Home"), L.Get("Svc_HhDetailNoData", appId));
                return (null, null, "");
            }
            var cover = r.TryGetProperty("image", out var img) ? img.GetString() : null;
            var release = r.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String
                ? rd.GetString() ?? "" : "";
            LogService.Info(L.Get("LogCat_Home"), L.Get("Svc_HhDetailHit", appId, name));
            return (name, string.IsNullOrWhiteSpace(cover) ? null : cover, release.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Home"), L.Get("Svc_HhDetailError", appId, ex.Message));
            return (null, null, "");
        }
    }

    // 热门榜单接口按页返回，翻页拼满榜单池；深页条目同样固定不会跳变
    private const string RankUrl = "https://api.xiaoheihe.cn/game/web/all_recommend/games/?os_type=web&version=999.0.0&show_type=popular&limit={0}&offset={1}";

    // 热门池：并行拉多页拼起来，单页失败不影响其他页
    public static async Task<List<RankGame>> GetPopularPoolAsync(int total = 120, CancellationToken ct = default)
    {
        var pageTasks = new List<Task<List<RankGame>>>();
        for (var offset = 0; offset < total; offset += 30)
            pageTasks.Add(GetRankPageAsync(offset, Math.Min(30, total - offset), ct));
        var pages = await Task.WhenAll(pageTasks).ConfigureAwait(false);
        var seen = new HashSet<int>();
        var all = new List<RankGame>();
        foreach (var page in pages)
            foreach (var g in page)
                if (seen.Add(g.AppId))
                    all.Add(g);
        return all.Take(total).ToList();
    }

    private static async Task<List<RankGame>> GetRankPageAsync(int offset, int limit, CancellationToken ct)
    {
        var result = new List<RankGame>();
        try
        {
            using var resp = await _http.GetAsync(string.Format(RankUrl, limit, offset), ct);
            if (!resp.IsSuccessStatusCode)
            {
                LogService.Warn(L.Get("LogCat_Home"), L.Get("Svc_HhRankFail", (int)resp.StatusCode));
                return result;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var r) ||
                !r.TryGetProperty("list", out var list) ||
                list.ValueKind != JsonValueKind.Array)
            {
                LogService.Info(L.Get("LogCat_Home"), L.Get("Svc_HhRankNoResult"));
                return result;
            }
            foreach (var g in list.EnumerateArray())
            {
                try
                {
                    if (!TryGetRankAppId(g, out var appId)) continue;
                    var name = g.TryGetProperty("game_name", out var n) ? n.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(name) && g.TryGetProperty("name", out var n2))
                        name = n2.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var cover = g.TryGetProperty("game_img", out var c) ? c.GetString() ?? "" : "";
                    var covers = new List<string>();
                    if (!string.IsNullOrWhiteSpace(cover)) covers.Add(cover);
                    var fallback = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
                    if (!covers.Contains(fallback)) covers.Add(fallback);
                    long follow = 0;
                    if (g.TryGetProperty("follow_num", out var f))
                    {
                        if (f.ValueKind == JsonValueKind.Number) follow = f.GetInt64();
                        else if (f.ValueKind == JsonValueKind.String && long.TryParse(f.GetString(), out var fs)) follow = fs;
                    }
                    result.Add(new RankGame(appId, name, covers[0], covers, follow));
                    if (result.Count >= limit) break;
                }
                catch { }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Home"), L.Get("Svc_HhRankError", ex.Message));
        }
        return result;
    }

    private static bool TryGetRankAppId(JsonElement g, out int appId)
    {
        appId = 0;
        if (!g.TryGetProperty("appid", out var aid)) return false;
        if (aid.ValueKind == JsonValueKind.Number)
        {
            if (!aid.TryGetInt32(out appId) || appId <= 0) return false;
        }
        else if (aid.ValueKind == JsonValueKind.String)
        {
            if (!int.TryParse(aid.GetString(), out appId) || appId <= 0) return false;
        }
        else return false;
        return true;
    }
}
