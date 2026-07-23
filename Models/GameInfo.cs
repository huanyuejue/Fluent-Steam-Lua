using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

public partial class GameInfo : ObservableObject
{
    [ObservableProperty]
    private int _appId;

    [ObservableProperty]
    private string _luaFilePath = string.Empty;

    [ObservableProperty]
    private string _gameName = string.Empty;

    [ObservableProperty]
    private string _coverImagePath = string.Empty;

    [ObservableProperty]
    private DateTime _luaFileTime;

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
}

