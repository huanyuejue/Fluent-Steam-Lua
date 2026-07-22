using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ILuaFileManager
{
    Task<List<GameInfo>> ScanLuaFilesAsync();
    Task AddLuaFileAsync(string sourceFilePath);
    Task DeleteLuaFileAsync(int appId);
    void StartWatching();
    void StopWatching();
    event EventHandler? FilesChanged;
    Task<GameInfo?> ParseLuaFileAsync(int appId);
    Task SetManifestPinAsync(int appId, bool pin, Dictionary<int, string>? manifestIds = null);
}
