using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public sealed record UpdateCheckResult(bool HasUpdate, Version CurrentVersion, Version LatestVersion, string TagName, string ReleaseUrl, string ReleaseNotes);

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default);
}

public class UpdateService : IUpdateService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private const string ProjectUrl = "https://github.com/huanyuejue/Fluent-Steam-Lua";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/huanyuejue/Fluent-Steam-Lua/releases/latest";

    public UpdateService(IHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var json = await _httpClientProvider.SendWithProxyRetryAsync(
            "app-update-check",
            TimeSpan.FromSeconds(20),
            client => client.GetStringAsync(LatestReleaseApiUrl, ct),
            ConfigureHeaders);

        using var doc = JsonDocument.Parse(json);
        var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        var releaseUrl = doc.RootElement.GetProperty("html_url").GetString()
            ?? $"{ProjectUrl}/releases/latest";
        var releaseNotes = ExtractUpdateContent(
            doc.RootElement.TryGetProperty("body", out var bodyElem)
                ? bodyElem.GetString() ?? string.Empty
                : string.Empty);

        var latestVersion = ParseReleaseVersion(tagName)
            ?? throw new InvalidOperationException($"无法识别最新版本号：{tagName}");
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        var currentVersion = new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);

        return new UpdateCheckResult(latestVersion > currentVersion, currentVersion, latestVersion, tagName, releaseUrl, releaseNotes);
    }

    // 仅提取 "## 更新内容" 到首个分隔线之间的区域，跳过标题行
    private static string ExtractUpdateContent(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        var lines = body.Replace("\r\n", "\n").Split('\n');

        var startIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Equals("## 更新内容", StringComparison.OrdinalIgnoreCase))
            {
                startIndex = i + 1;
                break;
            }
        }

        if (startIndex < 0) return FormatPlainText(body);

        var endIndex = lines.Length;
        for (int i = startIndex; i < lines.Length; i++)
        {
            if (IsHorizontalRule(lines[i]))
            {
                endIndex = i;
                break;
            }
        }

        return FormatPlainText(string.Join('\n', lines[startIndex..endIndex]));
    }

    // 去除 Markdown 标记，仅保留文字；列表项 "- / *" 转为 "•"
    private static string FormatPlainText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                sb.AppendLine();
                continue;
            }

            // 标题行 ### xxx → xxx
            if (line[0] == '#')
            {
                var title = line.TrimStart('#').Trim();
                if (title.Length > 0)
                    sb.AppendLine(title);
                continue;
            }

            // 引用块 > xxx → xxx
            if (line[0] == '>')
            {
                line = line.TrimStart('>').Trim();
                if (line.Length == 0) continue;
            }

            // 列表项 - xxx / * xxx → • xxx
            if (line.Length > 1 && line[1] == ' ' && (line[0] == '-' || line[0] == '*'))
                line = "• " + line[2..].Trim();
            else if (line is "-" or "*" or "+")
                line = "•";

            sb.AppendLine(StripInlineMarkup(line));
        }
        return sb.ToString().TrimEnd();
    }

    // 清理行内标记：[文字](url)→文字、**文字**→文字、`文字`→文字
    private static string StripInlineMarkup(string line)
    {
        line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");
        line = line.Replace("**", "").Replace("__", "");
        line = line.Replace("`", "");
        return line;
    }

    private static bool IsHorizontalRule(string line)
    {
        var t = line.Trim();
        if (t.Length < 3) return false;
        var c = t[0];
        if (c != '-' && c != '*' && c != '_') return false;
        foreach (var ch in t)
            if (ch != c) return false;
        return true;
    }

    private static void ConfigureHeaders(HttpClient client)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FluentSteamLuaManager/1.0");
    }

    private static Version? ParseReleaseVersion(string tagName)
    {
        var versionText = tagName.Trim().TrimStart('v', 'V');
        return Version.TryParse(versionText, out var version) ? version : null;
    }
}
