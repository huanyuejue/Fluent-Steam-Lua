using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ILuaFileManager
{
    Task<List<GameInfo>> ScanLuaFilesAsync();
    Task AddLuaFileAsync(string sourceFilePath);
    Task AddBinFileAsync(string sourceFilePath);
    /// <summary>放入 Steam depotcache，返回目标路径；Steam 路径缺失返回 null。</summary>
    Task<string?> AddManifestFileAsync(string sourceFilePath);
    Task RemoveAppIdsFromLuaAsync(int appId, IEnumerable<int> depotIds);
    Task DeleteLuaFileAsync(int appId);
    void StartWatching();
    void StopWatching();
    event EventHandler? FilesChanged;
    Task<GameInfo?> ParseLuaFileAsync(int appId);
    Task SetManifestPinAsync(int appId, bool pin, Dictionary<int, string>? manifestIds = null);
    Task DisableGameAsync(int appId);
    Task EnableGameAsync(int appId);
}
