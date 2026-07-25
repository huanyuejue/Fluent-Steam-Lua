using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ITrainerService
{
    Task<List<TrainerInfo>> GetHotTrainersAsync(int count = 10);
    Task<List<TrainerInfo>> GetNewReleasesAsync(int count = 10);
    Task<List<TrainerInfo>> SearchTrainersAsync(string query);
    Task<string?> GetDownloadUrlAsync(string pageUrl);
    Task<int> GetCheatCountAsync(string pageUrl);
}
