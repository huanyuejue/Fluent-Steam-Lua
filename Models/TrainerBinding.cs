using System.IO;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using SteamLuaManager.Services;

namespace SteamLuaManager.Models;

public partial class TrainerBinding : ObservableObject
{
    public string GameName { get; set; } = string.Empty;
    public string GameExePath { get; set; } = string.Empty;
    public string TrainerFilePath { get; set; } = string.Empty;
    public string TrainerDisplayName { get; set; } = string.Empty;
    [ObservableProperty]
    private bool _isEnabled = true;

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
            return L.Get("Trainer_AutoKeysSummary", string.Join(L.Get("Common_ListSep"), descs));
        }
    }

    // 语言切换后由视图模型调用，让界面上的本地化文案重新取值
    public void RefreshLocalizedTexts() => OnPropertyChanged(nameof(AutoKeysSummary));

    public List<string> AutoKeys { get; set; } = new();
}
