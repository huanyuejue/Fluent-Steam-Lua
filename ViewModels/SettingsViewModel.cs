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
    private string _selectedBackdrop = "Acrylic10";

    public record BackdropOption(string Display, string Value);

    public List<BackdropOption> BackdropOptions { get; } = new()
    {
        new("亚克力", "Acrylic10"),
        new("云母", "Mica"),
        new("无", "None"),
    };

    public record ThemeOption(string Display, string Value);

    public List<ThemeOption> ThemeOptions { get; } = new()
    {
        new("跟随系统", "System"),
        new("深色模式", "Dark"),
        new("浅色模式", "Light"),
    };

    public List<CdnEndpoint> CdnEndpoints { get; } = CdnEndpoint.Defaults;

    public ObservableCollection<SpeedTestItem> SpeedTestResults { get; } = new();

    public ObservableCollection<SpeedTestItem> MirrorSpeedTestResults { get; } = new();

    public SettingsViewModel(ISteamPathService steamPathService, ILuaFileManager luaFileManager,
        ISettingsService settingsService, ISteamApiService steamApiService,
        ISteamManifestRepoService manifestRepoService, IDialogService dialogService)
    {
        _steamPathService = steamPathService;
        _luaFileManager = luaFileManager;
        _settingsService = settingsService;
        _steamApiService = steamApiService;
        _manifestRepoService = manifestRepoService;
        _dialogService = dialogService;
        _settings = settingsService.Load();

        SteamPath = _settings.SteamPath;
        IsAutoRefreshEnabled = _settings.AutoRefreshEnabled;
        IsFabVisible = _settings.IsFabVisible;
        IsCardRefreshVisible = _settings.IsCardRefreshVisible;
        ManifestMirror = GitHubMirror.NormalizePreferredHost(_settings.ManifestMirror);
        AutoFetchCovers = _settings.AutoFetchCovers;
        AutoCheckUpdateEnabled = _settings.AutoCheckUpdateEnabled;
        AutoRefreshKeyCache = _settings.AutoRefreshKeyCache;
        IsShowTrainerSections = _settings.ShowTrainerSections;
        IsShowCopyLogButton = _settings.ShowCopyLogButton;
        EnableLogging = _settings.EnableLogging;
        MinimizeToTray = _settings.MinimizeToTray;

        SelectedTheme = _settings.SelectedTheme;
        SelectedCdnIndex = Math.Clamp(_settings.SelectedCdnIndex, 0, CdnEndpoints.Count - 1);
        _selectedBackdrop = _settings.SelectedBackdrop;
        DownloadMode = _settings.DownloadMode;
        KeyFolderPath = _settings.KeyFolderPath;

        _steamApiService.CdnAutoSwitched += OnCdnAutoSwitched;

        if (string.IsNullOrEmpty(SteamPath))
        {
            var detectedPath = steamPathService.DetectSteamPath();
            SteamPath = detectedPath ?? "未检测到Steam";
        }

        LuaFolderPath = steamPathService.GetLuaFolder() ?? "未配置";

        RefreshFriendBroadcastToggle();
        RefreshManifestSource();
    }

    private void OnCdnAutoSwitched(int newIndex)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (newIndex >= 0 && newIndex < CdnEndpoints.Count)
            {
                SelectedCdnIndex = newIndex;
                StatusMessage = $"封面节点已自动切换: {CdnEndpoints[newIndex].Name}";
                LogService.Warn("设置", $"封面节点已自动切换: {CdnEndpoints[newIndex].Name}");
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
        StatusMessage = $"封面节点已切换: {CdnEndpoints[value].Name}";
        LogService.Info("设置", $"封面节点已切换: {CdnEndpoints[value].Name}");
    }

    partial void OnIsFabVisibleChanged(bool value)
    {
        _settings.IsFabVisible = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "悬浮按钮已显示" : "悬浮按钮已隐藏";
        LogService.Info("设置", value ? "悬浮按钮已显示" : "悬浮按钮已隐藏");
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
            "Dark" => "已切换为深色模式",
            "Light" => "已切换为浅色模式",
            _ => "已跟随系统主题",
        };
        LogService.Info("设置", $"主题已切换: {StatusMessage}");
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
        StatusMessage = value ? "卡片刷新按钮已显示" : "卡片刷新按钮已隐藏";
        LogService.Info("设置", value ? "卡片刷新按钮已显示" : "卡片刷新按钮已隐藏");
    }

    partial void OnAutoFetchCoversChanged(bool value)
    {
        _settings.AutoFetchCovers = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "主页游戏封面自动获取已开启" : "主页游戏封面自动获取已关闭";
        LogService.Info("设置", value ? "主页游戏封面自动获取已开启" : "主页游戏封面自动获取已关闭");
    }

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        _settings.AutoRefreshEnabled = value;
        _settingsService.Save(_settings);

        if (value)
            _luaFileManager.StartWatching();
        else
            _luaFileManager.StopWatching();
        StatusMessage = value ? "自动监控已开启" : "自动监控已关闭";
        LogService.Info("设置", value ? "自动监控已开启" : "自动监控已关闭");
    }

    partial void OnAutoCheckUpdateEnabledChanged(bool value)
    {
        _settings.AutoCheckUpdateEnabled = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "启动时自动检查更新已开启" : "启动时自动检查更新已关闭";
        LogService.Info("设置", value ? "启动时自动检查更新已开启" : "启动时自动检查更新已关闭");
    }

    partial void OnAutoRefreshKeyCacheChanged(bool value)
    {
        _settings.AutoRefreshKeyCache = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "自动更新密钥缓存已开启" : "自动更新密钥缓存已关闭";
        LogService.Info("设置", value ? "自动更新密钥缓存已开启" : "自动更新密钥缓存已关闭");
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "关闭时最小化到系统托盘已开启" : "关闭时最小化到系统托盘已关闭";
        LogService.Info("设置", value ? "关闭时最小化到系统托盘已开启" : "关闭时最小化到系统托盘已关闭");
    }

    partial void OnSelectedBackdropChanged(string value)
    {
        _settings.SelectedBackdrop = value;
        _settingsService.Save(_settings);
        StatusMessage = value switch
        {
            "Acrylic10" => "背景效果已切换为亚克力",
            "Mica" => "背景效果已切换为云母",
            "None" => "背景效果已关闭",
            _ => ""
        };
        if (!string.IsNullOrEmpty(StatusMessage))
            LogService.Info("设置", StatusMessage);
    }

    [RelayCommand]
    private void BrowseSteamPath()
    {
        var dialog = new OpenFileDialog
        {
            FileName = "steam.exe",
            Filter = "Steam可执行文件|steam.exe",
            Title = "选择Steam安装路径"
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
                StatusMessage = $"Steam路径已设置为: {dir}";
                LogService.Info("设置", $"Steam路径已设置为: {dir}");
            }
        }
    }

    [RelayCommand]
    private void ResetSteamPath()
    {
        _steamPathService.SetCustomPath(string.Empty);
        var detectedPath = _steamPathService.DetectSteamPath();
        SteamPath = detectedPath ?? "未检测到Steam";
        _settings.SteamPath = string.Empty;
        _settingsService.Save(_settings);
        RefreshFriendBroadcastToggle();
        RefreshManifestSource();
        StatusMessage = "已重置为自动检测路径";
        LogService.Info("设置", "已重置为自动检测路径");
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try
        {
            var cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            if (!Directory.Exists(cacheDir))
            {
                StatusMessage = "没有需要清理的缓存";
                LogService.Info("设置", "没有需要清理的缓存");
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
                StatusMessage = $"缓存已清理(跳过{lockedFiles.Count}个占用文件)";
                LogService.Info("设置", $"缓存已清理(跳过{lockedFiles.Count}个占用文件): {string.Join(", ", lockedFiles)}");
            }
            else
            {
                StatusMessage = $"缓存已清理(共{deletedCount}个文件)";
                LogService.Info("设置", $"缓存已清理(共{deletedCount}个文件)");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"清理失败: {ex.Message}";
            LogService.Error("设置", $"清理缓存失败: {ex}");
        }
    }

    [RelayCommand]
    private void OpenLuaFolder()
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder) || !Directory.Exists(luaFolder))
        {
            StatusMessage = "Lua文件夹不存在";
            LogService.Warn("设置", "Lua文件夹不存在");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = luaFolder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开失败: {ex.Message}";
            LogService.Error("设置", $"打开 Lua 文件夹失败 ({luaFolder}): {ex}");
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
            LogService.Error("设置", $"选择 Lua 目录失败: {ex}");
            StatusMessage = "打开目录选择失败，请重试";
            return;
        }
        if (string.IsNullOrEmpty(dir)) return;
        if (string.Equals(oldFolder, dir, StringComparison.OrdinalIgnoreCase)) return;

        if (_steamPathService.SetConfiguredLuaPath(dir))
        {
            LuaFolderPath = _steamPathService.GetLuaFolder() ?? "未配置";

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

            StatusMessage = $"Lua 目录已设置为: {dir}";
            LogService.Info("设置", $"Lua 目录已设置为: {dir}");
        }
        else
        {
            StatusMessage = "Lua 目录设置失败，请检查配置文件写入权限";
            LogService.Error("设置", "Lua 目录设置失败，请检查配置文件写入权限");
        }
    }

    [RelayCommand]
    private async Task ResetLuaConfigAsync()
    {
        var oldFolder = _steamPathService.GetLuaFolder();

        if (_steamPathService.ResetConfiguredLuaPath())
        {
            LuaFolderPath = _steamPathService.GetLuaFolder() ?? "未配置";

            // 自定义路径 → 默认路径，同样触发迁移检测
            await MigrateLuaFilesIfNeededAsync(oldFolder, LuaFolderPath);

            if (IsAutoRefreshEnabled)
            {
                _luaFileManager.StopWatching();
                _luaFileManager.StartWatching();
            }

            WeakReferenceMessenger.Default.Send(new LuaFolderChangedMessage());

            StatusMessage = $"已重置为默认目录: {LuaFolderPath}";
            LogService.Info("设置", $"已重置 Lua 目录为默认: {LuaFolderPath}");
        }
        else
        {
            StatusMessage = "重置失败，请检查配置文件写入权限";
            LogService.Error("设置", "重置 Lua 目录配置失败");
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
            "迁移 Lua 文件",
            $"原路径 {oldFolder} 存在 {files.Length} 个 lua 文件（含子目录），\n是否迁移到新路径 {newFolder}？",
            "迁移", "不迁移");
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
                    LogService.Warn("设置", $"迁移 {file} 失败: {ex2.Message}");
                }
            }
        }
        StatusMessage = $"已迁移 {copied}/{files.Length} 个 lua 文件";
        LogService.Info("设置", $"已迁移 {copied}/{files.Length} 个 lua 文件到 {newFolder}");
        return true;
    }

    private Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消")
        => _dialogService.ShowConfirmAsync(title, message, primaryText, closeText);

    [RelayCommand]
    private void OpenSteamFolder()
    {
        var steamDir = SteamPath;
        if (!string.IsNullOrEmpty(steamDir) && Directory.Exists(steamDir))
            Process.Start(new ProcessStartInfo { FileName = steamDir, UseShellExecute = true });
        else
        {
            StatusMessage = "Steam路径不存在或未设置";
            LogService.Warn("设置", "Steam路径不存在或未设置");
        }
    }

    [RelayCommand]
    private void OpenBinStatsFolder()
    {
        var steamDir = SteamPath;
        if (string.IsNullOrEmpty(steamDir) || !Directory.Exists(steamDir))
        {
            StatusMessage = "Steam路径不存在或未设置";
            LogService.Warn("设置", "Steam路径不存在或未设置");
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
            StatusMessage = "Steam路径不存在或未设置";
            LogService.Warn("设置", "Steam路径不存在或未设置");
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
        StatusMessage = "正在测试所有CDN节点...";

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
                StatusMessage = $"测速完成，最快节点: {best.Name} ({best.LatencyMs}ms)";
                LogService.Info("设置", $"CDN 测速完成，最快节点: {best.Name} ({best.LatencyMs}ms)");
            }
            else
            {
                StatusMessage = "所有节点均不可达";
                LogService.Warn("设置", "CDN 测速完成，所有节点均不可达");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"测速失败: {ex.Message}";
            LogService.Error("设置", $"CDN 测速失败: {ex}");
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
        StatusMessage = "正在测试所有镜像源...";

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
                StatusMessage = $"测速完成，最快镜像源: {best.Name} ({best.LatencyMs}ms)，可在上方下拉框中切换";
                LogService.Info("设置", $"镜像源测速完成，最快: {best.Name} ({best.LatencyMs}ms)");
            }
            else
            {
                StatusMessage = "所有镜像源均不可达";
                LogService.Warn("设置", "镜像源测速完成，所有源均不可达");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"测速失败: {ex.Message}";
            LogService.Error("设置", $"镜像源测速失败: {ex}");
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
    private bool _autoRefreshKeyCache = true;

    [ObservableProperty]
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
            StatusMessage = "写入 opensteamtool.toml 失败，请检查文件权限";
            return;
        }
        StatusMessage = value ? "好友游玩状态广播已开启" : "好友游玩状态广播已关闭";
        LogService.Info("设置", StatusMessage);
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
    // 非法参数会导致部分上游返回业务错误而另一部分直接 502，将存活源误判为不可用
    public List<ManifestSourceOption> ManifestSourceOptions { get; } =
    [
        new("20770407", "20770407", "https://20770407.xyz/manifest/481/3183503801510301321"),
        new("wudrm", "wudrm", "http://gmrc.wudrm.com/manifest/3183503801510301321"),
        new("opensteamtool", "opensteamtool", "https://manifest.opensteamtool.com/3183503801510301321"),
        new("steamrun", "steamrun", "https://manifest.steam.run/api/manifest/3183503801510301321"),
        new("manifestdex", "manifestdex", "https://manifest.manifestdex.com/3183503801510301321", "ManifestDeX/1.0"),
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

    public record ManifestTestResult(string Id, string DisplayName, bool IsReachable, long LatencyMs, int StatusCode)
    {
        // 行内状态与接口设置测速保持一致：可用带延迟，不可用只给结论
        public string StatusText => IsReachable ? $"可用 {LatencyMs}ms" : "不可用";

        // 我只用红绿两色表达连通结论，不按延迟分色，避免延迟色干扰可用性判断
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
            StatusMessage = "写入 opensteamtool.toml 失败，请检查文件权限";
            return;
        }
        StatusMessage = $"上游清单库已切换为 {value}";
        LogService.Info("设置", StatusMessage);
    }

    private void RefreshManifestSource()
    {
        _syncingManifestSource = true;
        try { SelectedManifestSource = _steamPathService.GetManifestSource(); }
        finally { _syncingManifestSource = false; }
    }

    [RelayCommand]
    private async Task TestManifestSourceAsync()
    {
        if (IsManifestSourceTesting) return;
        IsManifestSourceTesting = true;
        ManifestTestResults.Clear();
        ManifestTestTotal = ManifestSourceOptions.Count;
        ManifestTestProgress = 0;
        StatusMessage = "正在测试上游清单库连通性...";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            // 与接口设置测速同一汇报模式：各源并发探测，完成一个即实时落行并推进度
            var progress = new Progress<ManifestTestResult>(r =>
            {
                ManifestTestProgress++;
                ManifestTestResults.Add(r);
            });
            var tasks = ManifestSourceOptions.Select(opt => ProbeManifestSourceAsync(http, opt, progress));

            var results = await Task.WhenAll(tasks);

            var reachable = results.Count(r => r.IsReachable);
            StatusMessage = $"连通性测试完成：{reachable}/{results.Length} 个可用";
            LogService.Info("设置", $"上游清单库连通性测试：{string.Join(", ", results.Select(r => $"{r.DisplayName}={r.StatusCode}({r.LatencyMs}ms)"))}");
        }
        finally
        {
            IsManifestSourceTesting = false;
        }
    }

    private static async Task<ManifestTestResult> ProbeManifestSourceAsync(
        HttpClient http, ManifestSourceOption opt, IProgress<ManifestTestResult> progress)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ManifestTestResult result;
        try
        {
            // 探活请求复刻内核发包条件，有 UA 门禁的源缺失 UA 会被直接拦截
            using var request = new HttpRequestMessage(HttpMethod.Get, opt.TestUrl);
            if (!string.IsNullOrEmpty(opt.UserAgent))
                request.Headers.UserAgent.ParseAdd(opt.UserAgent);
            using var resp = await http.SendAsync(request);
            sw.Stop();
            var body = await resp.Content.ReadAsStringAsync();
            // 按内核解析请求码的口径判定：拿到有效数字才算可用，502/CF 拦截/业务错误均视为不可用
            var ok = resp.StatusCode == System.Net.HttpStatusCode.OK && IsManifestCodeResponse(opt.Id, body);
            result = new ManifestTestResult(opt.Id, opt.DisplayName, ok, sw.ElapsedMilliseconds, (int)resp.StatusCode);
        }
        catch (Exception)
        {
            sw.Stop();
            result = new ManifestTestResult(opt.Id, opt.DisplayName, false, sw.ElapsedMilliseconds, 0);
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
        StatusMessage = value ? "日志记录已开启，将输出到软件目录的 app.log" : "日志记录已关闭";
        LogService.Info("设置", value ? "日志记录已开启，将输出到软件目录的 app.log" : "日志记录已关闭");
    }

    partial void OnIsShowCopyLogButtonChanged(bool value)
    {
        _settings.ShowCopyLogButton = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "日志复制按钮已显示" : "日志复制按钮已隐藏";
        LogService.Info("设置", value ? "日志复制按钮已显示" : "日志复制按钮已隐藏");
    }

    partial void OnIsShowTrainerSectionsChanged(bool value)
    {
        _settings.ShowTrainerSections = value;
        _settingsService.Save(_settings);
        StatusMessage = value ? "修改器推荐栏目已显示" : "修改器推荐栏目已隐藏";
        LogService.Info("设置", value ? "修改器推荐栏目已显示" : "修改器推荐栏目已隐藏");
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
            "Remote" => "已切换为远程清单仓库",
            "DepotKey" => "已切换为本地缓存仓库 V1",
            "DepotKey2" => "已切换为本地缓存仓库 V2",
            _ => ""
        };
        if (!string.IsNullOrEmpty(StatusMessage))
            LogService.Info("设置", StatusMessage);
    }

    partial void OnManifestMirrorChanged(string value)
    {
        _settings.ManifestMirror = value;
        _settingsService.Save(_settings);
        StatusMessage = value == "direct" ? "镜像加速源已切换为直连" : $"镜像加速源已切换为 {value}（直连最后尝试）";
        LogService.Info("设置", StatusMessage);
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
    public string StatusText => IsSuccess ? $"{LatencyMs}ms" : "失败";
    public string ColorCode => IsSuccess ? LatencyMs switch
    {
        <= 200 => "#4CAF50",
        <= 500 => "#FF9800",
        _ => "#F44336"
    } : "#F44336";
}
