using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SteamLuaManager.Models;

public class DownloadedTrainerItem : INotifyPropertyChanged
{
    private string _displayName = string.Empty;

    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
