using System.IO;
using System.Text.Json;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class AppSettings
{
    public string SteamPath { get; set; } = string.Empty;
    public bool AutoRefreshEnabled { get; set; } = true;
    public int SelectedCdnIndex { get; set; }
    public string SelectedViewMode { get; set; } = "卡片";
    public string AchievementViewMode { get; set; } = "卡片";
    public string SelectedBackdrop { get; set; } = "Acrylic10";
    public string DownloadMode { get; set; } = "DepotKey";
    public string KeyFolderPath { get; set; } = string.Empty;
    public bool IsFabVisible { get; set; } = true;
    public bool IsCardRefreshVisible { get; set; } = true;
    public string SelectedTheme { get; set; } = "System";
    public bool AutoCheckUpdateEnabled { get; set; } = true;
public bool ShowTrainerSections { get; set; } = true;
    public bool ShowCopyLogButton { get; set; }
    public bool EnableLogging { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool AutoRefreshKeyCache { get; set; } = true;
    public List<TrainerBinding> TrainerBindings { get; set; } = new();
}

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
    event Action<AppSettings>? SettingsChanged;
}

public class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsFilePath;
    private readonly object _saveLock = new();

    public SettingsService()
    {
        _settingsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                if (string.IsNullOrWhiteSpace(settings.DownloadMode))
                    settings.DownloadMode = "DepotKey";
                return settings;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("设置", $"读取配置失败，已使用默认配置: {ex.Message}");
        }
        return new AppSettings();
    }

    public event Action<AppSettings>? SettingsChanged;

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        lock (_saveLock)
        {
            try
            {
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                LogService.Error("设置", $"保存配置失败: {ex.Message}");
                return;
            }
        }
        SettingsChanged?.Invoke(settings);
    }
}
