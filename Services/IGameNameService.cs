namespace SteamLuaManager.Services;

public interface IGameNameService
{
    Task<string> GetChineseNameAsync(string gameName, bool forceRefresh = false);
}
