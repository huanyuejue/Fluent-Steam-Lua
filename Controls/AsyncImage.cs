using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamLuaManager.Services;

namespace SteamLuaManager.Controls;

/// <summary>
/// 异步加载远程图片的 Image：绑定 SourceUrl 后由调用方触发 BeginLoad()（懒加载），
/// 仅内存缓存避免重复请求；下载完成后回 UI 线程设置 Source；失败保持空白。
/// URL 链以 "|" 分隔，支持三种候选：
///   file:// 本地文件（已落盘封面）；storeapi://{appId} 标记触发 Store API 解析真实 URL；
///   其余按普通 HTTP 下载。
/// </summary>
public class AsyncImage : Image
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        // 带浏览器指纹头，避免 CDN 将空 User-Agent 请求判为 bot 而限速/下发挑战页
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        return client;
    }

    private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new();
    private static readonly SemaphoreSlim Throttle = new(4);

    private string _pendingUrl = "";
    private bool _started;

    public static readonly DependencyProperty SourceUrlProperty = DependencyProperty.Register(
        nameof(SourceUrl), typeof(string), typeof(AsyncImage), new PropertyMetadata("", OnSourceUrlChanged));

    public string SourceUrl
    {
        get => (string)GetValue(SourceUrlProperty);
        set => SetValue(SourceUrlProperty, value);
    }

    /// <summary>为 true 时 SourceUrl 变化不立即下载，等待 BeginLoad() 手动触发（视口懒加载场景）。</summary>
    public static readonly DependencyProperty DeferLoadingProperty = DependencyProperty.Register(
        nameof(DeferLoading), typeof(bool), typeof(AsyncImage), new PropertyMetadata(false));

    public bool DeferLoading
    {
        get => (bool)GetValue(DeferLoadingProperty);
        set => SetValue(DeferLoadingProperty, value);
    }

    private static void OnSourceUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var image = (AsyncImage)d;
        // URL 变化（如列表重排后容器复用）时允许重新加载
        image._started = false;
        image._pendingUrl = e.NewValue as string ?? "";
        if (image.DeferLoading)
        {
            image.Source = null;
            return;
        }

        // XAML 按声明顺序应用属性：SourceUrl 可能先于 DeferLoading 设置，
        // 延迟一个调度周期确认属性全部应用后再决定是否立即加载。
        // 注意：本回调先于 BeginLoad 执行，若 BeginLoad 已触发（缓存命中/下载中），
        // 不能再清空 Source，否则会覆盖刚设置好的封面（刷新后必现的竞态）。
        _ = image.Dispatcher.InvokeAsync(() =>
        {
            if (image.DeferLoading)
            {
                if (!image._started)
                {
                    image.Source = null;
                }
                return;
            }
            image.BeginLoad();
        });
    }

    public void BeginLoad()
    {
        if (_started) return;
        _started = true;

        var url = _pendingUrl;
        if (string.IsNullOrEmpty(url)) return;

        if (Cache.TryGetValue(url, out var cached))
        {
            Source = cached;
            return;
        }

        var dispatcher = Dispatcher;
        _ = LoadCoreAsync(url, dispatcher, this);
    }

    private static async Task LoadCoreAsync(string url, Dispatcher dispatcher, AsyncImage image)
    {
        var urls = url.Split('|', StringSplitOptions.RemoveEmptyEntries);
        BitmapImage? bitmap = null;

        await Throttle.WaitAsync();
        try
        {
            foreach (var candidate in urls)
            {
                if (candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    bitmap = TryLoadLocalFile(candidate);
                    if (bitmap != null) break;
                    continue;
                }
                if (candidate.StartsWith("storeapi://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    var bytes = await Http.GetByteArrayAsync(candidate.Trim()).ConfigureAwait(false);
                    bitmap = CreateBitmap(bytes);
                    if (bitmap != null) break;
                }
                catch (Exception)
                {
                    // 尝试下一个候选
                }
            }

            if (bitmap == null)
            {
                // 模板 URL 全部失败（新游戏已迁移到带哈希的新 CDN 结构）→ 经 Store API 解析真实 header URL 再试一次
                var fallbackUrl = await TryResolveStoreApiFallback(url);
                if (!string.IsNullOrEmpty(fallbackUrl))
                {
                    try
                    {
                        var bytes = await Http.GetByteArrayAsync(fallbackUrl).ConfigureAwait(false);
                        bitmap = CreateBitmap(bytes);
                    }
                    catch (Exception)
                    {
                        // 兜底失败，保持空白
                    }
                }
            }
        }
        finally
        {
            Throttle.Release();
        }

        if (bitmap == null)
        {
            return;
        }

        Cache[url] = bitmap;
        await dispatcher.InvokeAsync(() =>
        {
            if (image.SourceUrl == url)
            {
                image.Source = bitmap;
            }
        });
    }

    private static BitmapImage? CreateBitmap(byte[] bytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BitmapImage? TryLoadLocalFile(string candidate)
    {
        try
        {
            var path = new Uri(candidate).LocalPath;
            if (!File.Exists(path)) return null;
            return CreateBitmap(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>URL 链含 storeapi://{appId} 标记时，经服务实时解析真实封面 URL。</summary>
    private static async Task<string?> TryResolveStoreApiFallback(string url)
    {
        var marker = Array.Find(url.Split('|'), u => u.StartsWith("storeapi://", StringComparison.OrdinalIgnoreCase));
        if (marker == null || !int.TryParse(marker["storeapi://".Length..], out var appId))
        {
            return null;
        }
        if (_storeApi == null)
        {
            _storeApi = SteamLuaManager.App.ServiceProvider?.GetService(typeof(ISteamApiService)) as ISteamApiService;
        }
        if (_storeApi == null) return null;
        return await _storeApi.ResolveHeaderUrlAsync(appId);
    }

    private static ISteamApiService? _storeApi;
}