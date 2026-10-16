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
                MonitorStatusText = " -  未有激活绑定项，SvcMonitor后台服务已结束进程";
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
                ? " -  SvcMonitor后台服务正在运行"
                : " -  SvcMonitor后台服务未运行";
        }
        catch (Exception ex)
        {
            LogService.Warn("修改器", $"轮询监控状态失败: {ex.Message}");
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

    private void OnAutoLaunchStatusChanged(string msg)
    {
        StatusMessage = msg;
        if (msg.Contains("失败") || msg.Contains("超时"))
            LogService.Warn("修改器", $"自动启动: {msg}");
        else
            LogService.Info("修改器", $"自动启动: {msg}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TrainerBindings.CollectionChanged -= OnBindingsChanged;
        foreach (var b in TrainerBindings)
            b.PropertyChanged -= OnBindingPropertyChanged;
        _autoLaunchService.StatusChanged -= OnAutoLaunchStatusChanged;
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
            StatusMessage = "正在加载热门推荐...";
            var trainers = await _trainerService.GetHotTrainersAsync(10);
            HotTrainers.Clear();
            foreach (var t in trainers)
                HotTrainers.Add(t);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载热门推荐失败：{ex.Message}";
            LogService.Error("修改器", $"加载热门推荐失败: {ex}");
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
        catch (Exception ex) { LogService.Warn("修改器", $"加载最新发布失败: {ex.Message}"); }
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
        catch (Exception ex) { LogService.Warn("修改器", $"加载已下载修改器列表失败: {ex.Message}"); }

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
                return $"{trainer.GameName}：下载文件不存在";

            var (gameName, options) = TrainerExeParser.Parse(trainer.DownloadUrl);
            if (options.Count == 0)
                return $"{trainer.GameName}：解析未提取到功能项（修改器可能不含中文字符串）";

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

            return $"{trainer.GameName}：提取到 {options.Count} 项功能" +
                   (string.IsNullOrWhiteSpace(gameName) ? "" : $"，中文名「{gameName}」");
        }
        catch (Exception ex)
        {
            return $"解析 {trainer.GameName} 失败：{ex.Message}";
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
        catch (Exception ex) { LogService.Warn("修改器", $"读取功能项缓存失败: {ex.Message}"); return null; }
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
        catch (Exception ex) { LogService.Warn("修改器", $"删除功能项缓存失败: {ex.Message}"); }
    }

    public static string? LoadCachedGameName(string trainerFilePath)
    {
        try
        {
            var key = SanitizeFileName(Path.GetFileNameWithoutExtension(trainerFilePath));
            var file = Path.Combine(CheatOptionsCacheDir, $"{key}.name.txt");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) { LogService.Warn("修改器", $"读取中文名缓存失败: {ex.Message}"); return null; }
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
            System.Diagnostics.Debug.WriteLine($"写入共享绑定配置失败: {ex.Message}");
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
        catch (Exception ex) { LogService.Warn("修改器", $"检查后台服务注册失败: {ex.Message}"); return false; }
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
            MonitorStatusText = " -  SvcMonitor后台服务正在运行";
        else if (!anyEnabled)
            MonitorStatusText = " -  未有激活绑定项，SvcMonitor后台服务已结束进程";
        else
            MonitorStatusText = " -  SvcMonitor后台服务未运行";
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
        catch (Exception ex) { LogService.Warn("修改器", $"启动后台监控进程失败: {ex.Message}"); }
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
        catch (Exception ex) { LogService.Warn("修改器", $"释放 SvcMonitor 失败: {ex.Message}"); return false; }
    }

    [RelayCommand]
    private async Task InstallServiceAsync()
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            "安装后台服务",
            "安装该服务后可在不启动该软件的情况下实现打开游戏自启动修改器，是否安装？\n\n（本服务仅作为游戏进程监控，无其他作用，后台内存占用不到10MB）",
            "安装", "取消");
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
                    StatusMessage = "释放 SvcMonitor 失败";
                    LogService.Error("修改器", "释放 SvcMonitor 失败");
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
            StatusMessage = "后台服务已安装并启动，开机自动运行";
            LogService.Info("修改器", "后台服务已安装并启动，开机自动运行");
        }
        catch (Exception ex)
        {
            StatusMessage = $"安装异常: {ex.Message}";
            LogService.Error("修改器", $"后台服务安装异常: {ex}");
        }
        RefreshMonitorStatus();
    }

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        var confirmed = await _dialogService.ShowConfirmAsync(
            "卸载后台服务",
            "确定卸载该服务？卸载后需要开启该软件才能实现打开游戏自启动修改器",
            "卸载", "取消");
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
            StatusMessage = IsServiceInstalled ? "卸载失败，请手动删除注册表项" : "后台服务已卸载";
            if (IsServiceInstalled)
                LogService.Error("修改器", "后台服务卸载失败，注册表项仍存在");
            else
                LogService.Info("修改器", "后台服务已卸载");
        }
        catch (Exception ex)
        {
            StatusMessage = $"卸载异常: {ex.Message}";
            LogService.Error("修改器", $"后台服务卸载异常: {ex}");
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
            StatusMessage = $"已添加绑定: {dialog.Result.GameName} → {System.IO.Path.GetFileName(dialog.Result.TrainerFilePath)}";
            LogService.Info("修改器", $"已添加绑定: {dialog.Result.GameName} → {dialog.Result.TrainerFilePath}");
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
                StatusMessage = $"已更新绑定: {dialog.Result.GameName} → {System.IO.Path.GetFileName(dialog.Result.TrainerFilePath)}";
                LogService.Info("修改器", $"已更新绑定: {dialog.Result.GameName} → {dialog.Result.TrainerFilePath}");
            }
        }
    }

    [RelayCommand]
    private async Task RemoveBinding(TrainerBinding? binding)
    {
        if (binding == null) return;
        // 删绑定不可恢复，照删文件确认框同样前置确认
        var confirmed = await _dialogService.ShowConfirmAsync(
            "确认删除",
            $"确定要删除绑定 \"{binding.GameName}\" 吗？",
            "删除", "取消");
        if (!confirmed) return;
        TrainerBindings.Remove(binding);
        SaveBindings();
        StatusMessage = $"已删除绑定: {binding.GameName}";
        LogService.Info("修改器", $"已删除绑定: {binding.GameName}");
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = SearchQuery?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            StatusMessage = "请输入游戏名称";
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
                StatusMessage = "未找到匹配的修改器";
                LogService.Warn("修改器", $"搜索未找到匹配的修改器: {query}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"搜索失败：{ex.Message}";
            LogService.Error("修改器", $"搜索修改器失败: {ex}");
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
                StatusMessage = $"获取 {trainer.GameName} 下载链接失败";
                LogService.Warn("修改器", $"获取 {trainer.GameName} 下载链接失败 (URL: {trainer.PageUrl})");
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

            StatusMessage = $"{trainer.GameName} 下载完成，正在解析修改器功能...";
            var parseResult = await CacheCheatOptionsAsync(trainer);
            StatusMessage = parseResult ?? $"{trainer.GameName} 下载完成";
            LogService.Info("修改器", $"{trainer.GameName} 下载完成: {savePath} ({bytesReadTotal} bytes)");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{trainer.GameName} 下载超时";
            LogService.Warn("修改器", $"{trainer.GameName} 下载超时 (URL: {downloadUrl})");
        }
        catch (Exception ex)
        {
            StatusMessage = $"下载 {trainer.GameName} 失败：{ex.Message}";
            LogService.Error("修改器", $"下载 {trainer.GameName} 失败: {ex}");
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
            StatusMessage = "文件不存在，无法创建快捷方式";
            return;
        }
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var stem = SanitizeFileName(item.DisplayName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(stem))
                stem = Path.GetFileNameWithoutExtension(item.FileName);
            if (string.IsNullOrWhiteSpace(stem))
                stem = "修改器快捷方式";
            var lnkPath = Path.Combine(desktop, stem + ".lnk");
            if (File.Exists(lnkPath))
            {
                StatusMessage = $"桌面已存在“{stem}.lnk”，未重复创建";
                return;
            }
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                StatusMessage = "系统缺少 WSH 组件，无法创建快捷方式";
                return;
            }
            var shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                StatusMessage = "系统缺少 WSH 组件，无法创建快捷方式";
                return;
            }
            // 晚绑定调用，不引入 COM 引用（dotnet build 不支持 ResolveComReference）
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, [lnkPath]);
            if (shortcut == null)
            {
                StatusMessage = "创建快捷方式失败：系统组件无响应";
                return;
            }
            var shortcutType = shortcut.GetType();
            var dir = Path.GetDirectoryName(item.FilePath) ?? string.Empty;
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [item.FilePath]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [dir]);
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [item.FilePath + ",0"]);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, [item.DisplayName]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            StatusMessage = $"已创建桌面快捷方式：{stem}.lnk";
        }
        catch (Exception ex)
        {
            StatusMessage = $"创建快捷方式失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteDownloadedFileAsync(DownloadedTrainerItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;
        if (!File.Exists(item.FilePath)) return;

        var confirmed = await _dialogService.ShowConfirmAsync(
            "确认删除",
            $"确定要删除文件 \"{item.DisplayName}\" 吗？",
            "删除", "取消");

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
