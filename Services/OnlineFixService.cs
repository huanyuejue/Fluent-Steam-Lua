using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

// OnlineFix 补丁数据源：online-fix.me（DLE 站，无 JSON 接口，全走 HTML 解析）。
// 实测结论：搜索/文章页/目录索引均无需登录；文件下载必须同一会话先 GET 目录页再带 Referer 下文件；
// 请求间隔至少 3 秒否则吃 401。页面编码 windows-1251，必须转码后解析。
public sealed class OnlineFixService : IOnlineFixService, IDisposable
{
    private const string SiteRoot = "https://online-fix.me";
    private static readonly TimeSpan MinRequestGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(60);
    private const int BufferSize = 81920;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _paceGate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;
    private bool _disposed;

    static OnlineFixService()
    {
        // windows-1251 在 .NET Core 默认没有，不注册直接 GetEncoding 会抛
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public OnlineFixService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = HttpTimeout };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        _paceGate.Dispose();
    }

    // ---- 搜索：站内搜索区分大小写，依次试原词/小写/大写三路合并去重，保证大小写随便输 ----

    public async Task<IReadOnlyList<OnlineFixSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<OnlineFixSearchResult>();
        var q = query.Trim();
        var variants = new[] { q, q.ToLowerInvariant(), q.ToUpperInvariant() }.Distinct().ToList();
        var merged = new Dictionary<string, OnlineFixSearchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in variants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var r in await SearchOnceAsync(v, ct).ConfigureAwait(false))
                    merged.TryAdd(r.Url, r);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Warn(L.Get("LogCat_Online"), L.Get("Svc_OfParseClassFailed", v, ex.Message));
            }
        }
        return merged.Values.ToList();
    }

    private async Task<IReadOnlyList<OnlineFixSearchResult>> SearchOnceAsync(string query, CancellationToken ct)
    {
        var url = $"{SiteRoot}/index.php?do=search&subaction=search&story={Uri.EscapeDataString(query.Trim())}";
        var doc = await GetDocumentAsync(url, ct).ConfigureAwait(false);
        var list = new List<OnlineFixSearchResult>();
        var articles = doc.DocumentNode.SelectNodes("//div[contains(@class,'news-search')]//a[@class='big-link']");
        if (articles == null) return list;
        foreach (var a in articles)
        {
            try
            {
                var href = HtmlEntity.DeEntitize(a.GetAttributeValue("href", ""));
                if (string.IsNullOrWhiteSpace(href)) continue;
                var article = a.ParentNode;
                var title = HtmlEntity.DeEntitize(
                    article.SelectSingleNode(".//h2[@class='title']")?.InnerText.Trim() ?? "").Trim();
                if (string.IsNullOrEmpty(title)) continue;
                var tablet = HtmlEntity.DeEntitize(
                    article.SelectSingleNode(".//div[@class='preview-text']//a[contains(@href,'/files/progs/') or contains(@href,'/programs/')]")?.InnerText.Trim() ?? "");
                var version = HtmlEntity.DeEntitize(
                    article.SelectSingleNode(".//div[@class='edit']")?.InnerText.Trim() ?? "");
                var updated = article.SelectSingleNode(".//time[@datetime]")?.GetAttributeValue("datetime", "");
                list.Add(new OnlineFixSearchResult(title, ToAbsolute(href),
                    string.IsNullOrWhiteSpace(version) ? null : version,
                    string.IsNullOrWhiteSpace(tablet) ? null : tablet,
                    string.IsNullOrWhiteSpace(updated) ? null : updated));
            }
            catch { }
        }
        return list;
    }

    // ---- 详情 ----

    public async Task<OnlineFixDetail?> GetDetailAsync(string articleUrl, CancellationToken ct = default)
    {
        OnlineFixDetail? head = null;
        try
        {
            var doc = await GetDocumentAsync(articleUrl, ct).ConfigureAwait(false);
            var title = HtmlEntity.DeEntitize(
                doc.DocumentNode.SelectSingleNode("//h1[@id='news-title']")?.InnerText.Trim() ?? "").Trim();
            if (string.IsNullOrEmpty(title)) return null;
            var version = HtmlEntity.DeEntitize(
                doc.DocumentNode.SelectSingleNode("//div[contains(@class,'lightedited')]")?.InnerText.Trim() ?? "");
            // 详情页来源链接直接在正文里（不在 preview-text 容器内），全文档取第一个
            var tablet = HtmlEntity.DeEntitize(
                doc.DocumentNode.SelectSingleNode("//a[contains(@href,'/files/progs/') or contains(@href,'/programs/')]")?.InnerText.Trim() ?? "");
            // dateModified 是 InnerText 不是 content 属性；time 取第一个（发布时间）
            var updated = doc.DocumentNode.SelectSingleNode("//span[@itemprop='dateModified']")?.InnerText.Trim();
            if (string.IsNullOrWhiteSpace(updated))
                updated = doc.DocumentNode.SelectSingleNode("//time[@datetime]")?.GetAttributeValue("datetime", "");
            var buttons = doc.DocumentNode.SelectNodes("//div[@class='quote']//a[contains(@href,'online-fix.me:2053')]");
            string uploadsBase = "";
            if (buttons != null)
            {
                foreach (var b in buttons)
                {
                    var href = HtmlEntity.DeEntitize(b.GetAttributeValue("href", ""));
                    // 只取官方直链目录，torrent/hosters/drive 不进详情
                    if (href.Contains("/uploads/", StringComparison.OrdinalIgnoreCase)
                        && !href.Contains("/torrents/", StringComparison.OrdinalIgnoreCase))
                    {
                        uploadsBase = href;
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(uploadsBase)) return null;
            if (!uploadsBase.EndsWith('/')) uploadsBase += "/";
            var dirName = SanitizeDirName(DirNameFromUrl(uploadsBase));
            if (string.IsNullOrEmpty(dirName)) dirName = SanitizeDirName(title);
            var body = doc.DocumentNode.SelectSingleNode("//div[@itemprop='articleBody']");
            var instructions = ExtractInstructions(TextWithBreaks(body));
            var files = await ListFilesAsync(uploadsBase + "Fix%20Repair/", ct).ConfigureAwait(false);
            head = new OnlineFixDetail(title, articleUrl, dirName, uploadsBase,
                string.IsNullOrWhiteSpace(version) ? null : version,
                string.IsNullOrWhiteSpace(tablet) ? null : tablet,
                string.IsNullOrWhiteSpace(updated) ? null : updated,
                instructions, files);
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Online"), L.Get("Svc_OfArticleParseFailed", articleUrl, ex.Message));
        }
        return head;
    }

    // 正文取文本：InnerText 会把 <br> 直接压掉导致步骤全挤在一行，先换成换行符；
    // 块级标签前后补换行，连续空行压成一个
    private static string TextWithBreaks(HtmlNode? node)
    {
        if (node == null) return string.Empty;
        var sb = new StringBuilder();
        AppendTextWithBreaks(node, sb);
        var text = sb.ToString();
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n\s*\n\s*\n+", "\n\n");
        return text.Trim();
    }

    private static void AppendTextWithBreaks(HtmlNode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                sb.Append(HtmlEntity.DeEntitize(child.InnerText));
            }
            else if (child.Name.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append('\n');
            }
            else
            {
                var isBlock = child.Name.Equals("p", StringComparison.OrdinalIgnoreCase)
                    || child.Name.Equals("div", StringComparison.OrdinalIgnoreCase)
                    || child.Name.Equals("li", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(child.Name, @"^h\d$", RegexOptions.IgnoreCase);
                if (isBlock) sb.Append('\n');
                AppendTextWithBreaks(child, sb);
                if (isBlock) sb.Append('\n');
            }
        }
    }

    // 使用说明：正文找 Как запускать / Как играть 标题，取其后到下一个同级标题前的内容；
    // 标题五花八门，找不到就回退正文前 6000 字，总长度截断 8000
    private static string ExtractInstructions(string bodyText)
    {
        var text = HtmlEntity.DeEntitize(bodyText).Trim();
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var markers = new[] { "Как запускать", "Как играть" };
        var start = -1;
        foreach (var m in markers)
        {
            start = text.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (start >= 0) break;
        }
        if (start < 0)
            return text.Length <= 6000 ? text : text[..6000];
        var ends = new[] { "Кооператив", "Мультиплеер", "Достижения", "Примечание", "Важно:", "Дополнительно" };
        var end = text.Length;
        foreach (var m in ends)
        {
            var i = text.IndexOf(m, start + 5, StringComparison.OrdinalIgnoreCase);
            if (i > start && i < end) end = i;
        }
        var section = text.Substring(start, Math.Min(end, text.Length) - start).Trim();
        return section.Length <= 8000 ? section : section[..8000];
    }

    // ---- 目录文件列表 ----

    public async Task<IReadOnlyList<OnlineFixFileEntry>> ListFilesAsync(string dirUrl, CancellationToken ct = default)
    {
        var list = new List<OnlineFixFileEntry>();
        try
        {
            if (!dirUrl.EndsWith('/')) dirUrl += "/";
            var doc = await GetDocumentAsync(dirUrl, ct).ConfigureAwait(false);
            var links = doc.DocumentNode.SelectNodes("//pre/a[@href]");
            if (links == null) return list;
            foreach (var a in links)
            {
                var href = a.GetAttributeValue("href", "");
                if (string.IsNullOrEmpty(href) || href == "../") continue;
                var name = Uri.UnescapeDataString(href.TrimEnd('/'));
                var tail = HtmlEntity.DeEntitize(a.NextSibling?.InnerText ?? "").Trim();
                var m = Regex.Match(tail, @"(\d{2}-[A-Za-z]{3}-\d{4}\s+\d{2}:\d{2})\s+([\d.]+[KMG]?)");
                list.Add(new OnlineFixFileEntry(name, dirUrl + href,
                    m.Success ? m.Groups[2].Value : null,
                    m.Success ? m.Groups[1].Value : null));
            }
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Online"), L.Get("Svc_OfDirFailed", dirUrl, ex.Message));
        }
        return list;
    }

    // ---- 文件下载：同一会话先 GET 目录页，再带 Referer 下文件；失败抛错由调用方提示 ----

    public async Task DownloadFileAsync(string fileUrl, string refererDirUrl, string destPath,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await PaceAsync(ct).ConfigureAwait(false);
        try
        {
            // 预热会话：目录页本身不重要，失败也不拦，真失败在文件请求时报出
            using var warm = await _http.GetAsync(refererDirUrl, ct).ConfigureAwait(false);
        }
        catch { }
        await PaceAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);
        request.Headers.Referrer = new Uri(refererDirUrl);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(L.Get("Svc_OfDownloadHttpFailed", (int)response.StatusCode));
        var total = response.Content.Headers.ContentLength ?? -1;
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
        var buffer = new byte[BufferSize];
        long done = 0;
        int read;
        while ((read = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (total > 0) progress?.Report(Math.Round((double)done / total * 100, 1));
        }
        await fileStream.FlushAsync(ct).ConfigureAwait(false);
    }

    // ---- 内部 ----

    private async Task<HtmlDocument> GetDocumentAsync(string url, CancellationToken ct)
    {
        await PaceAsync(ct).ConfigureAwait(false);
        var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        // 站点 windows-1251，不转码直接解析全是乱码
        var html = Encoding.GetEncoding("windows-1251").GetString(bytes);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    // 请求限速：站有 CF + 目录服限频，相邻请求至少间隔 3 秒
    private async Task PaceAsync(CancellationToken ct)
    {
        await _paceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = MinRequestGap - (DateTime.UtcNow - _lastRequestUtc);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _paceGate.Release();
        }
    }

    private static string ToAbsolute(string href)
        => href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : SiteRoot + href;

    private static string DirNameFromUrl(string url)
    {
        try { return Uri.UnescapeDataString(new Uri(url).Segments.Last().Trim('/')); }
        catch { return string.Empty; }
    }

    private static string SanitizeDirName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "unknown" : clean;
    }
}
