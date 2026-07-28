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

    [JsonIgnore]
    public bool HasAutoKeys => AutoKeys.Count > 0;

    [JsonIgnore]
    public string AutoKeysSummary
    {
        get
        {
            if (AutoKeys.Count == 0) return string.Empty;
            var descs = AutoKeys.Select(k =>
            {
                var idx = k.LastIndexOf(" - ", StringComparison.Ordinal);
                return idx > 0 ? k[(idx + 3)..] : k;
            });
            return "已自动激活的功能：" + string.Join("、", descs);
        }
    }

    public List<string> AutoKeys { get; set; } = new();

    public TrainerBinding Clone() => new()
    {
        GameName = GameName,
        GameExePath = GameExePath,
        TrainerFilePath = TrainerFilePath,
        TrainerDisplayName = TrainerDisplayName,
        IsEnabled = IsEnabled,
        AutoKeys = new List<string>(AutoKeys)
    };
}
