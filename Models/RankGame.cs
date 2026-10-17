using SteamLuaManager.Services;

namespace SteamLuaManager.Models;

// 小黑盒热门榜单项：集合顺序即榜单名次
public sealed record RankGame(int AppId, string Name, string CoverUrl, List<string> CoverCandidates, long Followers)
{
    // 中文环境关注数过万时折万展示，其余环境按原值展示
    public string FollowersText => Followers >= 10000 && LocalizationService.Instance.CurrentLanguage == "zh"
        ? L.Get("Rank_FollowersWan", (Followers / 10000.0).ToString("F1"))
        : L.Get("Rank_Followers", Followers);

    // 卡片副标题：只显示关注数
    public string DisplaySubtitle => FollowersText;
}
