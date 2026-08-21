namespace SteamLuaManager.Services;

/// <summary>
/// 通过 SteamKit2 + AppAccessToken 向 Steam 服务器查询完整 appinfo 的兜底服务。
/// </summary>
public interface ISteamAppInfoService
{
    /// <summary>用 access token 查询 appinfo 完整 depots 与 DLC 列表。token 无效或超时返回 null。</summary>
    Task<AppInfoQueryResult?> QueryFullAppInfoAsync(int appId, ulong accessToken, CancellationToken ct = default);
}

/// <summary>完整 appinfo 查询结果（depots + DLC）。</summary>
public class AppInfoQueryResult
{
    public string AppName { get; set; } = string.Empty;
    public List<int> DepotIds { get; set; } = new();
    public List<int> DlcAppIds { get; set; } = new();
}