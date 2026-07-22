namespace SteamLuaManager.Services;

public interface ISteamManifestService
{
    Dictionary<int, string> ParseMountedDepots(string acfPath);
    Task<string?> FetchLatestManifestIdAsync(int appId, int depotId);
}
