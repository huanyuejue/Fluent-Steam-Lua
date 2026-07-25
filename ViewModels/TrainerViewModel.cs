using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class TrainerViewModel : ObservableObject
{
    private readonly ITrainerService _trainerService;
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISettingsService _settingsService;
    private readonly IGameNameService _gameNameService;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHotSectionVisible))]
    private bool _isShowingHot = true;

    [ObservableProperty]
    private bool _isShowingDownloaded;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHotSectionVisible))]
    private bool _isShowTrainerSections = true;

    public bool HasNoResults => HasSearched && SearchResults.Count == 0;

    public bool IsHotSectionVisible => IsShowingHot && IsShowTrainerSections;

    private bool _hotLoaded;
    private bool _newReleasesLoaded;

    public ObservableCollection<TrainerInfo> HotTrainers { get; } = new();
    public ObservableCollection<TrainerInfo> NewReleases { get; } = new();
    public ObservableCollection<TrainerInfo> SearchResults { get; } = new();
    public ObservableCollection<DownloadedTrainerItem> DownloadedTrainers { get; } = new();

    private static string CacheDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "trainers");

    public TrainerViewModel(ITrainerService trainerService, IHttpClientProvider httpClientProvider,
        ISettingsService settingsService, IGameNameService gameNameService)
    {
        _trainerService = trainerService;
        _httpClientProvider = httpClientProvider;
        _settingsService = settingsService;
        _gameNameService = gameNameService;
        _isShowTrainerSections = _settingsService.Load().ShowTrainerSections;
        _settingsService.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        IsShowTrainerSections = settings.ShowTrainerSections;
    }

    [RelayCommand]
    private async Task LoadSectionsAsync()
    {
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
        IsShowingDownloaded = false;
    }

    [RelayCommand]
    private void ShowDownloadedView()
    {
        IsShowingDownloaded = true;
        _ = LoadDownloadedTrainersAsync();
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
