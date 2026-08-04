using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SteamLuaManager.Controls;

/// <summary>
/// 异步加载远程图片的 Image：绑定 SourceUrl 后由调用方触发 BeginLoad()（懒加载），
/// 仅内存缓存避免重复请求；下载完成后回 UI 线程设置 Source；失败保持空白。
/// 支持 "url1|url2" 备用链。
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
        // 延迟一个调度周期确认属性全部应用后再决定是否立即加载
        _ = image.Dispatcher.InvokeAsync(() =>
        {
            if (image.DeferLoading)
            {
                image.Source = null;
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
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

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
                try
                {
                    var bytes = await Http.GetByteArrayAsync(candidate.Trim()).ConfigureAwait(false);
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = new MemoryStream(bytes);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    break;
                }
                catch
                {
                    // 尝试下一个备用地址
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
}
