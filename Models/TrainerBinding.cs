using System.IO;
using System.Text.Json.Serialization;

namespace SteamLuaManager.Models;

public class TrainerBinding
{
    public string GameName { get; set; } = string.Empty;
    public string GameExePath { get; set; } = string.Empty;
    public string TrainerFilePath { get; set; } = string.Empty;
    public string TrainerDisplayName { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    [JsonIgnore]
    public string DisplayInfo => IsEnabled ? $"✅ {GameName} → {TrainerDisplayName}" : $"❌ {GameName} → {TrainerDisplayName}";

    [JsonIgnore]
    public string TrainerFileName => Path.GetFileName(TrainerFilePath);

    public TrainerBinding Clone() => new()
    {
        GameName = GameName,
        GameExePath = GameExePath,
        TrainerFilePath = TrainerFilePath,
        TrainerDisplayName = TrainerDisplayName,
        IsEnabled = IsEnabled
    };
}
