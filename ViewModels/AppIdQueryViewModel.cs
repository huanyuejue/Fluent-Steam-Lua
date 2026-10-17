using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

// AppID 查询窗：用目录末级名（多为游戏英文名）调小黑盒优先、Steam 商店兜底搜出候选，选中回填
public partial class AppIdQueryViewModel : ObservableObject, IDisposable
{
    private readonly IHttpClientProvider _httpClientProvider;
    private bool _disposed;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ObservableCollection<HeiHeGame> Results { get; } = new();

    public int? SelectedAppId { get; private set; }

    public event Action? RequestClose;

    public AppIdQueryViewModel(IHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var keyword = SearchText?.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            StatusMessage = L.Get("AppId_EnterName");
            return;
        }
        if (IsSearching) return;
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        IsSearching = true;
        Results.Clear();
        StatusMessage = "";
        try
        {
            // 小黑盒优先：只要主游戏，DLC/试玩/软件等统统不要（type 缺失的保留，免得误杀）
            List<HeiHeGame> found;
            try
            {
                found = (await XiaoHeiHeService.SearchByNameAsync(keyword, ct))
                    .Where(g => !IsNonGameType(g.Type))
                    .ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Warn(L.Get("LogCat_Crack"), L.Get("AppId_HeiHeFailed", ex.Message));
                found = new List<HeiHeGame>();
            }
            // 商店兜底：中文区 + 英文区各一次
            if (found.Count == 0)
            {
                foreach (var (cc, lang) in new[] { ("cn", "schinese"), ("us", "english") })
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        found = await SearchSteamStoreAsync(keyword, cc, lang, ct);
                        if (found.Count > 0) break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
                // 商店结果无类型信息：批量问一次 appdetails，按 data.type 只留主游戏；查不到的不拦
                await DropNonGamesByAppDetailsAsync(found, ct);
            }
            foreach (var g in found.Take(10))
                Results.Add(g);
            StatusMessage = Results.Count == 0 ? L.Get("AppId_NoMatch") : L.Get("AppId_FoundCount", Results.Count);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = L.Get("AppId_SearchCancelled");
        }
        catch (Exception ex)
        {
            StatusMessage = L.Get("AppId_SearchFailed", ex.Message);
        }
        finally
        {
            IsSearching = false;
            HasSearched = true;
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }
            cts.Dispose();
        }
    }

    private static bool IsNonGameType(string type) => type switch
    {
        "dlc" or "demo" or "software" or "video" or "series" or "episode" or "mod" or "hardware" or "music" => true,
        _ => false,
    };

    // 无类型候选逐个定性：appdetails 不支持一次传多个 id，只能逐个问；查不到的不拦
    private async Task DropNonGamesByAppDetailsAsync(List<HeiHeGame> list, CancellationToken ct)
    {
        var unknowns = list.Where(g => string.IsNullOrEmpty(g.Type)).Select(g => g.AppId).Distinct().ToList();
        if (unknowns.Count == 0) return;
        var bad = new HashSet<int>();
        foreach (var id in unknowns)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var url = $"https://store.steampowered.com/api/appdetails?appids={id}";
                var json = await _httpClientProvider.SendWithProxyRetryAsync(
                    "crack-appid-detail", TimeSpan.FromSeconds(8),
                    client => client.GetStringAsync(url, ct));
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (!doc.RootElement.TryGetProperty(id.ToString(), out var entry)) continue;
                var ok = entry.TryGetProperty("success", out var s) && s.GetBoolean();
                var type = ok && entry.TryGetProperty("data", out var d) && d.TryGetProperty("type", out var t)
                    ? t.GetString() : null;
                if (type != null && !type.Equals("game", StringComparison.OrdinalIgnoreCase))
                    bad.Add(id);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Warn(L.Get("LogCat_Crack"), L.Get("AppId_ClassifyFailed", id, ex.Message));
            }
        }
        if (bad.Count > 0) list.RemoveAll(g => bad.Contains(g.AppId));
    }

    private async Task<List<HeiHeGame>> SearchSteamStoreAsync(string keyword, string cc, string lang, CancellationToken ct)
    {
        var list = new List<HeiHeGame>();
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(keyword)}&cc={cc}&l={lang}";
        var json = await _httpClientProvider.SendWithProxyRetryAsync(
            "crack-appid-store", TimeSpan.FromSeconds(10),
            client =>
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
                return client.GetStringAsync(url, ct);
            });
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in items.EnumerateArray())
        {
            try
            {
                if (!item.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out var appId))
                    continue;
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? keyword : keyword;
                string cover = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";
                if (item.TryGetProperty("tiny_image", out var tiny) && !string.IsNullOrEmpty(tiny.GetString()))
                    cover = tiny.GetString()!;
                list.Add(new HeiHeGame(appId, name, cover, new List<string> { cover }));
                if (list.Count >= 10) break;
            }
            catch { }
        }
        return list;
    }

    [RelayCommand]
    private void Select(HeiHeGame? game)
    {
        if (game == null) return;
        SelectedAppId = game.AppId;
        RequestClose?.Invoke();
    }
}
