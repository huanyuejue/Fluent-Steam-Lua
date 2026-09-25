using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ISteamApiService
{
    void PopulateFromCache(List<GameInfo> games);
    Task RefreshGameInfoAsync(List<GameInfo> games, CancellationToken cancellationToken = default, bool fetchCover = true);
    Task RefreshSingleGameAsync(GameInfo game, CancellationToken cancellationToken = default);
    int SelectedCdnIndex { get; }
    void UpdateCdnPreference(int selectedIndex);
    List<string> GetCoverUrls(int appId);
    Task<string?> ResolveHeaderUrlAsync(int appId, CancellationToken cancellationToken = default);
    // 商店下架游戏 appdetails 查无数据时，用备用源（SteamSpy → 社区页）解析游戏名
    Task<string?> GetFallbackGameNameAsync(int appId, CancellationToken cancellationToken = default);
    Task<List<(string Name, long LatencyMs, bool IsSuccess)>> TestCdnSpeedAsync(
        IProgress<(string Name, long LatencyMs, bool IsSuccess)>? progress = null);
    Task<bool?> IsComingSoonAsync(int appId, CancellationToken cancellationToken = default);
    event Action<int>? CdnAutoSwitched;
}