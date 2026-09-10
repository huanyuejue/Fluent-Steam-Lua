using SteamKit2;
using SteamKit2.Authentication;

namespace SteamLuaManager.Services;

public record LocalSteamAccount(string SteamId, string AccountName, string PersonaName, long Timestamp);
public record SteamLoginResult(bool Success, string Message);
public record ManifestDownloadResult(bool Success, string? FilePath, string Message);

public interface ISteamAccountService : IDisposable
{
    /// <summary>本机 Steam 登录过的账号（按最近登录倒序）。</summary>
    IReadOnlyList<LocalSteamAccount> ListLocalUsers();

    /// <summary>本机头像缓存路径（config/avatarcache/{steamId}.png），无则返回 null。</summary>
    string? GetAvatarPath(string steamId);

    bool IsLoggedOn { get; }
    string? CurrentAccountName { get; }

    /// <summary>缓存的账号名（设置里的已存凭证，无则为 null）。</summary>
    string? SavedAccountName { get; }

    /// <summary>是否有缓存登录凭证（只看字段，不解密不联网）。</summary>
    bool HasCachedToken();

    /// <summary>用缓存凭证静默登录（无弹窗，供点提取时兑现）。失败返回原因。</summary>
    Task<SteamLoginResult> TryRestoreSessionAsync(CancellationToken ct = default);

    /// <summary>登录态变化（登录成功/退出/断开）时触发，UI 用它刷新状态显示。</summary>
    event Action? SessionChanged;

    /// <summary>用本机记住的凭证直接登录，无密码无 2FA。</summary>
    Task<SteamLoginResult> LoginWithLocalAsync(string accountName, CancellationToken ct = default);

    /// <summary>用 refresh token 登录（令牌过期会自动续期并回写）。</summary>
    Task<SteamLoginResult> LoginWithRefreshTokenAsync(string username, string refreshToken, CancellationToken ct = default);

    /// <summary>账号密码登录，2FA 由 authenticator 回填（手机确认请直接拒绝）。</summary>
    Task<SteamLoginResult> LoginWithCredentialsAsync(string username, string password, IAuthenticator authenticator, CancellationToken ct = default);

    /// <summary>批量取 depot 解密密钥，拿不到的直接跳过（只返回成功的）。</summary>
    Task<Dictionary<uint, byte[]>> GetDepotKeysAsync(IEnumerable<(uint DepotId, uint AppId)> targets, CancellationToken ct = default);

    /// <summary>从 CDN 只下载单个 manifest 文件并存盘，不碰游戏数据。</summary>
    Task<ManifestDownloadResult> DownloadManifestAsync(uint appId, uint depotId, ulong manifestGid, byte[]? depotKey, string destDir, CancellationToken ct = default);

    void LogOff();
}
