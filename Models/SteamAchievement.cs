using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

public partial class AchievementGameInfo : ObservableObject
{
    public uint AppId { get; init; }
    public string Name { get; init; } = "";
    public string CoverUrl { get; set; } = "";
}

public partial class AchievementDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string IconNormal { get; init; } = "";
    public string IconLocked { get; init; } = "";
    public bool IsHidden { get; init; }
    public int Permission { get; init; }
}

public partial class AchievementEntry : ObservableObject
{
    public AchievementDefinition Definition { get; init; } = new();

    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public bool IsProtected => (Definition.Permission & 3) != 0;
    public bool IsHidden => Definition.IsHidden;

    public string IconUrl
    {
        get
        {
            var hash = IsAchieved
                ? (string.IsNullOrEmpty(Definition.IconNormal) ? Definition.IconLocked : Definition.IconNormal)
                : (string.IsNullOrEmpty(Definition.IconLocked) ? Definition.IconNormal : Definition.IconLocked);
            return string.IsNullOrEmpty(hash) ? "" : $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{_appId}/{hash}";
        }
    }

    public string UnlockTimeText => UnlockTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";

    private readonly uint _appId;

    public AchievementEntry(uint appId, AchievementDefinition definition, bool isAchieved, DateTime? unlockTime)
    {
        _appId = appId;
        Definition = definition;
        _isAchieved = isAchieved;
        _unlockTime = unlockTime;
        OriginalAchieved = isAchieved;
    }

    public bool OriginalAchieved { get; internal set; }
    public bool IsModified => IsAchieved != OriginalAchieved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconUrl))]
    [NotifyPropertyChangedFor(nameof(UnlockTimeText))]
    private bool _isAchieved;

    [ObservableProperty]
    private DateTime? _unlockTime;
}
