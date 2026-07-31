using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ISteamAchievementService : IDisposable
{
    bool IsConnected { get; }
    string? LastError { get; }

    bool Connect(uint appId = 0);
    Task<List<AchievementGameInfo>> ParseOwnedGamesAsync();
    List<AchievementGameInfo> FilterSubscribed(List<AchievementGameInfo> games);
}
