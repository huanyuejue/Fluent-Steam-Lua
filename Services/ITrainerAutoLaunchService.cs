using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public interface ITrainerAutoLaunchService : IDisposable
{
    void Start();
    void Stop();
    void ReloadBindings();
    event Action<string>? StatusChanged;
}
