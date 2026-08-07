using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

public partial class DlcInfo : ObservableObject
{
    [ObservableProperty]
    private int _appId;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _coverImagePath = string.Empty;

    [ObservableProperty]
    private bool _isImported;

    [ObservableProperty]
    private bool _hasDepot;

    [ObservableProperty]
    private bool _isFetching;

    [ObservableProperty]
    private string _fetchMessage = string.Empty;
}
