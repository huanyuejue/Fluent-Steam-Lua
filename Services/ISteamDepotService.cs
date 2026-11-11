using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ISteamDepotService
{
    void UseDataSource(string source);
    Task<DepotQueryResult?> QueryAppAsync(int appId, CancellationToken ct = default, int timeoutSeconds = 30, int maxAttempts = 3);
    Task<string?> GenerateLuaAsync(int appId, CancellationToken ct = default, bool pinManifest = false, bool fetchNames = false);
    Task<string?> GenerateLuaWithDlcAsync(int appId, CancellationToken ct = default, bool pinManifest = false, IProgress<(int Done, int Total)>? dlcProgress = null, bool fetchNames = false);
    Task<DlcFetchResult> FetchDlcAsync(string luaPath, int dlcAppId, bool hasOwnDepot, CancellationToken ct = default, string? dlcName = null);
    /// <summary>批量查各 DLC 是否有独立仓库；单项失败返回 null（未知），调用方回退旧启发式。</summary>
    Task<Dictionary<int, DlcDepotStatus?>> GetDlcHasDepotsAsync(IEnumerable<int> dlcIds, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default);
    Task<bool> EnsureKeyFilesAsync(CancellationToken ct = default);
    Task<KeyFileUpdateResult> UpdateKeyFilesAsync(CancellationToken ct = default);
    Task EnsureAllSourcesAsync(CancellationToken ct = default);
    DateTime? GetLastUpdateTime(string source);

    /// <summary>
    /// 释放常驻的密钥字典缓存。密钥库有数万条，长期占着几十 MB；
    /// 常驻进程在闲置时可以主动放掉，下次入库会按文件时间戳重新加载。
    /// </summary>
    void ReleaseKeyCache();

    /// <summary>
    /// 判断某 app 是否在本地 token 表里有 access token。
    /// 仓储返回空时用它区分「这游戏本身没仓库」和「要 token 才能查」，别让用户摸不着头脑。
    /// </summary>
    Task<bool> HasAppTokenAsync(int appId, CancellationToken ct = default);

    /// <summary>EnsureAllSourcesAsync 完成后触发，通知 UI 刷新时间显示。</summary>
    event Action? AllSourcesUpdated;
}

public class DlcFetchResult
{
    public bool Success { get; set; }
    public bool NeedKey { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>DLC 仓库状态：查询成功才有值，AppName 为 steamcmd 名（可能为空）。</summary>
public sealed record DlcDepotStatus(bool HasDepots, string AppName);

public class KeyFileUpdateResult
{
    public bool Success { get; set; }
    public int DepotKeysOldCount { get; set; }
    public int DepotKeysNewCount { get; set; }
    public int TokenKeysOldCount { get; set; }
    public int TokenKeysNewCount { get; set; }
    public long DepotKeysSizeBytes { get; set; }
    public long TokenKeysSizeBytes { get; set; }
}
