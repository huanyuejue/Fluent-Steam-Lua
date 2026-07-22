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
    void SetCustomPath(string path);
    string? GetCustomPath();
    SteamToolType DetectSteamToolType();
    List<string> GetAllLibraryPaths();
    string? FindAppManifest(int appId);
}
