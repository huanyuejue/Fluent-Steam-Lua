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

public record FetchedDepot(int DepotId, string Gid, RepoDepotKind Kind, string PicsGid, string? PlacedPath)
{
    // 已是最新版的不固定：pin 会把 acf 版本写成最新，导致 Steam 判定无需更新；
    // 只有仓库版落后于 Steam 最新（或 Steam 最新未知）时才固定到取到的版本。
    public bool NeedsPin =>
        Kind != RepoDepotKind.Latest
        || string.IsNullOrEmpty(PicsGid)
        || CompareGid(Gid, PicsGid) != 0;

    // gid 为十进制大整数：先比长度再比字典序
    internal static int CompareGid(string a, string b)
    {
        var x = a.TrimStart('0');
        var y = b.TrimStart('0');
        if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
        return string.Compare(x, y, StringComparison.Ordinal);
    }
}

public record ManifestRepoFetchResult(
    bool Success,
    string? Error,
    List<FetchedDepot> Fetched,
    List<int> MissingMainDepots,
    List<int> MissingDlcDepots,
    List<int> MissingUnknownDepots,
    List<int> UnknownDepots,
    bool PossiblyRateLimited,
    string? DepotCacheDir)
{
    public Dictionary<int, string> GetPinMap() =>
        Fetched.Where(f => f.NeedsPin).ToDictionary(f => f.DepotId, f => f.Gid);
}

public interface ISteamManifestRepoService
{
    /// <summary>
    /// 从 SteamManifestCache_Pro 仓库为游戏拉取各 depot 的 manifest。
    ///  per-depot 策略：PICS 最新 gid 的 raw 直链 → 分支文件 → Tag 旧版。
    ///  成功取到的文件直接放入 Steam depotcache，返回需固定的 gid 映射（已是最新版的不固定，
    ///  避免 acf 被写成最新导致 Steam 判定无需更新）。
    /// </summary>
    Task<ManifestRepoFetchResult> FetchManifestsAsync(
        int appId,
        IReadOnlyList<int> luaAppIds,
        IProgress<(int done, int total, string text)>? progress,
        CancellationToken ct = default);

    /// <summary>镜像源可用性测速：直连 + 各镜像并发下载同一探测文件，按完成顺序回报。</summary>
    Task<List<(string Name, long LatencyMs, bool IsSuccess)>> TestMirrorSpeedAsync(
        IProgress<(string Name, long LatencyMs, bool IsSuccess)>? progress = null);
}
