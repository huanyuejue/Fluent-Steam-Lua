using SteamLuaManager.Services;

namespace SteamLuaManager.Models;

public sealed class TrainerMetadata
{
    public int FormatVersion { get; set; } = 2;
    public long SourceLength { get; set; }
    public long SourceWriteTimeUtcTicks { get; set; }
    public string OriginalGameName { get; set; } = string.Empty;
    public string EnglishGameName { get; set; } = string.Empty;
    public string ChineseGameName { get; set; } = string.Empty;
    public List<CheatOption> Options { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayGameName => LocalizationService.Instance.CurrentLanguage == "zh"
        ? FirstNonempty(ChineseGameName, OriginalGameName, EnglishGameName)
        : FirstNonempty(EnglishGameName, TrainerExeParser.HasChinese(OriginalGameName) ? "" : OriginalGameName);

    private static string FirstNonempty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
