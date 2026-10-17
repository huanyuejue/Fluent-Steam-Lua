using SteamLuaManager.Services;

namespace SteamLuaManager;

// C# 里取文案的快捷入口：L.Get("Nav_Home") / L.Get("Ruku_FoundCount", count)。
// 日志、状态栏、弹窗走的都是它，切换语言后新写出的内容自动用新语言。
public static class L
{
    public static string Get(string key) => LocalizationService.Instance.GetString(key);

    public static string Get(string key, params object?[] args) =>
        LocalizationService.Instance.GetString(key, args);
}
