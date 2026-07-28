using System.Text.Json.Serialization;

namespace SteamLuaManager.Models;

public class CheatOption
{
    public string KeyName { get; set; } = string.Empty;
    public string Modifiers { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayText => string.IsNullOrEmpty(Modifiers)
        ? $"{KeyName} - {Description}"
        : $"{Modifiers}+{KeyName} - {Description}";

    [JsonIgnore]
    public string FullKey => string.IsNullOrEmpty(Modifiers)
        ? KeyName
        : $"{Modifiers}+{KeyName}";
}
