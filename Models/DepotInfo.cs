namespace SteamLuaManager.Models;

public class DepotInfo
{
    public int DepotId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string ManifestId { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
}
