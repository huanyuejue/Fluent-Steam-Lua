namespace SteamLuaManager.Models;

public class DepotKeyInfo
{
    public int DepotId { get; set; }
    public string Key { get; set; } = string.Empty;
    public bool IsMatched { get; set; }
}

public class DepotQueryResult
{
    public int AppId { get; set; }
    public string AppName { get; set; } = string.Empty;
    public List<DepotKeyInfo> GameDepots { get; set; } = new();
    public List<int> DlcAppIds { get; set; } = new();
    public string? AppToken { get; set; }
}
