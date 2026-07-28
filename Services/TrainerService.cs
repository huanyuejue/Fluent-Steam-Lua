using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public class TrainerService : ITrainerService
{
    private readonly IHttpClientProvider _httpClientProvider;

    public TrainerService(IHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    public async Task<List<TrainerInfo>> GetHotTrainersAsync(int count = 10)
    {
        var result = new List<TrainerInfo>();
        var html = await FetchHomepageAsync();

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var items = doc.DocumentNode.SelectNodes("//div[contains(@class,'popular-posts')]//ul[contains(@class,'wpp-list')]/li");
        if (items == null) return result;

        foreach (var li in items.Take(count))
        {
            var trainer = ParseWppListItem(li);
            if (trainer != null) result.Add(trainer);
        }

        return result;
    }

    public async Task<List<TrainerInfo>> GetNewReleasesAsync(int count = 10)
    {
        var result = new List<TrainerInfo>();
        var html = await FetchHomepageAsync();

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var items = doc.DocumentNode.SelectNodes("//div[@id='rpwe_widget-4']//ul[contains(@class,'rpwe-ul')]/li");
        if (items == null) return result;

        foreach (var item in items.Take(count))
        {
            var titleLink = item.SelectSingleNode(".//h3[contains(@class,'rpwe-title')]/a");
            var imgNode = item.SelectSingleNode(".//a[contains(@class,'rpwe-img')]//img");

            var name = Decode(titleLink?.InnerText.Trim() ?? "");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var pageUrl = titleLink?.GetAttributeValue("href", "") ?? "";
            var coverUrl = imgNode?.GetAttributeValue("src", "") ?? "";

            result.Add(new TrainerInfo
            {
                GameName = StripTrainerSuffix(name),
                CoverUrl = coverUrl,
                PageUrl = pageUrl
            });
        }

        return result;
    }

    public async Task<List<TrainerInfo>> SearchTrainersAsync(string query)
    {
        var result = new List<TrainerInfo>();

        if (string.IsNullOrWhiteSpace(query)) return result;

        var url = $"https://flingtrainer.com/?s={Uri.EscapeDataString(query)}";
        var html = await _httpClientProvider.SendWithProxyRetryAsync(
            "trainer-search",
            TimeSpan.FromSeconds(15),
            client => client.GetStringAsync(url));

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var articles = doc.DocumentNode.SelectNodes("//article[contains(@class,'post-standard')]");
        if (articles == null) return result;

        foreach (var article in articles)
        {
            var titleNode = article.SelectSingleNode(".//h2[contains(@class,'post-title')]/a");
            var imgNode = article.SelectSingleNode(".//img[contains(@class,'wp-post-image')]");
            var dayNode = article.SelectSingleNode(".//div[contains(@class,'post-details-day')]");
            var monthNode = article.SelectSingleNode(".//div[contains(@class,'post-details-month')]");
            var yearNode = article.SelectSingleNode(".//div[contains(@class,'post-details-year')]");

            var name = Decode(titleNode?.InnerText.Trim() ?? "Unknown");
            var pageUrl = titleNode?.GetAttributeValue("href", "") ?? "";
            var coverUrl = imgNode?.GetAttributeValue("src", "") ?? "";

            var dateStr = "";
            if (dayNode != null && monthNode != null && yearNode != null)
                dateStr = $"{yearNode.InnerText.Trim()}.{GetMonthNumber(monthNode.InnerText.Trim())}.{dayNode.InnerText.Trim()}";

            result.Add(new TrainerInfo
            {
                GameName = StripTrainerSuffix(name),
                CoverUrl = coverUrl,
                PageUrl = pageUrl,
                UpdateDate = dateStr
            });

            if (result.Count >= 10) break;
        }

        return result;
    }

    public async Task<string?> GetDownloadUrlAsync(string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;

        try
        {
            var html = await _httpClientProvider.SendWithProxyRetryAsync(
                "trainer-page",
                TimeSpan.FromSeconds(15),
                client => client.GetStringAsync(pageUrl));

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var linkNode = doc.DocumentNode.SelectSingleNode("//a[contains(@class,'attachment-link')]");
            if (linkNode == null) return null;

            var href = linkNode.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href)) return null;

            return href;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> FetchHomepageAsync()
    {
        return await _httpClientProvider.SendWithProxyRetryAsync(
            "trainer-home",
            TimeSpan.FromSeconds(15),
            client => client.GetStringAsync("https://flingtrainer.com/"));
    }

    private static TrainerInfo? ParseWppListItem(HtmlNode li)
    {
        var titleLink = li.SelectSingleNode(".//a[contains(@class,'wpp-post-title')]");
        var imgNode = li.SelectSingleNode(".//img[contains(@class,'wpp-thumbnail')]");

        var name = Decode(titleLink?.InnerText.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var pageUrl = titleLink?.GetAttributeValue("href", "") ?? "";
        var coverUrl = imgNode?.GetAttributeValue("src", "") ?? "";

        return new TrainerInfo
        {
            GameName = StripTrainerSuffix(name),
            CoverUrl = coverUrl,
            PageUrl = pageUrl
        };
    }

    private static string Decode(string html) => WebUtility.HtmlDecode(html).Replace('\u2019', '\'').Replace('\u2018', '\'');

    private static string StripTrainerSuffix(string name)
    {
        var suffix = " Trainer";
        return name.EndsWith(suffix) ? name[..^suffix.Length] : name;
    }

    private static string GetMonthNumber(string month)
    {
        return month.ToLower() switch
        {
            "jan" => "01", "feb" => "02", "mar" => "03", "apr" => "04",
            "may" => "05", "jun" => "06", "jul" => "07", "aug" => "08",
            "sep" => "09", "oct" => "10", "nov" => "11", "dec" => "12",
            _ => month
        };
    }
}
