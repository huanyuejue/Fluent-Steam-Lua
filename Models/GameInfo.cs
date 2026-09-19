using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

public partial class GameInfo : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveSubtitle))]
    private int _appId;

    [ObservableProperty]
    private string _luaFilePath = string.Empty;

    [ObservableProperty]
    private string _gameName = string.Empty;

    [ObservableProperty]
    private string _coverImagePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveSubtitle))]
    private DateTime? _lastSaveTime;

    [ObservableProperty]
    private DateTime _luaFileTime;

    // 云存档行副标题：无存档时间时只显示 AppID
    public string SaveSubtitle => LastSaveTime.HasValue
        ? $"AppID: {AppId} • 上次存档: {LastSaveTime:yyyy-MM-dd HH:mm}"
        : $"AppID: {AppId}";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _token = string.Empty;

    [ObservableProperty]
    private bool _isManifestPinned;

    [ObservableProperty]
    private bool _isDisabled;

    [ObservableProperty]
    private int _manifestSourceIndex;

    [ObservableProperty]
    private bool _isSelected;

    public ObservableCollection<DepotInfo> Depots { get; set; } = new();

    /// <summary>裸 addappid(id) 行的 id（无密钥的纯 DLC 等），解析时收集，不影响 Depots。</summary>
    public List<int> BareAppIds { get; set; } = new();
}

