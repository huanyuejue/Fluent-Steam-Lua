namespace SteamLuaManager.Services;

public enum SteamToolType
{
    None,
    OpenSteamTool,
    SteamTools
}

public interface ISteamPathService
{
    string? DetectSteamPath();
    string? GetLuaFolder();
    string? GetLuaConfigFile();
    bool SetConfiguredLuaPath(string path);
    bool ResetConfiguredLuaPath();
    bool GetFriendBroadcastEnabled();
    bool SetFriendBroadcastEnabled(bool enabled);
    void SetCustomPath(string path);
    string? GetCustomPath();
    SteamToolType DetectSteamToolType();
    List<string> GetAllLibraryPaths();
    string? FindAppManifest(int appId);
}
