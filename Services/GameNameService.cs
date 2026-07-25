using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class GameNameService : IGameNameService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ConcurrentDictionary<string, string>? _cache;

    public GameNameService(IHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
        _cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "trainers", "game_names.json");
    }

    public async Task<string> GetChineseNameAsync(string gameName, bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return gameName;
        var key = gameName.Trim();

        await _gate.WaitAsync();
        try
        {
            // 加载文件缓存
            if (_cache == null) await LoadCacheAsync();

            // 非强制刷新则查缓存
            if (!forceRefresh)
            {
                if (_cache!.TryGetValue(key, out var cached))
                    return cached;
            }

            // 查询 Steam Store API
            var chineseName = await QuerySteamStoreAsync(key);
            var result = chineseName ?? key;

            _cache![key] = result;
            await SaveCacheAsync();

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool ContainsCjk(string text)
    {
        return text.Any(c => c is (>= '\u4E00' and <= '\u9FFF')
                                or (>= '\u3400' and <= '\u4DBF')
                                or (>= '\uF900' and <= '\uFAFF'));
    }

    private async Task<string?> QuerySteamStoreAsync(string gameName)
    {
        try
        {
            var searchUrl = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(gameName)}&cc=cn&l=schinese";
            var searchJson = await _httpClientProvider.SendWithProxyRetryAsync(
                "steam-store",
                TimeSpan.FromSeconds(10),
                client => client.GetStringAsync(searchUrl));

            using var searchDoc = JsonDocument.Parse(searchJson);
            var items = searchDoc.RootElement.GetProperty("items");
            if (items.GetArrayLength() == 0) return null;

            var firstAppId = items[0].GetProperty("id").GetInt32();

            // 取第一个匹配结果的中文名（检测是否含 CJK 字符）
            if (items[0].TryGetProperty("name", out var nameEl))
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(name) && ContainsCjk(name))
                    return name;
            }

            // 如果 store search 没有返回中文名，查 appdetails 拿中文名
            var detailUrl = $"https://store.steampowered.com/api/appdetails/?appids={firstAppId}&cc=cn&l=schinese";
            var detailJson = await _httpClientProvider.SendWithProxyRetryAsync(
                "steam-store",
                TimeSpan.FromSeconds(10),
                client => client.GetStringAsync(detailUrl));

            using var detailDoc = JsonDocument.Parse(detailJson);
            if (detailDoc.RootElement.TryGetProperty(firstAppId.ToString(), out var appData) &&
                appData.GetProperty("success").GetBoolean())
            {
                var data = appData.GetProperty("data");
                if (data.TryGetProperty("name", out var detailName))
                    return detailName.GetString();
            }
        }
        catch
        {
            // 静默失败，返回 null 就走英文回退
        }

        return null;
    }

    private async Task LoadCacheAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(_cachePath))
            {
                var json = await File.ReadAllTextAsync(_cachePath);
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (deserialized != null)
                {
                    _cache = new ConcurrentDictionary<string, string>(deserialized, StringComparer.OrdinalIgnoreCase);
                    return;
                }
            }
        }
        catch { }

        _cache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task SaveCacheAsync()
    {
        try
        {
            var dict = new Dictionary<string, string>(_cache!, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_cachePath, json);
        }
        catch { }
    }
}
