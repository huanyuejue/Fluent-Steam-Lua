using System.Collections.ObjectModel;
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

public partial class TrainerViewModel : ObservableObject
{
    private readonly ITrainerService _trainerService;
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISettingsService _settingsService;
    private readonly IGameNameService _gameNameService;
    private readonly ITrainerAutoLaunchService _autoLaunchService;

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
        RefreshMonitorStatus();
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
        ISettingsService settingsService, IGameNameService gameNameService,
        ITrainerAutoLaunchService autoLaunchService)
    {
        _trainerService = trainerService;
        _httpClientProvider = httpClientProvider;
        _settingsService = settingsService;
        _gameNameService = gameNameService;
        _autoLaunchService = autoLaunchService;
        _isShowTrainerSections = _settingsService.Load().ShowTrainerSections;
        _autoLaunchService.StatusChanged += msg => StatusMessage = msg;
        _settingsService.SettingsChanged += OnSettingsChanged;
        LoadBindings();
        IsServiceInstalled = IsMonitorInstalled();
        RefreshMonitorStatus();
        _monitorPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _monitorPollTimer.Tick += MonitorPollTimer_Tick;
        _monitorPollTimer.Start();
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        IsShowTrainerSections = settings.ShowTrainerSections;
    }

    [RelayCommand]
    private async Task LoadSectionsAsync()
    {
        if (!IsShowTrainerSections) return;
        var tasks = new List<Task>();
        if (!_hotLoaded)
            tasks.Add(LoadHotAsync());
        if (!_newReleasesLoaded)
            tasks.Add(LoadNewReleasesAsync());
        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
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
        catch { }
    }

    [RelayCommand]
    private Task LoadDownloadedTrainersAsync()
    {
        DownloadedTrainers.Clear();
        try
        {
            var dir = CacheDir;
            if (!Directory.Exists(dir))
            {
                SyncDownloadedStates(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                return Task.CompletedTask;
            }

            var files = Directory.GetFiles(dir, "*.exe")
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f));
            var downloadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<DownloadedTrainerItem>();

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    items.Add(new DownloadedTrainerItem { FileName = name, FilePath = file });
                    downloadedNames.Add(name);
                }
            }

            foreach (var item in items)
                item.DisplayName = ExtractGameName(item.FileName);

            foreach (var item in items)
                DownloadedTrainers.Add(item);

            SyncDownloadedStates(downloadedNames);

            _ = RefreshChineseNamesCoreAsync(items, forceRefresh: false);
        }
        catch { }

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

    private void LoadBindings()
    {
        var bindings = _settingsService.Load().TrainerBindings;
        TrainerBindings.Clear();
        foreach (var b in bindings)
            TrainerBindings.Add(b);
    }

    private static string SharedBindingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteamLuaManager", "bindings.json");

    [RelayCommand]
    private void SaveBindings()
    {
        var settings = _settingsService.Load();
        settings.TrainerBindings = TrainerBindings.ToList();
        _settingsService.Save(settings);
        _autoLaunchService.ReloadBindings();
        WriteSharedBindings();
        TryStartMonitorIfNeeded();
        RefreshMonitorStatus();
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
            if (Process.GetProcessesByName(procName).Any(p => !p.HasExited))
                return true;

            // 没有运行则检查注册表（可能注册了但尚未启动）
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            var val = key?.GetValue(RegistryRunName) as string;
            return !string.IsNullOrEmpty(val);
        }
        catch { return false; }
    }

    private static void KillMonitorProcess()
    {
        var name = Path.GetFileNameWithoutExtension(MonitorExePath);
        foreach (var p in Process.GetProcessesByName(name))
        {
            if (!p.HasExited)
            {
                try { p.Kill(); p.WaitForExit(2000); }
                catch { }
            }
        }
    }

    private void RefreshMonitorStatus()
    {
        var procName = Path.GetFileNameWithoutExtension(MonitorExePath);
        var running = Process.GetProcessesByName(procName).Any(p => !p.HasExited);
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
            if (Process.GetProcessesByName(procName).Any(p => !p.HasExited))
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
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });

            RefreshMonitorStatus();
        }
        catch { }
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
        catch { return false; }
    }

    [RelayCommand]
    private async Task InstallServiceAsync()
    {
        var confirm = new iNKORE.UI.WPF.Modern.Controls.ContentDialog
        {
            Title = "安装后台服务",
            Content = "安装该服务后可在不启动该软件的情况下实现打开游戏自启动修改器，是否安装？\n\n（本服务仅作为游戏进程监控，无其他作用，后台内存占用不到10MB）",
            PrimaryButtonText = "安装",
            CloseButtonText = "取消",
            DefaultButton = iNKORE.UI.WPF.Modern.Controls.ContentDialogButton.Primary
        };
        if (await confirm.ShowAsync() != iNKORE.UI.WPF.Modern.Controls.ContentDialogResult.Primary)
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
                    return;
                }
            }

            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                key?.SetValue(RegistryRunName, exePath);

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });

            IsServiceInstalled = true;
            StatusMessage = "后台服务已安装并启动，开机自动运行";
        }
        catch (Exception ex)
        {
            StatusMessage = $"安装异常: {ex.Message}";
        }
        RefreshMonitorStatus();
    }

    [RelayCommand]
    private async Task UninstallServiceAsync()
    {
        var confirm = new iNKORE.UI.WPF.Modern.Controls.ContentDialog
        {
            Title = "卸载后台服务",
            Content = "确定卸载该服务？卸载后需要开启该软件才能实现打开游戏自启动修改器",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = iNKORE.UI.WPF.Modern.Controls.ContentDialogButton.Primary
        };
        if (await confirm.ShowAsync() != iNKORE.UI.WPF.Modern.Controls.ContentDialogResult.Primary)
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
        }
        catch (Exception ex)
        {
            StatusMessage = $"卸载异常: {ex.Message}";
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
            }
        }
    }

    [RelayCommand]
    private void RemoveBinding(TrainerBinding? binding)
    {
        if (binding == null) return;
        TrainerBindings.Remove(binding);
        SaveBindings();
        StatusMessage = $"已删除绑定: {binding.GameName}";
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

            // 并行获取每个结果的修改项数量
            if (results.Count > 0)
            {
                await Parallel.ForEachAsync(results, async (r, ct) =>
                {
                    var count = await _trainerService.GetCheatCountAsync(r.PageUrl);
                    r.CheatCount = count;
                });
            }

            HasSearched = true;
            OnPropertyChanged(nameof(HasNoResults));
            IsShowingHot = results.Count == 0;
            if (results.Count == 0)
                StatusMessage = "未找到匹配的修改器";
        }
        catch (Exception ex)
        {
            StatusMessage = $"搜索失败：{ex.Message}";
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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = trainer.DownloadUrl,
                UseShellExecute = true
            });
            return;
        }

        trainer.IsDownloading = true;
        trainer.DownloadProgress = 0;
        StatusMessage = string.Empty;

        try
        {
            var downloadUrl = await _trainerService.GetDownloadUrlAsync(trainer.PageUrl);
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                StatusMessage = $"获取 {trainer.GameName} 下载链接失败";
                return;
            }

            var dir = CacheDir;
            Directory.CreateDirectory(dir);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var client = _httpClientProvider.GetClient("trainer-download", TimeSpan.FromSeconds(60));

            using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var fileName = GetFileNameFromResponse(response, trainer.GameName);
            var savePath = Path.Combine(dir, fileName);

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long bytesReadTotal = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, cts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cts.Token);
                bytesReadTotal += bytesRead;
                if (totalBytes > 0)
                    trainer.DownloadProgress = Math.Round((double)bytesReadTotal / totalBytes * 100, 1);
            }

            trainer.DownloadUrl = savePath;
            trainer.IsDownloaded = true;
            StatusMessage = $"{trainer.GameName} 下载完成";
            _ = LoadDownloadedTrainersAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"{trainer.GameName} 下载超时";
        }
        catch (Exception ex)
        {
            StatusMessage = $"下载 {trainer.GameName} 失败：{ex.Message}";
        }
        finally
        {
            trainer.IsDownloading = false;
        }
    }

    private static string GetFileNameFromResponse(HttpResponseMessage response, string gameName)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        if (disposition?.FileName != null)
        {
            var name = disposition.FileName.Trim('"');
            if (!string.IsNullOrWhiteSpace(name))
                return name;
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

    [RelayCommand]
    private void OpenDownloadedFile(DownloadedTrainerItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;
        if (File.Exists(item.FilePath))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.FilePath,
                UseShellExecute = true
            });
    }

    [RelayCommand]
    private async Task DeleteDownloadedFileAsync(DownloadedTrainerItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FilePath)) return;
        if (!File.Exists(item.FilePath)) return;

        var dialog = new ContentDialog
        {
            Title = "确认删除",
            Content = new TextBlock
            {
                Text = $"确定要删除文件 \"{item.DisplayName}\" 吗？",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            File.Delete(item.FilePath);
            _ = LoadDownloadedTrainersAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshChineseNamesAsync()
    {
        var items = DownloadedTrainers.ToArray();
        await RefreshChineseNamesCoreAsync(items, forceRefresh: true);
    }

    private async Task RefreshChineseNamesCoreAsync(IEnumerable<DownloadedTrainerItem> items, bool forceRefresh)
    {
        foreach (var item in items)
        {
            try
            {
                var gameName = ExtractGameName(item.FileName);
                if (string.IsNullOrWhiteSpace(gameName)) continue;
                var chineseName = await _gameNameService.GetChineseNameAsync(gameName, forceRefresh);
                if (!string.IsNullOrWhiteSpace(chineseName) && chineseName != item.DisplayName)
                    item.DisplayName = chineseName;
            }
            catch { }
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

    private void SyncDownloadedStates(HashSet<string> downloadedNames)
    {
        var normalizedFiles = downloadedNames.Select(n => NormalizeForMatch(n)).ToHashSet();

        foreach (var trainer in HotTrainers.Concat(NewReleases).Concat(SearchResults))
        {
            var normalized = NormalizeForMatch(trainer.GameName);
            trainer.IsDownloaded = normalizedFiles.Any(n => n.Contains(normalized));
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
