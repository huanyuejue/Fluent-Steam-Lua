using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Win32;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISteamPathService _steamPathService;
    private readonly ILuaFileManager _luaFileManager;
    private readonly ISettingsService _settingsService;
    private readonly ISteamApiService _steamApiService;
    private readonly ISteamManifestRepoService _manifestRepoService;
    private readonly IDialogService _dialogService;
    private readonly IHttpClientProvider _httpClientProvider;
    private AppSettings _settings;

    [ObservableProperty]
    private string _steamPath = string.Empty;

    [ObservableProperty]
    private string _luaFolderPath = string.Empty;

    [ObservableProperty]
    private bool _isAutoRefreshEnabled = true;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCdnOption))]
    private int _selectedCdnIndex;

    [ObservableProperty]
    private bool _isSpeedTesting;

    [ObservableProperty]
    private bool _isMirrorSpeedTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MirrorSpeedTestProgressText))]
    private int _mirrorSpeedTestProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MirrorSpeedTestProgressText))]
    private int _mirrorSpeedTestTotal;

    public string MirrorSpeedTestProgressText => $"{MirrorSpeedTestProgress}/{MirrorSpeedTestTotal}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestProgressText))]
    private int _speedTestProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedTestProgressText))]
    private int _speedTestTotal;

    public string SpeedTestProgressText => $"{SpeedTestProgress}/{SpeedTestTotal}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedBackdropOption))]
    private string _selectedBackdrop = "Acrylic10";

    [ObservableProperty]
    private string _language = "zh";

    public List<LanguageOption> LanguageOptions { get; } =
        LocalizationService.SupportedLanguages.ToList();

    // 选项列表永不重建：显示名每次按当前语言计算，切语言只通知刷新文本，
    // 列表实例与选择器选中项因此始终保持稳定，不依赖任何重建后的复位
    public sealed class BackdropOption : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly string _displayKey;
        public string Value { get; }

        public BackdropOption(string displayKey, string value)
        {
            _displayKey = displayKey;
            Value = value;
        }

        public string Display => L.Get(_displayKey);

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void RefreshDisplay() =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Display)));
    }

    public sealed class ThemeOption : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly string _displayKey;
        public string Value { get; }

        public ThemeOption(string displayKey, string value)
        {
            _displayKey = displayKey;
            Value = value;
        }

        public string Display => L.Get(_displayKey);

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void RefreshDisplay() =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Display)));
    }

    // CDN 下拉顺序/数量必须与 CdnEndpoint.Defaults 严格一致
    public sealed class CdnDisplayOption : System.ComponentModel.INotifyPropertyChanged
    {
        public CdnEndpoint Endpoint { get; }

        public CdnDisplayOption(CdnEndpoint endpoint) => Endpoint = endpoint;

        public string Display => L.Get(CdnEndpoint.GetDisplayKey(Endpoint.Name));

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void RefreshDisplay() =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Display)));
    }

    public List<BackdropOption> BackdropOptions { get; } = new()
    {
        new("Settings_Backdrop_Acrylic", "Acrylic10"),
        new("Settings_Backdrop_Mica", "Mica"),
        new("Settings_Backdrop_None", "None"),
    };

    public List<ThemeOption> ThemeOptions { get; } = new()
    {
        new("Settings_Theme_System", "System"),
        new("Settings_Theme_Dark", "Dark"),
        new("Settings_Theme_Light", "Light"),
    };

    public List<CdnDisplayOption> CdnDisplayOptions { get; } =
        CdnEndpoint.Defaults.Select(e => new CdnDisplayOption(e)).ToList();

    // 下拉用对象引用绑定：列表实例稳定后选择器选中项不会因列表变化被清空
    public ThemeOption? SelectedThemeOption
    {
        get => ThemeOptions.FirstOrDefault(o => o.Value == SelectedTheme);
        set { if (value != null) SelectedTheme = value.Value; }
    }

    public BackdropOption? SelectedBackdropOption
    {
        get => BackdropOptions.FirstOrDefault(o => o.Value == SelectedBackdrop);
        set { if (value != null) SelectedBackdrop = value.Value; }
    }

    public CdnDisplayOption? SelectedCdnOption
    {
        get => SelectedCdnIndex >= 0 && SelectedCdnIndex < CdnDisplayOptions.Count
            ? CdnDisplayOptions[SelectedCdnIndex]
            : null;
        set { if (value != null) SelectedCdnIndex = CdnDisplayOptions.IndexOf(value); }
    }

    // 语言切换时重建选项文案并刷新测速行（计算属性 StatusText 需重估）
    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        RefreshLocalizedOptions();

    private void RefreshLocalizedOptions()
    {
        // 选项列表保持同一实例，只让每项按当前语言刷新显示名；列表不重建，选择器选中项不受影响
        foreach (var o in ThemeOptions) o.RefreshDisplay();
        foreach (var o in BackdropOptions) o.RefreshDisplay();
        foreach (var o in CdnDisplayOptions) o.RefreshDisplay();
        RefreshCollectionView(SpeedTestResults);
        RefreshCollectionView(MirrorSpeedTestResults);
        RefreshCollectionView(ManifestTestResults);
    }

    private static void RefreshCollectionView(System.Collections.IEnumerable collection)
    {
        try
        {
            System.Windows.Data.CollectionViewSource.GetDefaultView(collection)?.Refresh();
        }
        catch { }
    }

    public ObservableCollection<SpeedTestItem> SpeedTestResults { get; } = new();

    public ObservableCollection<SpeedTestItem> MirrorSpeedTestResults { get; } = new();

    public SettingsViewModel(ISteamPathService steamPathService, ILuaFileManager luaFileManager,
        ISettingsService settingsService, ISteamApiService steamApiService,
        ISteamManifestRepoService manifestRepoService, IDialogService dialogService,
        IHttpClientProvider httpClientProvider)
    {
        _steamPathService = steamPathService;
        _luaFileManager = luaFileManager;
        _settingsService = settingsService;
        _steamApiService = steamApiService;
        _manifestRepoService = manifestRepoService;
        _dialogService = dialogService;
        _httpClientProvider = httpClientProvider;
        _settings = settingsService.Load();

        SteamPath = _settings.SteamPath;
        IsAutoRefreshEnabled = _settings.AutoRefreshEnabled;
        IsFabVisible = _settings.IsFabVisible;
        IsCardRefreshVisible = _settings.IsCardRefreshVisible;
        ManifestMirror = GitHubMirror.NormalizePreferredHost(_settings.ManifestMirror);
        AutoFetchCovers = _settings.AutoFetchCovers;
        AutoCheckUpdateEnabled = _settings.AutoCheckUpdateEnabled;
        AutoCheckKernelUpdateEnabled = _settings.AutoCheckKernelUpdateEnabled;
        AutoRefreshKeyCache = _settings.AutoRefreshKeyCache;
        IsShowTrainerSections = _settings.ShowTrainerSections;
        ShowRanking = _settings.ShowRanking;
        IsShowCopyLogButton = _settings.ShowCopyLogButton;
        EnableLogging = _settings.EnableLogging;
        MinimizeToTray = _settings.MinimizeToTray;

        // 历史存档可能残留空值或旧码值（曾被下拉重建时的空回填污染），统一归一化到合法项
        SelectedTheme = ThemeOptions.Any(o => o.Value == _settings.SelectedTheme) ? _settings.SelectedTheme : "System";
        SelectedCdnIndex = Math.Clamp(_settings.SelectedCdnIndex, 0, CdnEndpoint.Defaults.Count - 1);
        _selectedBackdrop = BackdropOptions.Any(o => o.Value == _settings.SelectedBackdrop) ? _settings.SelectedBackdrop : "Acrylic10";
        _settings.SelectedTheme = SelectedTheme;
        _settings.SelectedCdnIndex = SelectedCdnIndex;
        _settings.SelectedBackdrop = _selectedBackdrop;
        _language = LocalizationService.Normalize(_settings.Language);
        DownloadMode = _settings.DownloadMode;
        KeyFolderPath = _settings.KeyFolderPath;

        _steamApiService.CdnAutoSwitched += OnCdnAutoSwitched;
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;

        if (string.IsNullOrEmpty(SteamPath))
        {
            var detectedPath = steamPathService.DetectSteamPath();
            SteamPath = detectedPath ?? L.Get("Settings_SteamNotFound");
        }

        LuaFolderPath = steamPathService.GetLuaFolder() ?? L.Get("Settings_NotConfigured");

        RefreshFriendBroadcastToggle();
        RefreshManifestSource();
        RefreshManifestFailoverToggle();
    }

    // 显示名按下标取：CdnDisplayOptions 与 Defaults 同序，语言切换重建后依然对齐
    private string CdnDisplayLabel(int index) =>
        index >= 0 && index < CdnDisplayOptions.Count
            ? CdnDisplayOptions[index].Display
            : CdnEndpoint.Defaults[index].Name;

    private void OnCdnAutoSwitched(int newIndex)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (newIndex >= 0 && newIndex < CdnEndpoint.Defaults.Count)
            {
                SelectedCdnIndex = newIndex;
                StatusMessage = L.Get("Settings_CdnAutoSwitched", CdnDisplayLabel(newIndex));
                LogService.Warn(L.Get("LogCat_Settings"), StatusMessage);
            }
        });
    }

    private DispatcherTimer? _statusTimer;

    partial void OnStatusMessageChanged(string value)
    {
        _statusTimer?.Stop();
        if (string.IsNullOrEmpty(value)) return;

        _statusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick -= StatusTimer_Tick;
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        StatusMessage = string.Empty;
    }

    public void Dispose()
    {
        _steamApiService.CdnAutoSwitched -= OnCdnAutoSwitched;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
        if (_statusTimer != null)
        {
            _statusTimer.Stop();
            _statusTimer.Tick -= StatusTimer_Tick;
            _statusTimer = null;
        }
    }

    partial void OnSelectedCdnIndexChanged(int value)
    {
        _settings.SelectedCdnIndex = value;
        _settingsService.Save(_settings);
        _steamApiService.UpdateCdnPreference(value);
        StatusMessage = L.Get("Settings_CdnSwitched", CdnDisplayLabel(value));
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnIsFabVisibleChanged(bool value)
    {
        _settings.IsFabVisible = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? L.Get("Settings_FabShown") : L.Get("Settings_FabHidden");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnSelectedThemeChanged(string value)
    {
        _settings.SelectedTheme = value;
        _settingsService.Save(_settings);
        var isLight = false;
        switch (value)
        {
            case "Dark":
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
                break;
            case "Light":
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
                isLight = true;
                break;
            default:
                isLight = GetSystemTheme() == ApplicationTheme.Light;
                ThemeManager.Current.ApplicationTheme = isLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
                break;
        }
        StatusMessage = value switch
        {
            "Dark" => L.Get("Settings_ThemeDark"),
            "Light" => L.Get("Settings_ThemeLight"),
            _ => L.Get("Settings_ThemeSystem"),
        };
        LogService.Info(L.Get("LogCat_Settings"), L.Get("Settings_ThemeChangedLog", StatusMessage));
    }

    private static ApplicationTheme GetSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 1 ? ApplicationTheme.Light : ApplicationTheme.Dark;
        }
        catch { }
        return ApplicationTheme.Dark;
    }

    partial void OnIsCardRefreshVisibleChanged(bool value)
    {
        _settings.IsCardRefreshVisible = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? L.Get("Settings_CardRefreshShown") : L.Get("Settings_CardRefreshHidden");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnAutoFetchCoversChanged(bool value)
    {
        _settings.AutoFetchCovers = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? L.Get("Settings_CoversOn") : L.Get("Settings_CoversOff");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        _settings.AutoRefreshEnabled = value;
        _settingsService.Save(_settings);

        if (value)
            _luaFileManager.StartWatching();
        else
            _luaFileManager.StopWatching();
        StatusMessage = value ? L.Get("Settings_MonitorOn") : L.Get("Settings_MonitorOff");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnAutoCheckUpdateEnabledChanged(bool value)
    {
        _settings.AutoCheckUpdateEnabled = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? L.Get("Settings_UpdateCheckOn") : L.Get("Settings_UpdateCheckOff");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnAutoCheckKernelUpdateEnabledChanged(bool value)
    {
        _settings.AutoCheckKernelUpdateEnabled = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? L.Get("Settings_KernelCheckOn") : L.Get("Settings_KernelCheckOff");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnAutoRefreshKeyCacheChanged(bool value)
    {
        _settings.AutoRefreshKeyCache = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? L.Get("Settings_KeyCacheOn") : L.Get("Settings_KeyCacheOff");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? L.Get("Settings_TrayOn") : L.Get("Settings_TrayOff");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnSelectedBackdropChanged(string value)
    {
        _settings.SelectedBackdrop = value;
        _settingsService.Save(_settings);
        StatusMessage = value switch
        {
            "Acrylic10" => L.Get("Settings_BackdropAcrylic"),
            "Mica" => L.Get("Settings_BackdropMica"),
            "None" => L.Get("Settings_BackdropNone"),
            _ => ""
        };
        if (!string.IsNullOrEmpty(StatusMessage))
            LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnLanguageChanged(string value)
    {
        value = LocalizationService.Normalize(value);
        _settings.Language = value;
        _settingsService.Save(_settings);
        // 先切语言再写提示，提示本身就用新语言显示
        LocalizationService.Instance.SetLanguage(value);
        StatusMessage = L.Get("Settings_LanguageChanged");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    [RelayCommand]
    private void BrowseSteamPath()
    {
        var dialog = new OpenFileDialog
        {
            FileName = "steam.exe",
            Filter = L.Get("Settings_ExeFilter"),
            Title = L.Get("Settings_BrowseSteamTitle")
        };
        if (dialog.ShowDialog() == true)
        {
            var dir = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(dir))
            {
                SteamPath = dir;
                _steamPathService.SetCustomPath(dir);
                _settings.SteamPath = dir;
                _settingsService.Save(_settings);
                RefreshFriendBroadcastToggle();
                RefreshManifestSource();
                RefreshManifestFailoverToggle();
                StatusMessage = L.Get("Settings_SteamPathSet", dir);
                LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
            }
        }
    }

    [RelayCommand]
    private void ResetSteamPath()
    {
        _steamPathService.SetCustomPath(string.Empty);
        var detectedPath = _steamPathService.DetectSteamPath();
        SteamPath = detectedPath ?? L.Get("Settings_SteamNotFound");
        _settings.SteamPath = string.Empty;
        _settingsService.Save(_settings);
        RefreshFriendBroadcastToggle();
        RefreshManifestSource();
        RefreshManifestFailoverToggle();
        StatusMessage = L.Get("Settings_SteamPathReset");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try
        {
            var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            if (!Directory.Exists(cacheDir))
            {
                StatusMessage = L.Get("Settings_NoCacheToClean");
                LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
                return;
            }

            var lockedFiles = new List<string>();
            var deletedCount = 0;

            await Task.Run(() =>
            {
                foreach (var file in Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(file);
                        deletedCount++;
                    }
                    catch (IOException)
                    {
                        lockedFiles.Add(Path.GetFileName(file));
                    }
                    catch { }
                }

                foreach (var dir in Directory.GetDirectories(cacheDir))
                {
                    try { Directory.Delete(dir, true); }
                    catch { }
                }

                Directory.CreateDirectory(Path.Combine(cacheDir, "covers"));
            });

            if (lockedFiles.Count > 0)
            {
                StatusMessage = L.Get("Settings_CacheSkipped", lockedFiles.Count);
                LogService.Info(L.Get("LogCat_Settings"), L.Get("Settings_CacheSkippedLog", lockedFiles.Count, string.Join(", ", lockedFiles)));
            }
            else
            {
                StatusMessage = L.Get("Settings_CacheCleaned", deletedCount);
                LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Settings_CleanFailed", ex.Message);
            LogService.Error(L.Get("LogCat_Settings"), L.Get("Settings_LogCleanFailed", ex));
        }
    }

    [RelayCommand]
    private void OpenLuaFolder()
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder) || !Directory.Exists(luaFolder))
        {
            StatusMessage = L.Get("Settings_LuaFolderMissing");
            LogService.Warn(L.Get("LogCat_Settings"), StatusMessage);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = luaFolder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Settings_OpenFailed", ex.Message);
            LogService.Error(L.Get("LogCat_Settings"), L.Get("Settings_LogOpenLuaFailed", luaFolder, ex));
        }
    }

    [RelayCommand]
    private async Task ChangeLuaFolderAsync()
    {
        var oldFolder = _steamPathService.GetLuaFolder();

        string? dir;
        try
        {
            var owner = new System.Windows.Interop.WindowInteropHelper(System.Windows.Application.Current.MainWindow).Handle;
            dir = FolderPicker.PickFolder(oldFolder, owner);
        }
        catch (Exception ex)
        {
            LogService.Error(L.Get("LogCat_Settings"), L.Get("Settings_LogPickLuaFailed", ex));
            StatusMessage = L.Get("Settings_PickDirFailed");
            return;
        }
        if (string.IsNullOrEmpty(dir)) return;
        if (string.Equals(oldFolder, dir, StringComparison.OrdinalIgnoreCase)) return;

        if (_steamPathService.SetConfiguredLuaPath(dir))
        {
            LuaFolderPath = _steamPathService.GetLuaFolder() ?? L.Get("Settings_NotConfigured");

            // 路径变更后重启文件监听，指向新目录
            if (IsAutoRefreshEnabled)
            {
                _luaFileManager.StopWatching();
                _luaFileManager.StartWatching();
            }

            // 检测原路径是否有残留 lua 文件（含子目录，如被禁用清单），询问是否迁移
            await MigrateLuaFilesIfNeededAsync(oldFolder, dir);

            // 通知主页重新扫描游戏，避免残留旧路径的 lua 引用
            WeakReferenceMessenger.Default.Send(new LuaFolderChangedMessage());

            StatusMessage = L.Get("Settings_LuaDirSet", dir);
            LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
        }
        else
        {
            StatusMessage = L.Get("Settings_LuaDirSetFailed");
            LogService.Error(L.Get("LogCat_Settings"), StatusMessage);
        }
    }

    [RelayCommand]
    private async Task ResetLuaConfigAsync()
    {
        var oldFolder = _steamPathService.GetLuaFolder();

        if (_steamPathService.ResetConfiguredLuaPath())
        {
            LuaFolderPath = _steamPathService.GetLuaFolder() ?? L.Get("Settings_NotConfigured");

            // 自定义路径 → 默认路径，同样触发迁移检测
            await MigrateLuaFilesIfNeededAsync(oldFolder, LuaFolderPath);

            if (IsAutoRefreshEnabled)
            {
                _luaFileManager.StopWatching();
                _luaFileManager.StartWatching();
            }

            WeakReferenceMessenger.Default.Send(new LuaFolderChangedMessage());

            StatusMessage = L.Get("Settings_LuaDirReset", LuaFolderPath);
            LogService.Info(L.Get("LogCat_Settings"), L.Get("Settings_LogLuaDirReset", LuaFolderPath));
        }
        else
        {
            StatusMessage = L.Get("Settings_ResetFailed");
            LogService.Error(L.Get("LogCat_Settings"), L.Get("Settings_LogLuaDirResetFailed"));
        }
    }

    /// <summary>检测旧路径残留 lua 文件（含子目录），弹窗询问后迁移到新路径。</summary>
    private async Task<bool> MigrateLuaFilesIfNeededAsync(string? oldFolder, string newFolder)
    {
        if (string.IsNullOrEmpty(oldFolder) || !Directory.Exists(oldFolder) ||
            string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
            return false;

        var files = Directory.GetFiles(oldFolder, "*.lua", SearchOption.AllDirectories);
        if (files.Length == 0) return false;

        var migrate = await ShowConfirmAsync(
            L.Get("Settings_MigrateTitle"),
            L.Get("Settings_MigrateBody", oldFolder, files.Length, newFolder),
            L.Get("Settings_Migrate"), L.Get("Settings_NoMigrate"));
        if (!migrate) return false;

        var copied = 0;
        foreach (var file in files)
        {
            try
            {
                var relative = Path.GetRelativePath(oldFolder, file);
                var dest = Path.Combine(newFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest, overwrite: true);
                copied++;
            }
            catch
            {
                // 跨卷等场景 File.Move 不可用时回退为复制后删除
                try
                {
                    var relative = Path.GetRelativePath(oldFolder, file);
                    var dest = Path.Combine(newFolder, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, overwrite: true);
                    File.Delete(file);
                    copied++;
                }
                catch (Exception ex2)
                {
                    LogService.Warn(L.Get("LogCat_Settings"), L.Get("Settings_LogMigrateFailed", file, ex2.Message));
                }
            }
        }
        StatusMessage = L.Get("Settings_Migrated", copied, files.Length);
        LogService.Info(L.Get("LogCat_Settings"), L.Get("Settings_LogMigrated", copied, files.Length, newFolder));
        return true;
    }

    private Task<bool> ShowConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null)
        => _dialogService.ShowConfirmAsync(title, message, primaryText ?? L.Get("Common_Confirm"), closeText ?? L.Get("Common_Cancel"));

    [RelayCommand]
    private void OpenSteamFolder()
    {
        var steamDir = SteamPath;
        if (!string.IsNullOrEmpty(steamDir) && Directory.Exists(steamDir))
            Process.Start(new ProcessStartInfo { FileName = steamDir, UseShellExecute = true });
        else
        {
            StatusMessage = L.Get("Settings_SteamPathMissing");
            LogService.Warn(L.Get("LogCat_Settings"), StatusMessage);
        }
    }

    [RelayCommand]
    private void OpenBinStatsFolder()
    {
        var steamDir = SteamPath;
        if (string.IsNullOrEmpty(steamDir) || !Directory.Exists(steamDir))
        {
            StatusMessage = L.Get("Settings_SteamPathMissing");
            LogService.Warn(L.Get("LogCat_Settings"), StatusMessage);
            return;
        }
        var statsDir = Path.Combine(steamDir, "appcache", "stats");
        Directory.CreateDirectory(statsDir);
        Process.Start(new ProcessStartInfo { FileName = statsDir, UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenManifestFolder()
    {
        var steamDir = SteamPath;
        if (string.IsNullOrEmpty(steamDir) || !Directory.Exists(steamDir))
        {
            StatusMessage = L.Get("Settings_SteamPathMissing");
            LogService.Warn(L.Get("LogCat_Settings"), StatusMessage);
            return;
        }
        var manifestDir = Path.Combine(steamDir, "depotcache");
        Directory.CreateDirectory(manifestDir);
        Process.Start(new ProcessStartInfo { FileName = manifestDir, UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenCacheFolder()
    {
        var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
        if (!Directory.Exists(cacheDir))
            Directory.CreateDirectory(cacheDir);
        Process.Start(new ProcessStartInfo { FileName = cacheDir, UseShellExecute = true });
    }

    [RelayCommand]
    private async Task TestCdnSpeedAsync()
    {
        if (IsSpeedTesting) return;
        IsSpeedTesting = true;
        SpeedTestResults.Clear();
        SpeedTestTotal = CdnEndpoint.Defaults.Count;
        SpeedTestProgress = 0;
        StatusMessage = L.Get("Settings_TestingCdn");

        try
        {
            var progress = new Progress<(string Name, long LatencyMs, bool IsSuccess)>(result =>
            {
                SpeedTestProgress++;
                SpeedTestResults.Add(new SpeedTestItem
                {
                    Name = result.Name,
                    LatencyMs = result.LatencyMs,
                    IsSuccess = result.IsSuccess
                });
            });

            var results = await _steamApiService.TestCdnSpeedAsync(progress);

            var best = SpeedTestResults.Where(r => r.IsSuccess).OrderBy(r => r.LatencyMs).FirstOrDefault();
            if (best != null)
            {
                StatusMessage = L.Get("Settings_CdnBest", best.Name, best.LatencyMs);
                LogService.Info(L.Get("LogCat_Settings"), L.Get("Settings_LogCdnBest", best.Name, best.LatencyMs));
            }
            else
            {
                StatusMessage = L.Get("Settings_AllCdnDown");
                LogService.Warn(L.Get("LogCat_Settings"), L.Get("Settings_LogAllCdnDown"));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Settings_SpeedFailed", ex.Message);
            LogService.Error(L.Get("LogCat_Settings"), L.Get("Settings_LogCdnFailed", ex));
        }
        finally
        {
            IsSpeedTesting = false;
        }
    }

    [RelayCommand]
    private async Task TestMirrorSpeedAsync()
    {
        if (IsMirrorSpeedTesting) return;
        IsMirrorSpeedTesting = true;
        MirrorSpeedTestResults.Clear();
        MirrorSpeedTestTotal = GitHubMirror.ReleaseAssetMirrorPrefixes.Length + 1;
        MirrorSpeedTestProgress = 0;
        StatusMessage = L.Get("Settings_TestingMirror");

        try
        {
            var progress = new Progress<(string Name, long LatencyMs, bool IsSuccess)>(result =>
            {
                MirrorSpeedTestProgress++;
                MirrorSpeedTestResults.Add(new SpeedTestItem
                {
                    Name = result.Name,
                    LatencyMs = result.LatencyMs,
                    IsSuccess = result.IsSuccess
                });
            });

            var results = await _manifestRepoService.TestMirrorSpeedAsync(progress);

            var best = MirrorSpeedTestResults.Where(r => r.IsSuccess).OrderBy(r => r.LatencyMs).FirstOrDefault();
            if (best != null)
            {
                StatusMessage = L.Get("Settings_MirrorBest", best.Name, best.LatencyMs);
                LogService.Info(L.Get("LogCat_Settings"), L.Get("Settings_LogMirrorBest", best.Name, best.LatencyMs));
            }
            else
            {
                StatusMessage = L.Get("Settings_AllMirrorDown");
                LogService.Warn(L.Get("LogCat_Settings"), L.Get("Settings_LogAllMirrorDown"));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Settings_SpeedFailed", ex.Message);
            LogService.Error(L.Get("LogCat_Settings"), L.Get("Settings_LogMirrorFailed", ex));
        }
        finally
        {
            IsMirrorSpeedTesting = false;
        }
    }

    [ObservableProperty]
    private bool _isFabVisible = true;

    [ObservableProperty]
    private bool _isCardRefreshVisible = true;

    [ObservableProperty]
    private bool _autoFetchCovers = true;

    [ObservableProperty]
    private bool _autoCheckUpdateEnabled = true;

    [ObservableProperty]
    private bool _autoCheckKernelUpdateEnabled = true;

    [ObservableProperty]
    private bool _autoRefreshKeyCache = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedThemeOption))]
    private string _selectedTheme = "System";

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _downloadMode = "DepotKey";

    [ObservableProperty]
    private string _manifestMirror = "direct";

    [ObservableProperty]
    private bool _isShowTrainerSections = true;

    [ObservableProperty]
    private bool _showRanking = true;    [ObservableProperty]
    private bool _friendBroadcastEnabled = true;

    [ObservableProperty]
    private bool _isFriendBroadcastAvailable = true;

    private bool _syncingFriendBroadcast;

    // 开关唯一真相在内核 toml 里，我只做镜像显示；写失败要回拨，否则开关与文件不一致
    partial void OnFriendBroadcastEnabledChanged(bool value)
    {
        if (_syncingFriendBroadcast) return;
        if (!_steamPathService.SetFriendBroadcastEnabled(value))
        {
            _syncingFriendBroadcast = true;
            try { FriendBroadcastEnabled = !value; }
            finally { _syncingFriendBroadcast = false; }
            StatusMessage = L.Get("Settings_TomlWriteFailed");
            return;
        }
        StatusMessage = value ? L.Get("Settings_BroadcastOn") : L.Get("Settings_BroadcastOff");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    private void RefreshFriendBroadcastToggle()
    {
        _syncingFriendBroadcast = true;
        try
        {
            IsFriendBroadcastAvailable =
                !string.IsNullOrEmpty(_steamPathService.GetCustomPath() ?? _steamPathService.DetectSteamPath());
            FriendBroadcastEnabled = _steamPathService.GetFriendBroadcastEnabled();
        }
        finally { _syncingFriendBroadcast = false; }
    }

    // ========== 上游清单库切换 ==========

    public record ManifestSourceOption(string Id, string DisplayName, string TestUrl, string? UserAgent = null);

    // 探针使用公开游戏的真实 manifest，使各上游走与内核一致的业务路径；
    // 非法参数会导致部分上游返回业务错误而另一部分直接 502，将存活源误判为不可用；
    // 列表顺序与内核 kProviders 表一致，即 failover 顺位（SDM 需 depotId+gid 双参数）
    public List<ManifestSourceOption> ManifestSourceOptions { get; } =
    [
        new("20770407", "20770407", "https://20770407.xyz/manifest/481/3183503801510301321"),
        new("SDM", "SDM", "https://steamapi.993499094.xyz/manifest/481/3183503801510301321"),
        new("manifestdex", "manifestdex", "https://manifest.manifestdex.com/3183503801510301321", "ManifestDeX/1.0"),
        new("wudrm", "wudrm", "http://gmrc.wudrm.com/manifest/3183503801510301321"),
        new("steamrun", "steamrun", "https://manifest.steam.run/api/manifest/3183503801510301321"),
        new("opensteamtool", "opensteamtool", "https://manifest.opensteamtool.com/3183503801510301321"),
    ];

    [ObservableProperty]
    private string _selectedManifestSource = "20770407";

    [ObservableProperty]
    private bool _isManifestSourceTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManifestTestProgressText))]
    private int _manifestTestProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ManifestTestProgressText))]
    private int _manifestTestTotal;

    public string ManifestTestProgressText => $"{ManifestTestProgress}/{ManifestTestTotal}";

    [ObservableProperty]
    private ObservableCollection<ManifestTestResult> _manifestTestResults = new();

    public record ManifestTestResult(string Id, string DisplayName, bool IsReachable, long LatencyMs, int StatusCode, string FailReason = "")
    {
        // 行内状态与接口设置测速保持一致：可用带延迟，不可用给原因（状态码/超时/连接失败）
        public string StatusText => IsReachable ? L.Get("Settings_ManifestRowOk", LatencyMs) : L.Get("Settings_ManifestRowDown", FailReason);

        // 只用红绿两色表达连通结论，不按延迟分色，避免延迟色干扰可用性判断
        public string ColorCode => IsReachable ? "#4CAF50" : "#F44336";
    }

    private bool _syncingManifestSource;

    partial void OnSelectedManifestSourceChanged(string value)
    {
        if (_syncingManifestSource) return;
        if (!_steamPathService.SetManifestSource(value))
        {
            _syncingManifestSource = true;
            try { SelectedManifestSource = _steamPathService.GetManifestSource(); }
            finally { _syncingManifestSource = false; }
            StatusMessage = L.Get("Settings_TomlWriteFailed");
            return;
        }
        StatusMessage = L.Get("Settings_ManifestSwitched", value);
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    private void RefreshManifestSource()
    {
        _syncingManifestSource = true;
        try { SelectedManifestSource = _steamPathService.GetManifestSource(); }
        finally { _syncingManifestSource = false; }
    }

    [ObservableProperty]
    private bool _manifestFailoverEnabled = true;

    [ObservableProperty]
    private bool _isManifestFailoverAvailable = true;

    private bool _syncingManifestFailover;

    // 开关唯一真相在内核 toml 里，我只做镜像显示；写失败要回拨，否则开关与文件不一致
    partial void OnManifestFailoverEnabledChanged(bool value)
    {
        if (_syncingManifestFailover) return;
        if (!_steamPathService.SetManifestFailoverEnabled(value))
        {
            _syncingManifestFailover = true;
            try { ManifestFailoverEnabled = !value; }
            finally { _syncingManifestFailover = false; }
            StatusMessage = L.Get("Settings_TomlWriteFailed");
            return;
        }
        StatusMessage = value ? L.Get("Settings_FailoverOn") : L.Get("Settings_FailoverOff");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    private void RefreshManifestFailoverToggle()
    {
        _syncingManifestFailover = true;
        try
        {
            IsManifestFailoverAvailable =
                !string.IsNullOrEmpty(_steamPathService.GetCustomPath() ?? _steamPathService.DetectSteamPath());
            ManifestFailoverEnabled = _steamPathService.GetManifestFailoverEnabled();
        }
        finally { _syncingManifestFailover = false; }
    }

    [RelayCommand]
    private async Task TestManifestSourceAsync()
    {
        if (IsManifestSourceTesting) return;
        IsManifestSourceTesting = true;
        ManifestTestResults.Clear();
        ManifestTestTotal = ManifestSourceOptions.Count;
        ManifestTestProgress = 0;
        StatusMessage = L.Get("Settings_TestingManifest");

        try
        {
            // 与接口设置测速同一汇报模式：各源并发探测，完成一个即实时落行并推进度
            var progress = new Progress<ManifestTestResult>(r =>
            {
                ManifestTestProgress++;
                ManifestTestResults.Add(r);
            });
            var tasks = ManifestSourceOptions.Select(opt => ProbeManifestSourceAsync(opt, progress));

            var results = await Task.WhenAll(tasks);

            var reachable = results.Count(r => r.IsReachable);
            StatusMessage = L.Get("Settings_ManifestTestDone", reachable, results.Length);
            LogService.Info(L.Get("LogCat_Settings"), L.Get("Settings_LogManifestTest", string.Join(", ", results.Select(r => $"{r.DisplayName}={(r.IsReachable ? r.StatusCode.ToString() : r.FailReason)}({r.LatencyMs}ms)"))));
        }
        finally
        {
            IsManifestSourceTesting = false;
        }
    }

    private async Task<ManifestTestResult> ProbeManifestSourceAsync(
        ManifestSourceOption opt, IProgress<ManifestTestResult> progress)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ManifestTestResult result;
        try
        {
            // 走代理重试链：直连失败的用户靠这里兜底，连接级抖动自动重试；
            // 各源独立客户端命名，避免默认 UA 在共享实例间串味
            var (statusCode, body) = await _httpClientProvider.SendWithProxyRetryAsync(
                $"manifest-probe-{opt.Id}",
                TimeSpan.FromSeconds(8),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, opt.TestUrl);
                    using var resp = await client.SendAsync(request);
                    var text = await resp.Content.ReadAsStringAsync();
                    return (resp.StatusCode, text);
                },
                client =>
                {
                    // 专属 UA 优先：实例创建时已带默认浏览器 UA，不清掉就永远轮不到专属 UA
                    // （manifestdex 只认 ManifestDeX/1.0，其他 UA 一律 403）
                    if (!string.IsNullOrEmpty(opt.UserAgent))
                    {
                        client.DefaultRequestHeaders.UserAgent.Clear();
                        client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
                    }
                    else if (!client.DefaultRequestHeaders.UserAgent.Any())
                    {
                        HttpHeaderHelper.ConfigureApp(client);
                    }
                });
            sw.Stop();
            // 按内核解析请求码的口径判定：拿到有效数字才算可用，502/CF 拦截/业务错误均视为不可用
            var ok = statusCode == System.Net.HttpStatusCode.OK && IsManifestCodeResponse(opt.Id, body);
            result = new ManifestTestResult(opt.Id, opt.DisplayName, ok, sw.ElapsedMilliseconds, (int)statusCode, ok ? "" : ((int)statusCode).ToString());
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            result = new ManifestTestResult(opt.Id, opt.DisplayName, false, sw.ElapsedMilliseconds, 0, L.Get("Settings_FailTimeout"));
        }
        catch (Exception)
        {
            sw.Stop();
            result = new ManifestTestResult(opt.Id, opt.DisplayName, false, sw.ElapsedMilliseconds, 0, L.Get("Settings_FailConn"));
        }
        progress.Report(result);
        return result;
    }

    // 按各上游响应格式校验请求码有效性，与内核解析口径保持一致
    private static bool IsManifestCodeResponse(string providerId, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        if (providerId == "steamrun")
        {
            var key = body.IndexOf("content", StringComparison.OrdinalIgnoreCase);
            if (key < 0) return false;
            var digits = new string(body.Skip(key).SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            return digits.Length > 0;
        }
        var text = body.Trim().Trim('"');
        return text.Length > 0 && text.All(char.IsDigit);
    }

    [ObservableProperty]
    private bool _isShowCopyLogButton;

    [ObservableProperty]
    private bool _enableLogging;

    partial void OnEnableLoggingChanged(bool value)
    {
        _settings.EnableLogging = value;
        _settingsService.Save(_settings);
        LogService.SetEnabled(value);
        StatusMessage = value ? L.Get("Settings_LoggingOn") : L.Get("Settings_LoggingOff");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnIsShowCopyLogButtonChanged(bool value)
    {
        _settings.ShowCopyLogButton = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? L.Get("Settings_CopyLogShown") : L.Get("Settings_CopyLogHidden");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnIsShowTrainerSectionsChanged(bool value)
    {
        _settings.ShowTrainerSections = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? L.Get("Settings_TrainerShown") : L.Get("Settings_TrainerHidden");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnShowRankingChanged(bool value)
    {
        _settings.ShowRanking = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? L.Get("Settings_RankingShown") : L.Get("Settings_RankingHidden");
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    [ObservableProperty]
    private bool _isServiceInstalled;

    [ObservableProperty]
    private string _keyFolderPath = string.Empty;

    partial void OnDownloadModeChanged(string value)
    {
        _settings.DownloadMode = value;
        _settingsService.Save(_settings);
        StatusMessage = value switch
        {
            "Remote" => L.Get("Settings_ModeRemote"),
            "DepotKey" => L.Get("Settings_ModeLocal"),
            _ => ""
        };
        if (!string.IsNullOrEmpty(StatusMessage))
            LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnManifestMirrorChanged(string value)
    {
        _settings.ManifestMirror = value;
        _settingsService.Save(_settings);
        StatusMessage = value == "direct" ? L.Get("Settings_MirrorDirect") : L.Get("Settings_MirrorTo", value);
        LogService.Info(L.Get("LogCat_Settings"), StatusMessage);
    }

    partial void OnKeyFolderPathChanged(string value)
    {
        _settings.KeyFolderPath = value;
        _settingsService.Save(_settings);
    }
}

public class SpeedTestItem
{
    public string Name { get; set; } = string.Empty;
    public long LatencyMs { get; set; }
    public bool IsSuccess { get; set; }
    public string StatusText => IsSuccess ? $"{LatencyMs}ms" : L.Get("Settings_SpeedItemFailed");
    // 镜像加速源与封面 CDN 共用：延迟分三档着色；
    // 上游清单源（ManifestTestResult）是独立的红绿两色，不走这里
    public string ColorCode => IsSuccess ? LatencyMs switch
    {
        <= 500 => "#4CAF50",
        <= 2000 => "#FF9800",
        _ => "#F44336"
    } : "#F44336";
}
