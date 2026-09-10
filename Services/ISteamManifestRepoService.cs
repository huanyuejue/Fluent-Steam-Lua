namespace SteamLuaManager.Services;

public enum RepoDepotKind
{
    /// <summary>仓库文件与 Steam 最新 gid 一致。</summary>
    Latest,
    /// <summary>仓库有该 depot 文件，但 gid 落后于 Steam 最新。</summary>
    RepoStale,
    /// <summary>分支无文件，从 Tag 拿到旧版本。</summary>
    OldVersion,
}

public record FetchedDepot(int DepotId, string Gid, RepoDepotKind Kind, string PicsGid, string? PlacedPath);

public record ManifestRepoFetchResult(
    bool Success,
    string? Error,
    List<FetchedDepot> Fetched,
    List<int> MissingMainDepots,
    List<int> MissingDlcDepots,
    List<int> MissingUnknownDepots,
    bool PossiblyRateLimited,
    string? DepotCacheDir)
{
    public Dictionary<int, string> GetPinMap() =>
        Fetched.ToDictionary(f => f.DepotId, f => f.Gid);
}

public interface ISteamManifestRepoService
{
    /// <summary>
    /// 从 SteamManifestCache_Pro 仓库为游戏拉取各 depot 的 manifest。
    ///  per-depot 策略：PICS 最新 gid 的 raw 直链 → 分支文件 → Tag 旧版。
    ///  成功取到的文件直接放入 Steam depotcache，返回 gid 映射供调用方 setmanifest 固定。
    /// </summary>
    Task<ManifestRepoFetchResult> FetchManifestsAsync(
        int appId,
        IReadOnlyList<int> luaAppIds,
        IProgress<(int done, int total, string text)>? progress,
        CancellationToken ct = default);
}
