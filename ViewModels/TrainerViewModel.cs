using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using SteamLuaManager.Views;

namespace SteamLuaManager.ViewModels;

public partial class TrainerViewModel : ObservableObject, IDisposable
{
    private readonly ITrainerService _trainerService;
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISettingsService _settingsService;
    private readonly ITrainerAutoLaunchService _autoLaunchService;
    private readonly IDialogService _dialogService;
    private bool _disposed;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHotSectionVisible))]
    private bool _isShowingSearch = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHotSectionVisible))]
    private bool _isShowingHot = true;

    [ObservableProperty]
    private bool _isShowingDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitorStatusVisible))]
    private bool _isShowingBinding;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _monitorPollTimer;

    partial void OnStatusMessageChanged(string value)
    {
        _statusTimer?.Stop();
        if (string.IsNullOrEmpty(value)) return;
        _statusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick -= StatusTimer_Tick;
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        StatusMessage = string.Empty;
    }

    private void MonitorPollTimer_Tick(object? sender, EventArgs e)
    {
        _ = PollMonitorOnceAsync();
    }

    private bool _monitorBusy;

    // 监控状态轮询：快照放池线程、无激活绑定时停表；异常内部吞掉（定时器上下文无 await 调用方）
    private async Task PollMonitorOnceAsync()
    {
        if (_monitorBusy) return;
        _monitorBusy = true;
        try
        {
            if (!TrainerBindings.Any(b => b.IsEnabled))
            {
                MonitorStatusText = L.Get("Trainer_MonitorIdle");
                _monitorPollTimer?.Stop();
                return;
            }
            if (_monitorPollTimer is { } timer && !timer.IsEnabled)
                timer.Start();
            var procName = Path.GetFileNameWithoutExtension(MonitorExePath);
            var running = await Task.Run(() =>
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    using (p)
                    {
                        if (!p.HasExited) return true;
                    }
                }
                return false;
            });
            MonitorStatusText = running
                ? L.Get("Trainer_MonitorRunning")
                : L.Get("Trainer_MonitorStopped");
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogPollFailed", ex.Message));
        }
        finally
        {
            _monitorBusy = false;
        }
    }

    private void OnBindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (TrainerBinding b in e.OldItems)
                b.PropertyChanged -= OnBindingPropertyChanged;
        }
        if (e.NewItems != null)
        {
            foreach (TrainerBinding b in e.NewItems)
                b.PropertyChanged += OnBindingPropertyChanged;
        }
    }

    private void OnBindingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrainerBinding.IsEnabled))
            _ = PollMonitorOnceAsync();
    }

    private void EnsureBindingsHooked()
    {
        TrainerBindings.CollectionChanged -= OnBindingsChanged;
        TrainerBindings.CollectionChanged += OnBindingsChanged;
        foreach (var b in TrainerBindings)
        {
            b.PropertyChanged -= OnBindingPropertyChanged;
            b.PropertyChanged += OnBindingPropertyChanged;
        }
    }

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHotSectionVisible))]
    private bool _isShowTrainerSections = true;

    public bool HasNoResults => HasSearched && SearchResults.Count == 0;

    public bool IsHotSectionVisible => IsShowingSearch && IsShowingHot && IsShowTrainerSections;

    private bool _hotLoaded;
    private bool _newReleasesLoaded;

    public ObservableCollection<TrainerInfo> HotTrainers { get; } = new();
    public ObservableCollection<TrainerInfo> NewReleases { get; } = new();
    public ObservableCollection<TrainerInfo> SearchResults { get; } = new();
    public ObservableCollection<DownloadedTrainerItem> DownloadedTrainers { get; } = new();
    public ObservableCollection<TrainerBinding> TrainerBindings { get; } = new();

    private static string CacheDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "trainers");

    public TrainerViewModel(ITrainerService trainerService, IHttpClientProvider httpClientProvider,
        ISettingsService settingsService,
        ITrainerAutoLaunchService autoLaunchService,
        IDialogService dialogService)
    {
        _trainerService = trainerService;
        _httpClientProvider = httpClientProvider;
        _settingsService = settingsService;


        _autoLaunchService = autoLaunchService;
        _dialogService = dialogService;
        _isShowTrainerSections = _settingsService.Load().ShowTrainerSections;
        _autoLaunchService.StatusChanged += OnAutoLaunchStatusChanged;
        LocalizationService.Instance.PropertyChanged += OnLanguageChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        LoadBindings();
        EnsureBindingsHooked();
        IsServiceInstalled = IsMonitorInstalled();
        RefreshMonitorStatus();
        _monitorPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _monitorPollTimer.Tick += MonitorPollTimer_Tick;
        _ = PollMonitorOnceAsync();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        IsShowTrainerSections = settings.ShowTrainerSections;
    }

    private void OnAutoLaunchStatusChanged(string msg, bool isWarning)
    {
        StatusMessage = msg;
        if (isWarning)
            LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogAutoLaunch", msg));
        else
            LogService.Info(L.Get("LogCat_Trainer"), L.Get("Trainer_LogAutoLaunch", msg));
    }

    // 绑定卡片的自动激活摘要含本地化文案，切语言后逐项刷新
    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        foreach (var b in TrainerBindings) b.RefreshLocalizedTexts();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TrainerBindings.CollectionChanged -= OnBindingsChanged;
        foreach (var b in TrainerBindings)
            b.PropertyChanged -= OnBindingPropertyChanged;
        _autoLaunchService.StatusChanged -= OnAutoLaunchStatusChanged;
        LocalizationService.Instance.PropertyChanged -= OnLanguageChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        if (_statusTimer != null)
        {
            _statusTimer.Stop();
            _statusTimer.Tick -= StatusTimer_Tick;
            _statusTimer = null;
        }
        if (_monitorPollTimer != null)
        {
            _monitorPollTimer.Stop();
            _monitorPollTimer.Tick -= MonitorPollTimer_Tick;
            _monitorPollTimer = null;
        }
    }

    [RelayCommand]
    private async Task LoadSectionsAsync()
    {
        var tasks = new List<Task>();
        if (IsShowTrainerSections)
        {
            if (!_hotLoaded)
                tasks.Add(LoadHotAsync());
            if (!_newReleasesLoaded)
                tasks.Add(LoadNewReleasesAsync());
        }
        if (tasks.Count > 0)
            await Task.WhenAll(tasks);

        // 必须在热门/新作填充完成后再同步已下载状态，
        // 否则同步发生在列表为空时（首次进入的竞态），已下载修改器仍显示"下载"
        await LoadDownloadedTrainersAsync();
    }

    private async Task LoadHotAsync()
    {
        _hotLoaded = true;
        try
        {
            StatusMessage = L.Get("Trainer_LoadingHot");
            var trainers = await _trainerService.GetHotTrainersAsync(10);
            HotTrainers.Clear();
            foreach (var t in trainers)
                HotTrainers.Add(t);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Trainer_HotLoadFailed", ex.Message);
            LogService.Error(L.Get("LogCat_Trainer"), L.Get("Trainer_LogHotLoadFailed", ex));
        }
    }

    private async Task LoadNewReleasesAsync()
    {
        _newReleasesLoaded = true;
        try
        {
            var trainers = await _trainerService.GetNewReleasesAsync(10);
            NewReleases.Clear();
            foreach (var t in trainers)
                NewReleases.Add(t);
        }
        catch (Exception ex) { LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogNewReleaseFailed", ex.Message)); }
    }

    private Task LoadDownloadedTrainersAsync()
    {
        DownloadedTrainers.Clear();
        try
        {
            var dir = CacheDir;
            if (!Directory.Exists(dir))
            {
                SyncDownloadedStates(new List<DownloadedTrainerItem>());
                return Task.CompletedTask;
            }

            var files = Directory.GetFiles(dir, "*.exe")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f));
            var items = new List<DownloadedTrainerItem>();

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    items.Add(new DownloadedTrainerItem { FileName = name, FilePath = file });
                }
            }

            foreach (var item in items)
            {
                // 优先使用缓存的中文名，否则从文件名提取英文名
                item.DisplayName = LoadCachedGameName(item.FilePath) ?? ExtractGameName(item.FileName);
            }

            foreach (var item in items)
                DownloadedTrainers.Add(item);

            SyncDownloadedStates(items);

        }
        catch (Exception ex) { LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogDownloadedListFailed", ex.Message)); }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ShowSearchView()
    {
        IsShowingSearch = true;
        IsShowingDownloaded = false;
        IsShowingBinding = false;
        RefreshMonitorStatus();
    }

    [RelayCommand]
    private void ShowDownloadedView()
    {
        IsShowingSearch = false;
        IsShowingDownloaded = true;
        IsShowingBinding = false;
        RefreshMonitorStatus();
        _ = LoadDownloadedTrainersAsync();
    }

    [RelayCommand]
    private void ShowBindingView()
    {
        IsShowingSearch = false;
        IsShowingDownloaded = false;
        IsShowingBinding = true;
        RefreshMonitorStatus();
    }

    // ── 修改器选项缓存 ──
    public static string CheatOptionsCacheDir => Path.Combine(CacheDir, "options");

    private async Task<string?> CacheCheatOptionsAsync(TrainerInfo trainer)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(trainer.DownloadUrl) || !File.Exists(trainer.DownloadUrl))
                return L.Get("Trainer_NoFile", trainer.GameName);

            var (gameName, options) = TrainerExeParser.Parse(trainer.DownloadUrl);
            if (options.Count == 0)
                return L.Get("Trainer_NoOptions", trainer.GameName);

            Directory.CreateDirectory(CheatOptionsCacheDir);
            var key = SanitizeFileName(Path.GetFileNameWithoutExtension(trainer.DownloadUrl));
            var file = Path.Combine(CheatOptionsCacheDir, $"{key}.json");
            var json = JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(file, json);

            // 缓存中文名（供列表加载时读取）
            if (!string.IsNullOrWhiteSpace(gameName))
            {
                var nameFile = Path.Combine(CheatOptionsCacheDir, $"{key}.name.txt");
                await File.WriteAllTextAsync(nameFile, gameName);

                // 更新列表中已有项的 DisplayName
                var item = DownloadedTrainers.FirstOrDefault(d =>
                    d.FilePath?.Equals(trainer.DownloadUrl, StringComparison.OrdinalIgnoreCase) == true);
                if (item != null)
                    item.DisplayName = gameName;
            }

            return L.Get("Trainer_Extracted", trainer.GameName, options.Count) +
                   (string.IsNullOrWhiteSpace(gameName) ? "" : L.Get("Trainer_ExtractedNamed", gameName));
        }
        catch (Exception ex)
        {
            return L.Get("Trainer_ParseFailed", trainer.GameName, ex.Message);
        }
    }

    public static List<CheatOption>? LoadCachedOptions(string trainerFilePath)
    {
        try
        {
            var key = SanitizeFileName(Path.GetFileNameWithoutExtension(trainerFilePath));
            var file = Path.Combine(CheatOptionsCacheDir, $"{key}.json");
            if (!File.Exists(file)) return null;
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<List<CheatOption>>(json);
        }
        catch (Exception ex) { LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogOptionsCacheFailed", ex.Message)); return null; }
    }

    private static void DeleteCachedOptions(string trainerFilePath)
    {
        try
        {
            var key = SanitizeFileName(Path.GetFileNameWithoutExtension(trainerFilePath));
            foreach (var ext in new[] { ".json", ".name.txt" })
            {
                var file = Path.Combine(CheatOptionsCacheDir, $"{key}{ext}");
                if (File.Exists(file)) File.Delete(file);
            }
        }
        catch (Exception ex) { LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogDeleteOptionsFailed", ex.Message)); }
    }

    public static string? LoadCachedGameName(string trainerFilePath)
    {
        try
        {
            var key = SanitizeFileName(Path.GetFileNameWithoutExtension(trainerFilePath));
            var file = Path.Combine(CheatOptionsCacheDir, $"{key}.name.txt");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) { LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogNameCacheFailed", ex.Message)); return null; }
    }

    private void LoadBindings()
    {
        var bindings = _settingsService.Load().TrainerBindings;
        foreach (var old in TrainerBindings)
            old.PropertyChanged -= OnBindingPropertyChanged;
        TrainerBindings.Clear();
        foreach (var b in bindings)
            TrainerBindings.Add(b);
        EnsureBindingsHooked();
    }

    private static string SharedBindingsPath => Path.Combine(
        MonitorDir, "bindings.json");

    [RelayCommand]
    private void SaveBindings()
    {
        var settings = _settingsService.Load();
        settings.TrainerBindings = TrainerBindings.ToList();
        _settingsService.Save(settings);
        _autoLaunchService.ReloadBindings();
        WriteSharedBindings();
        TryStartMonitorIfNeeded();
        _ = PollMonitorOnceAsync();
    }

    private void WriteSharedBindings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SharedBindingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(TrainerBindings.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SharedBindingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(L.Get("Trainer_SharedConfigWriteFailed", ex.Message));
        }
    }

    // ── 后台监控服务 ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitorStatusVisible))]
    private bool _isServiceInstalled;

    [ObservableProperty]
    private string _monitorStatusText = string.Empty;

    public bool MonitorStatusVisible => IsShowingBinding && IsServiceInstalled;

    private static string MonitorDir => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "SvcMonitor");

    private static string MonitorExePath => Path.Combine(MonitorDir, "SvcMonitor.exe");

    private const string RegistryRunName = "SteamLuaManagerMonitor";

    private bool IsMonitorInstalled()
    {
        try
        {
            var procName = Path.GetFileNameWithoutExtension(MonitorExePath);
            // 只要进程里有SvcMonitor在运行，就视为已安装
            var procs = Process.GetProcessesByName(procName);
            try
            {
                foreach (var p in procs)
                {
                    try { if (!p.HasExited) return true; }
                    catch { }
                }
            }
            finally
            {
                foreach (var p in procs)
                    try { p.Dispose(); } catch { }
            }

            // 没有运行则检查注册表（可能注册了但尚未启动）
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            var val = key?.GetValue(RegistryRunName) as string;
            return !string.IsNullOrEmpty(val);
        }
        catch (Exception ex) { LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogRegCheckFailed", ex.Message)); return false; }
    }

    private static void KillMonitorProcess()
    {
        var name = Path.GetFileNameWithoutExtension(MonitorExePath);
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                if (!p.HasExited)
                {
                    try { p.Kill(); p.WaitForExit(2000); }
                    catch { }
                }
            }
        }
    }

    private void RefreshMonitorStatus()
    {
        var procName = Path.GetFileNameWithoutExtension(MonitorExePath);
        var running = false;
        foreach (var p in Process.GetProcessesByName(procName))
        {
            using (p)
            {
                if (!p.HasExited)
                {
                    running = true;
                    break;
                }
            }
        }
        var anyEnabled = TrainerBindings.Any(b => b.IsEnabled);
        if (running)
            MonitorStatusText = L.Get("Trainer_MonitorRunning");
        else if (!anyEnabled)
            MonitorStatusText = L.Get("Trainer_MonitorIdle");
        else
            MonitorStatusText = L.Get("Trainer_MonitorStopped");
    }

    private void TryStartMonitorIfNeeded()
    {
        try
        {
            var procName = Path.GetFileNameWithoutExtension(MonitorExePath);
            var alreadyRunning = false;
            foreach (var p in Process.GetProcessesByName(procName))
            {
                using (p)
                {
                    if (!p.HasExited)
                    {
                        alreadyRunning = true;
                        break;
                    }
                }
            }
            if (alreadyRunning)
                return;
            if (!TrainerBindings.Any(b => b.IsEnabled))
                return;

            // 仅当已安装（exe 存在）时才启动，不自动安装
            var exePath = MonitorExePath;
            if (!File.Exists(exePath))
                return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            })?.Dispose();

            RefreshMonitorStatus();
        }
        catch (Exception ex) { LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogMonitorStartFailed", ex.Message)); }
    }

    private static bool ExtractEmbeddedMonitor(string targetDir)
    {
        try
        {
            Directory.CreateDirectory(targetDir);
            var assembly = Assembly.GetExecutingAssembly();
            const string resourceName = "SteamLuaManager.Resources.SvcMonitor.zip";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return false;
            using var zip = new ZipArchive(stream);
            foreach (var entry in zip.Entries)
            {
                var dest = Path.Combine(targetDir, entry.FullName);
                var dir = Path.GetDirectoryName(dest)!;
                Directory.CreateDirectory(dir);
                if (!entry.FullName.EndsWith("/"))
                    entry.ExtractToFile(dest, true);
            }
            return true;
        }
        catch (Exception ex) { LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogMonitorExtractFailed", ex.Message)); return false; }
    }

    [RelayCommand]
    private async Task InstallServiceAsync()
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            L.Get("Trainer_InstallSvcTitle"),
            L.Get("Trainer_InstallSvcBody"),
            L.Get("Trainer_Install"), L.Get("Common_Cancel"));
        if (!confirmed)
            return;

        try
        {
            var exePath = MonitorExePath;
            if (!File.Exists(exePath))
            {
                var dir = Path.GetDirectoryName(exePath)!;
                if (!ExtractEmbeddedMonitor(dir))
                {
                    StatusMessage = L.Get("Trainer_ExtractSvcFailed");
                    LogService.Error(L.Get("LogCat_Trainer"), L.Get("Trainer_ExtractSvcFailed"));
                    return;
                }
            }

            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                key?.SetValue(RegistryRunName, exePath);

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            })?.Dispose();

            IsServiceInstalled = true;
            StatusMessage = L.Get("Trainer_SvcInstalled");
            LogService.Info(L.Get("LogCat_Trainer"), L.Get("Trainer_SvcInstalled"));
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Trainer_InstallError", ex.Message);
            LogService.Error(L.Get("LogCat_Trainer"), L.Get("Trainer_LogInstallError", ex));
        }
        RefreshMonitorStatus();
    }

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            L.Get("Trainer_UninstallSvcTitle"),
            L.Get("Trainer_UninstallSvcBody"),
            L.Get("Trainer_Uninstall"), L.Get("Common_Cancel"));
        if (!confirmed)
            return;

        try
        {
            KillMonitorProcess();

            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                key?.DeleteValue(RegistryRunName, false);

            // 删除 SvcMonitor 文件夹
            try { Directory.Delete(MonitorDir, true); } catch { }

            await Task.Delay(500);
            IsServiceInstalled = IsMonitorInstalled();
            StatusMessage = IsServiceInstalled ? L.Get("Trainer_UninstallFailedManual") : L.Get("Trainer_SvcUninstalled");
            if (IsServiceInstalled)
                LogService.Error(L.Get("LogCat_Trainer"), L.Get("Trainer_LogUninstallFailed"));
            else
                LogService.Info(L.Get("LogCat_Trainer"), L.Get("Trainer_SvcUninstalled"));
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Trainer_UninstallError", ex.Message);
            LogService.Error(L.Get("LogCat_Trainer"), L.Get("Trainer_LogUninstallError", ex));
        }
        RefreshMonitorStatus();
    }

    [RelayCommand]
    private async Task AddBindingAsync()
    {
        if (DownloadedTrainers.Count == 0)
            await LoadDownloadedTrainersAsync();

        var dialog = new TrainerBindingDialog(DownloadedTrainers);
        dialog.Owner = Application.Current.MainWindow;
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            TrainerBindings.Add(dialog.Result);
            SaveBindings();
            StatusMessage = L.Get("Trainer_BindingAdded", dialog.Result.GameName, System.IO.Path.GetFileName(dialog.Result.TrainerFilePath));
            LogService.Info(L.Get("LogCat_Trainer"), L.Get("Trainer_BindingAdded", dialog.Result.GameName, dialog.Result.TrainerFilePath));
        }
    }

    [RelayCommand]
    private async Task EditBindingAsync(TrainerBinding? binding)
    {
        if (binding == null) return;

        if (DownloadedTrainers.Count == 0)
            await LoadDownloadedTrainersAsync();

        var dialog = new TrainerBindingDialog(DownloadedTrainers, binding);
        dialog.Owner = Application.Current.MainWindow;
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            var idx = TrainerBindings.IndexOf(binding);
            if (idx >= 0)
            {
                TrainerBindings[idx] = dialog.Result;
                SaveBindings();
                StatusMessage = L.Get("Trainer_BindingUpdated", dialog.Result.GameName, System.IO.Path.GetFileName(dialog.Result.TrainerFilePath));
                LogService.Info(L.Get("LogCat_Trainer"), L.Get("Trainer_BindingUpdated", dialog.Result.GameName, dialog.Result.TrainerFilePath));
            }
        }
    }

    [RelayCommand]
    private async Task RemoveBinding(TrainerBinding? binding)
    {
        if (binding == null) return;
        // 删绑定不可恢复，照删文件确认框同样前置确认
        var confirmed = await _dialogService.ShowConfirmAsync(
            L.Get("Trainer_ConfirmDeleteTitle"),
            L.Get("Trainer_ConfirmDeleteBinding", binding.GameName),
            L.Get("Trainer_Delete"), L.Get("Common_Cancel"));
        if (!confirmed) return;
        TrainerBindings.Remove(binding);
        SaveBindings();
        StatusMessage = L.Get("Trainer_BindingDeleted", binding.GameName);
        LogService.Info(L.Get("LogCat_Trainer"), L.Get("Trainer_BindingDeleted", binding.GameName));
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = SearchQuery?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            StatusMessage = L.Get("Trainer_EnterGameName");
            return;
        }

        if (IsSearching) return;

        IsSearching = true;
        HasSearched = false;
        SearchResults.Clear();
        StatusMessage = string.Empty;

        try
        {
            var results = await _trainerService.SearchTrainersAsync(query);

            foreach (var r in results)
                SearchResults.Add(r);

            // 搜索结果填充后即时同步已下载状态与本地路径，避免显示"下载"或误触发下载
            await LoadDownloadedTrainersAsync();

            HasSearched = true;
            OnPropertyChanged(nameof(HasNoResults));
            IsShowingHot = results.Count == 0;
            if (results.Count == 0)
            {
                StatusMessage = L.Get("Trainer_NoMatch");
                LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogSearchEmpty", query));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Trainer_SearchFailed", ex.Message);
            LogService.Error(L.Get("LogCat_Trainer"), L.Get("Trainer_LogSearchFailed", ex));
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync(TrainerInfo? trainer)
    {
        if (trainer == null || trainer.IsDownloading) return;

        // 已下载则直接打开
        if (trainer.IsDownloaded && !string.IsNullOrWhiteSpace(trainer.DownloadUrl))
        {
            var openPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = trainer.DownloadUrl,
                UseShellExecute = true
            };
            // 本地文件才定工作目录（URL 走浏览器，与目录无关）
            if (!string.IsNullOrEmpty(trainer.DownloadUrl) &&
                !trainer.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var downloadDir = Path.GetDirectoryName(trainer.DownloadUrl);
                if (!string.IsNullOrEmpty(downloadDir)) openPsi.WorkingDirectory = downloadDir;
            }
            System.Diagnostics.Process.Start(openPsi);
            return;
        }

        trainer.IsDownloading = true;
        trainer.DownloadProgress = 0;
        StatusMessage = string.Empty;

        string? downloadUrl = null;
        try
        {
            downloadUrl = await _trainerService.GetDownloadUrlAsync(trainer.PageUrl);
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                StatusMessage = L.Get("Trainer_DownloadUrlFailed", trainer.GameName);
                LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogDownloadUrlFailed", trainer.GameName, trainer.PageUrl));
                return;
            }

            var dir = CacheDir;
            Directory.CreateDirectory(dir);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var client = _httpClientProvider.GetClient("trainer-download", TimeSpan.FromSeconds(60));

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            if (Uri.TryCreate(trainer.PageUrl, UriKind.Absolute, out var refererUri))
                request.Headers.Referrer = refererUri;
            request.Headers.TryAddWithoutValidation("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var fileName = GetFileNameFromResponse(response, trainer.GameName);
            var savePath = Path.Combine(dir, fileName);

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cts.Token);

            long bytesReadTotal = 0;
            {
                // 单独作用域确保 FileStream 在解析前释放
                await using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                var buffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, cts.Token)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
                    bytesReadTotal += bytesRead;
                    if (totalBytes > 0)
                        trainer.DownloadProgress = Math.Round((double)bytesReadTotal / totalBytes * 100, 1);
                }
            }

            trainer.DownloadUrl = savePath;
            trainer.IsDownloaded = true;
            await LoadDownloadedTrainersAsync();

            StatusMessage = L.Get("Trainer_Downloading", trainer.GameName);
            var parseResult = await CacheCheatOptionsAsync(trainer);
            StatusMessage = parseResult ?? L.Get("Trainer_Downloaded", trainer.GameName);
            LogService.Info(L.Get("LogCat_Trainer"), L.Get("Trainer_LogDownloaded", trainer.GameName, savePath, bytesReadTotal));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = L.Get("Trainer_DownloadTimeout", trainer.GameName);
            LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Trainer_LogDownloadTimeout", trainer.GameName, downloadUrl));
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Trainer_DownloadFailed", trainer.GameName, ex.Message);
            LogService.Error(L.Get("LogCat_Trainer"), L.Get("Trainer_LogDownloadFailed", trainer.GameName, ex));
        }
        finally
        {
            trainer.IsDownloading = false;
        }
    }

    private static string GetFileNameFromResponse(HttpResponseMessage response, string gameName)
    {
        // 服务端文件名不可信：绝对路径会让 Path.Combine 丢弃下载目录，
        // 目录穿越符会写到目录之外，统一展平为纯文件名
        var disposition = response.Content.Headers.ContentDisposition;
        var raw = disposition?.FileNameStar;
        if (string.IsNullOrWhiteSpace(raw))
            raw = disposition?.FileName;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var flat = SanitizeFileName(Path.GetFileName(raw.Trim('"').Trim()));
            if (!string.IsNullOrWhiteSpace(flat) && !IsReservedFileName(flat))
                return flat;
        }

        var ext = response.Content.Headers.ContentType?.MediaType switch
        {
            "application/x-msdownload" or
            "application/octet-stream" or
            "application/x-msdos-program" => ".exe",
            "application/zip" or "application/x-zip-compressed" => ".zip",
            _ => ".exe"
        };

        return $"{SanitizeFileName(gameName)}-FLiNG{ext}";
    }

    private static bool IsReservedFileName(string fileName)
    {
        // Windows 保留设备名即使带扩展名也无法创建，命中则回退游戏名命名
        var stem = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5"
            or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5"
            or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    }

    [RelayCommand]
    private void OpenDownloadedFile(DownloadedTrainerItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;
        if (File.Exists(item.FilePath))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.FilePath,
                WorkingDirectory = Path.GetDirectoryName(item.FilePath) ?? string.Empty,
                UseShellExecute = true
            });
    }

    [RelayCommand]
    private void CreateDesktopShortcut(DownloadedTrainerItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;
        if (!File.Exists(item.FilePath))
        {
            StatusMessage = L.Get("Trainer_FileMissing");
            return;
        }
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var stem = SanitizeFileName(item.DisplayName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(stem))
                stem = Path.GetFileNameWithoutExtension(item.FileName);
            if (string.IsNullOrWhiteSpace(stem))
                stem = L.Get("Trainer_ShortcutNameFallback");
            var lnkPath = Path.Combine(desktop, stem + ".lnk");
            if (File.Exists(lnkPath))
            {
                StatusMessage = L.Get("Trainer_ShortcutExists", stem);
                return;
            }
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                StatusMessage = L.Get("Trainer_NoWsh");
                return;
            }
            var shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                StatusMessage = L.Get("Trainer_NoWsh");
                return;
            }
            // 晚绑定调用，不引入 COM 引用（dotnet build 不支持 ResolveComReference）
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, [lnkPath]);
            if (shortcut == null)
            {
                StatusMessage = L.Get("Trainer_ShortcutNoResponse");
                return;
            }
            var shortcutType = shortcut.GetType();
            var dir = Path.GetDirectoryName(item.FilePath) ?? string.Empty;
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [item.FilePath]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [dir]);
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [item.FilePath + ",0"]);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, [item.DisplayName]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            StatusMessage = L.Get("Trainer_ShortcutCreated", stem);
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("Trainer_ShortcutFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteDownloadedFileAsync(DownloadedTrainerItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;
        if (!File.Exists(item.FilePath)) return;

        var confirmed = await _dialogService.ShowConfirmAsync(
            L.Get("Trainer_ConfirmDeleteTitle"),
            L.Get("Trainer_ConfirmDeleteFile", item.DisplayName),
            L.Get("Trainer_Delete"), L.Get("Common_Cancel"));

        if (confirmed)
        {
            File.Delete(item.FilePath);
            // 同时删除对应的缓存文件
            DeleteCachedOptions(item.FilePath);
            _ = LoadDownloadedTrainersAsync();
        }
    }

    [RelayCommand]
    private void OpenCacheDir()
    {
        var dir = CacheDir;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        SearchResults.Clear();
        HasSearched = false;
        OnPropertyChanged(nameof(HasNoResults));
        IsShowingHot = true;
        StatusMessage = string.Empty;
    }

    private void SyncDownloadedStates(List<DownloadedTrainerItem> items)
    {
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = NormalizeForMatch(item.FileName);
            if (!string.IsNullOrWhiteSpace(key))
                byFileName.TryAdd(key, item.FilePath);
        }

        foreach (var trainer in HotTrainers.Concat(NewReleases).Concat(SearchResults))
        {
            var normalized = NormalizeForMatch(trainer.GameName);
            var match = byFileName.FirstOrDefault(kv => kv.Key.Contains(normalized));
            if (match.Key != null)
            {
                // 同时记录本地文件路径，使"打开"按钮直接启动已下载的修改器（不再走下载逻辑）
                trainer.IsDownloaded = true;
                trainer.DownloadUrl = match.Value;
            }
            else
            {
                trainer.IsDownloaded = false;
            }
        }
    }

    private static readonly string[] TrailingSuffixes =
    [
        " Early Access",
        " Deluxe Edition",
        " Gold Edition",
        " GOTY Edition",
        " Complete Edition",
        " Definitive Edition",
        " Steam"
    ];

    private static string ExtractGameName(string fileName)
    {
        // "Destiny of Immortal Early Access Plus 58 Trainer" → "Destiny of Immortal"
        // Step 1: 去掉末尾的 "Plus N Trainer" 或 "Trainer" 以及版本号
        var name = fileName;
        var idx = name.LastIndexOf(" Trainer", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) name = name[..idx].Trim();
        idx = name.LastIndexOf(" Plus ", StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            var after = name[(idx + 6)..].Trim();
            if (after.All(c => char.IsDigit(c) || c == '.' || c == '+'))
                name = name[..idx].Trim();
        }
        idx = name.LastIndexOf(" v", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) name = name[..idx].Trim();
        // Step 2: 迭代去掉结尾已知后缀（处理 "Early Access Deluxe Edition" 等嵌套）
        bool stripped;
        do
        {
            stripped = false;
            foreach (var suffix in TrailingSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^suffix.Length].Trim();
                    stripped = true;
                    break;
                }
            }
        } while (stripped);
        return name;
    }

    private static string NormalizeForMatch(string name)
    {
        return string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
    }
}
