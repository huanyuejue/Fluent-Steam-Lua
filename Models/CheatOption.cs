using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SteamLuaManager.Services;

namespace SteamLuaManager.Models;

public class CheatOption : INotifyPropertyChanged
{
    public string KeyName { get; set; } = string.Empty;
    public string Modifiers { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string EnglishDescription { get; set; } = string.Empty;
    public string ChineseDescription { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayDescription => LocalizationService.Instance.CurrentLanguage == "zh"
        ? FirstNonempty(ChineseDescription, Description, EnglishDescription)
        : !string.IsNullOrWhiteSpace(EnglishDescription) ? EnglishDescription
        : !TrainerExeParser.HasChinese(Description) && !string.IsNullOrWhiteSpace(Description) ? Description
        : L.Get("Trainer_OptionEnglishUnavailable");

    [JsonIgnore]
    public string SourceDescription => FirstNonempty(Description, ChineseDescription, EnglishDescription);

    [JsonIgnore]
    public bool SupportsAutomaticKey => !Modifiers.Contains('+');

    [JsonIgnore]
    public string DisplayText => string.IsNullOrEmpty(Modifiers)
        ? $"{KeyName} - {DisplayDescription}"
        : $"{Modifiers}+{KeyName} - {DisplayDescription}";

    [JsonIgnore]
    public string FullKey => string.IsNullOrEmpty(Modifiers)
        ? KeyName
        : $"{Modifiers}+{KeyName}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshLocalizedTexts()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayDescription)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
    }

    // Legacy bindings store a description after the hotkey. Its text is metadata.
    public static string HotkeyFromStoredValue(string value)
    {
        var hotkey = Regex.Match(value.Trim(), @"^(?:(?:Ctrl|Alt|Shift)\+)*(?:Num\s*[\d+*./-]+|F\s*\d+)", RegexOptions.IgnoreCase);
        if (hotkey.Success) return Regex.Replace(hotkey.Value, @"\bNum\s*", "Num ", RegexOptions.IgnoreCase);
        var separator = value.IndexOf(" - ", StringComparison.Ordinal);
        return separator >= 0 ? value[..separator].Trim() : value.Trim();
    }

    private static string FirstNonempty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
