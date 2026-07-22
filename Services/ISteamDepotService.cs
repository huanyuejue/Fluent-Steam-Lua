using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ISteamDepotService
{
    void UseDataSource(string source);
    Task<DepotQueryResult?> QueryAppAsync(int appId, CancellationToken ct = default);
    Task<string?> GenerateLuaAsync(int appId, string? keyFolderPath = null, CancellationToken ct = default);
    Task<string?> GenerateLuaWithDlcAsync(int appId, string? keyFolderPath = null, CancellationToken ct = default);
    Task<bool> EnsureKeyFilesAsync(CancellationToken ct = default);
    Task<KeyFileUpdateResult> UpdateKeyFilesAsync(CancellationToken ct = default);
    Task EnsureAllSourcesAsync(CancellationToken ct = default);
}

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
