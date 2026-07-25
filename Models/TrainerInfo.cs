using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamLuaManager.Models;

public partial class TrainerInfo : ObservableObject
{
    [ObservableProperty]
    private string _gameName = string.Empty;

    [ObservableProperty]
    private string _coverUrl = string.Empty;

    [ObservableProperty]
    private string _pageUrl = string.Empty;

    [ObservableProperty]
    private string _downloadUrl = string.Empty;

    [ObservableProperty]
    private string _fileSize = string.Empty;

    [ObservableProperty]
    private string _updateDate = string.Empty;

    [ObservableProperty]
    private string _version = string.Empty;

    [ObservableProperty]
    private bool _isDownloaded;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private int _cheatCount;
}
