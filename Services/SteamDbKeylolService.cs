using System.Net.Http;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

// Steam 游戏信息整合查询（steamdb.keylol.com）的中文搜索源：小黑盒搜索接口
// 加上登录校验后不再可用，入库前置的名字搜索改走这里。站点是服务端渲染，
// 结果表结构固定，按行解析即可；接口未公开，字段缺失按无结果处理。
public static class SteamDbKeylolService
{
    private const string SearchUrl = "https://steamdb.keylol.com/search/{0}";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    // 结果行：class / id / type / 名称链接 / 价格，名称与价格可能为空
    private static readonly Regex RowRegex = new(
        @"<tr>\s*<td>(?<kind>[^<]*)</td>\s*<td>(?<id>\d+)</td>\s*<td>(?<type>[^<]*)</td>\s*" +
        @"<td><a[^>]*>(?<name>[^<]+)</a></td>\s*<td>(?<price>[^<]*)</td>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // 名字搜索：只收 app 类型条目，按关键词归一化后过滤，避免站点模糊匹配带出无关结果
    public static async Task<List<HeiHeGame>> SearchByNameAsync(string keyword, CancellationToken ct = default)
    {
        var result = new List<HeiHeGame>();
        var normKeyword = Norm(keyword);
        if (string.IsNullOrEmpty(normKeyword))
            return result;

        string html;
        try
        {
            using var resp = await _http.GetAsync(
                string.Format(SearchUrl, Uri.EscapeDataString(keyword)), ct);
            if (!resp.IsSuccessStatusCode)
            {
                LogService.Warn(L.Get("LogCat_Home"), L.Get("Svc_KdSearchHttpFail", (int)resp.StatusCode));
                return result;
            }
            html = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Home"), L.Get("Svc_KdSearchFail", ex.Message));
            return result;
        }

        var seen = new HashSet<int>();
        foreach (Match m in RowRegex.Matches(html))
        {
            if (!int.TryParse(m.Groups["id"].Value, out var appId) || appId <= 0)
                continue;
            // 只认 app：dlc/demo/package 之类的条目入库后会指向错误的仓库
            var type = m.Groups["type"].Value.Trim();
            if (!type.Equals("app", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = System.Net.WebUtility.HtmlDecode(m.Groups["name"].Value).Trim();
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(appId))
                continue;

            // 结果表只有名称与价格，发布日期留空（界面不展示该字段时不阻塞入库）
            result.Add(new HeiHeGame(appId, name, "", [], "", type));
        }

        // 站点已按相关性排好，只把与关键词关系更近的提上来：名字以关键词开头的
        // 排最前，其次是含关键词的，其余保持站点原序。不再按名字长度排——那样
        // 「闪避刺客」会压过「刺客信条：黑旗」，明显不如原序合理
        var picked = result
            .Select((g, i) => (Game: g, Index: i, Rank: NameRank(g.Name, normKeyword)))
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Index)
            .Take(10)
            .Select(x => x.Game)
            .ToList();

        // Steam 的旧封面地址（/steam/apps/{id}/header.jpg）对新游戏已 404，真实地址带
        // 内容哈希子目录。小黑盒详情返回的 image 已处理新老格式，用它补封面；
        // 查不到就留空，让界面回退到占位图，不阻塞搜索
        await FillCoversAsync(picked, ct);
        return picked;
    }

    // 并发补封面：小黑盒详情按 AppID 查，返回的 image 是新老格式通用的完整地址。
    // 并发写 List 的索引器不安全，先落到同长度的数组再整体回填；
    // 单个失败只丢这一条的封面，不影响整批
    private static async Task FillCoversAsync(List<HeiHeGame> games, CancellationToken ct)
    {
        if (games.Count == 0) return;

        var covers = new string?[games.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, games.Count), options, async (i, token) =>
        {
            try
            {
                var (_, cover, _) = await XiaoHeiHeService.GetGameDetailAsync(games[i].AppId, token)
                    .ConfigureAwait(false);
                covers[i] = cover;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // 详情接口拿不到就保留空封面，界面显示占位图
            }
        }).ConfigureAwait(false);

        for (var i = 0; i < games.Count; i++)
            if (!string.IsNullOrWhiteSpace(covers[i]))
                games[i] = games[i] with { CoverUrl = covers[i]!, CoverCandidates = [covers[i]!] };
    }

    // 名字与关键词的关系分级：0 前缀命中，1 包含，2 无关。分级内保持站点原序
    private static int NameRank(string name, string normKeyword)
    {
        var norm = Norm(name);
        if (norm.StartsWith(normKeyword, StringComparison.Ordinal)) return 0;
        return norm.Contains(normKeyword, StringComparison.Ordinal) ? 1 : 2;
    }

    // 关键词归一化：去掉空白与常见标点，兼顾中英文混排的名字匹配
    private static string Norm(string s) =>
        Regex.Replace(s ?? "", @"[\s\p{P}\p{S}]", "").ToLowerInvariant();
}
