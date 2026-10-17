using System.Net.Http;
using System.Text.Json;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

// 小黑盒国内搜索源：入库前置的名字搜索与 appid 精确补位优先走这里，
// 搜不到再降级到现有国外链路。未公开接口，解析按缺字段即跳过处理。
// 来源标注只写 app.log（LogService），不进窗口日志。
public sealed record HeiHeGame(int AppId, string Name, string CoverUrl, List<string> CoverCandidates, string ReleaseDate = "", string Type = "");

public static class XiaoHeiHeService
{
    private const string SearchUrl = "https://api.xiaoheihe.cn/game/search/?q={0}&limit=10&offset=0";
    private const string DetailUrl = "https://api.xiaoheihe.cn/game/web/get_game_detail/?appid={0}";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // 名字搜索：只收 Steam 平台有效条目，按名含关键词/type/关注数加权排序，取前 10；
    // 关键词归一化（去标点）后匹配，无一匹配判 miss 走替补链，避免乱码返回一堆无关结果
    public static async Task<List<HeiHeGame>> SearchByNameAsync(string keyword, CancellationToken ct = default)
    {
        var result = new List<HeiHeGame>();
        var normKeyword = Norm(keyword);
        if (string.IsNullOrEmpty(normKeyword))
            return result;
        try
        {
            using var resp = await _http.GetAsync(
                string.Format(SearchUrl, Uri.EscapeDataString(keyword)), ct);
            if (!resp.IsSuccessStatusCode)
            {
                LogService.Warn(L.Get("LogCat_Home"), L.Get("Svc_HhSearchFail", (int)resp.StatusCode));
                return result;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var r) ||
                !r.TryGetProperty("games", out var games) ||
                games.ValueKind != JsonValueKind.Array)
            {
                LogService.Info(L.Get("LogCat_Home"), L.Get("Svc_HhNoResult"));
                return result;
            }
            var scored = new List<(HeiHeGame Game, bool NameHit, bool IsGame, int Follow)>();
            foreach (var g in games.EnumerateArray())
            {
                if (!TryGetSteamAppId(g, out var appId)) continue;
                var name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                var type = g.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                // 封面候选：Steam 直链优先，heybox 备用（未发售游戏 Steam 侧可能无图，如 4209920）；
                // 竖图裁切难看但聊胜于无，老模板垫底（新游戏已 404）
                var head = g.TryGetProperty("head_image_616_353", out var h) ? h.GetString() ?? "" : "";
                var img = g.TryGetProperty("image", out var im) ? im.GetString() ?? "" : "";
                var vertical = g.TryGetProperty("vertical_head_image", out var v) ? v.GetString() ?? "" : "";
                var covers = new List<string>();
                foreach (var u in new[] { head, img, vertical,
                             $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg" })
                {
                    if (!string.IsNullOrEmpty(u) && !covers.Contains(u))
                        covers.Add(u);
                }
                var follow = g.TryGetProperty("follow_num", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var fn) ? fn : 0;
                // 发售日期展示用：release_date 已是中文可读格式，缺失退到 release_date_desc；
                // 非字符串类型直接丢弃（GetString 对数字会抛异常）
                var release = g.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.String
                    ? rd.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(release))
                    release = g.TryGetProperty("release_date_desc", out var rdd) && rdd.ValueKind == JsonValueKind.String
                        ? rdd.GetString() ?? "" : "";
                scored.Add((new HeiHeGame(appId, name, covers[0], covers, release.Trim(), type),
                    Norm(name).Contains(normKeyword, StringComparison.Ordinal),
                    type == "game", follow));
            }
            var ordered = scored
                .OrderByDescending(x => x.NameHit)
                .ThenByDescending(x => x.IsGame)
                .ThenByDescending(x => x.Follow)
                .ToList();
            // 门控：名中至少一个，或头名是高关注游戏（英文搜中文名场景，如 ass 搜出刺客信条）；
            // 否则判 miss 走替补，避免乱码展示一堆无关结果
            var top = ordered.FirstOrDefault();
            if (!ordered.Any(x => x.NameHit) && (top.Game == null || !top.IsGame || top.Follow < 10000))
            {
                LogService.Info(L.Get("LogCat_Home"), L.Get("Svc_HhNoResultKw", keyword));
                return result;
            }
            result = ordered.Take(10).Select(x => x.Game).ToList();
            LogService.Info(L.Get("LogCat_Home"), L.Get("Svc_HhHit", result.Count, keyword));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Home"), L.Get("Svc_HhSearchError", ex.Message));
        }
        return result;
    }

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

    // 归一化：去标点空白后比对，中英文名含冒号空格等不影响命中
    private static string Norm(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // Steam 有效条目：platforms 含 steam，且 steam_appid 与 appid 一致（过滤 epic/xbox/手游的假 id）
    private static bool TryGetSteamAppId(JsonElement g, out int appId)
    {
        appId = 0;
        if (!g.TryGetProperty("steam_appid", out var sid) || sid.ValueKind != JsonValueKind.Number || !sid.TryGetInt32(out var steamId) || steamId <= 0)
            return false;
        if (!g.TryGetProperty("appid", out var aid) || aid.ValueKind != JsonValueKind.Number || !aid.TryGetInt32(out var id) || id != steamId)
            return false;
        if (!g.TryGetProperty("platforms", out var platforms) || platforms.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var p in platforms.EnumerateArray())
        {
            if (p.ValueKind == JsonValueKind.String &&
                string.Equals(p.GetString(), "steam", StringComparison.OrdinalIgnoreCase))
            {
                appId = steamId;
                return true;
            }
        }
        return false;
    }
}
