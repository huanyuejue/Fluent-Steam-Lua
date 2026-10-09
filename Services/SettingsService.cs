using System.IO;
using System.Text.Json;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class AppSettings
{
    public string SteamPath { get; set; } = string.Empty;
    public bool AutoRefreshEnabled { get; set; } = true;
    // 封面节点默认 Heybox 国内源；已存档的老用户保持原选择，不静默迁移
    public int SelectedCdnIndex { get; set; } = CdnEndpoint.Defaults.FindIndex(c => c.IsApiLookup);
    // 封面节点序号迁移标记：Heybox 置顶前存档的旧序号需重映射；新对象默认已迁移
    public bool CdnOrderMigrated { get; set; } = true;
    public string SelectedViewMode { get; set; } = "卡片";
    public string AchievementViewMode { get; set; } = "卡片";
    public string SelectedBackdrop { get; set; } = "Acrylic10";
    public string DownloadMode { get; set; } = "DepotKey";
    public string KeyFolderPath { get; set; } = string.Empty;
    public bool IsFabVisible { get; set; } = true;
    public bool IsCardRefreshVisible { get; set; } = true;
    public string SelectedTheme { get; set; } = "System";
    public bool AutoCheckUpdateEnabled { get; set; } = true;
    public bool AutoCheckKernelUpdateEnabled { get; set; } = true;
public bool ShowTrainerSections { get; set; } = true;
    public bool ShowCopyLogButton { get; set; }
    public bool EnableLogging { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool AutoRefreshKeyCache { get; set; } = true;
    public bool CloudBackupConfirmed { get; set; }
    public bool AutoFetchCovers { get; set; } = true;
    // 大库封面询问标记：首次达到阈值时问过一次后不再打扰
    public bool CoverFetchAsked { get; set; }
    /// <summary>文件下载首选镜像源主机（direct = 直连优先），作用于内核包下载（清单获取已直连）。</summary>
    public string ManifestMirror { get; set; } = GitHubMirror.DirectKey;
    public List<TrainerBinding> TrainerBindings { get; set; } = new();
    public string SavedAccountName { get; set; } = string.Empty;
    public string EncryptedManifestHubKey { get; set; } = string.Empty;
    public string ManifestHubKeyTime { get; set; } = string.Empty;
    public string EncryptedRefreshToken { get; set; } = string.Empty;
    public string EncryptedGuardData { get; set; } = string.Empty;
    // 免启动破解用的 Steam Web API Key（选填）：补全模拟器成就/库存信息，不填则跳过
    public string CrackWebApiKey { get; set; } = string.Empty;
    // 手柄适配：开关 + 灵敏度倍率（影响导航重复间隔，默认 1.0）
    public bool EnableGamepad { get; set; }
    public double GamepadSensitivity { get; set; } = 1.0;
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
                using var doc = JsonDocument.Parse(json);
                var settings = doc.RootElement.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
                if (string.IsNullOrWhiteSpace(settings.DownloadMode))
                    settings.DownloadMode = "DepotKey";
                // 本地缓存仓库 V2 已废弃，老配置迁移到唯一保留的本地缓存仓库
                else if (settings.DownloadMode == "DepotKey2")
                    settings.DownloadMode = "DepotKey";
                // 封面节点 Heybox 置顶：旧序号 1..4 → +1，旧 5（Heybox）→ 1，0 不变；
                // 史前存档（无该键）当时 effective 值为旧默认 0，保持 0；幂等，下次 Save 落盘
                if (!settings.CdnOrderMigrated)
                {
                    if (!doc.RootElement.TryGetProperty(nameof(AppSettings.SelectedCdnIndex), out _))
                        settings.SelectedCdnIndex = 0;
                    else
                        settings.SelectedCdnIndex = settings.SelectedCdnIndex switch
                        {
                            >= 1 and <= 4 => settings.SelectedCdnIndex + 1,
                            5 => 1,
                            _ => settings.SelectedCdnIndex,
                        };
                    settings.CdnOrderMigrated = true;
                }
                if (string.IsNullOrWhiteSpace(settings.ManifestMirror))
                    settings.ManifestMirror = GitHubMirror.DirectKey;
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
