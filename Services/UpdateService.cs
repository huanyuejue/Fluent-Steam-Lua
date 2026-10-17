using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public sealed record UpdateCheckResult(bool HasUpdate, Version CurrentVersion, Version LatestVersion, string TagName, string ReleaseUrl, string ReleaseNotes, string LooseAssetUrl, string SingleAssetUrl);

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
            HttpHeaderHelper.ConfigureApp);

        using var doc = JsonDocument.Parse(json);
        var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
        var releaseUrl = doc.RootElement.GetProperty("html_url").GetString()
            ?? $"{ProjectUrl}/releases/latest";
        var releaseNotes = ExtractUpdateContent(
            doc.RootElement.TryGetProperty("body", out var bodyElem)
                ? bodyElem.GetString() ?? string.Empty
                : string.Empty);
        var (looseAssetUrl, singleAssetUrl) = ExtractAssetUrls(doc.RootElement);

        var (latestNumeric, latestSuffix) = ParseReleaseVersion(tagName)
            ?? throw new InvalidOperationException(L.Get("Svc_UpdBadTag", tagName));
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        // Revision 纳入比较：否则装着 1.4.8.1 会永远提示更新；-1 兜底（理论上到不了，实测为 0）
        var currentNumeric = new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build,
            assemblyVersion.Revision < 0 ? 0 : assemblyVersion.Revision);
        // 当前后缀取 InformationalVersion（如 1.4.8-fix+hash 取 fix），纯数字版为空
        var currentSuffix = ReleaseSuffix(
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "");

        var hasUpdate = CompareRelease((latestNumeric, latestSuffix), (currentNumeric, currentSuffix)) > 0;
        return new UpdateCheckResult(hasUpdate, currentNumeric, latestNumeric, tagName, releaseUrl, releaseNotes, looseAssetUrl, singleAssetUrl);
    }

    // 按包名匹配散文件与单文件包的下载地址：新规范后缀优先命中，
    // 老命名（loose-file.*.zip）当前缀兜底，匹配不到留空由调用方回退手动更新
    private static (string Loose, string Single) ExtractAssetUrls(JsonElement root)
    {
        var loose = string.Empty;
        var single = string.Empty;
        try
        {
            if (!root.TryGetProperty("assets", out var assets)) return (loose, single);
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElem)
                    ? nameElem.GetString() ?? string.Empty
                    : string.Empty;
                var url = asset.TryGetProperty("browser_download_url", out var urlElem)
                    ? urlElem.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrEmpty(url)) continue;
                if (name.EndsWith("-loose-file.zip", StringComparison.OrdinalIgnoreCase))
                    loose = url;
                else if (name.EndsWith("-single-file.zip", StringComparison.OrdinalIgnoreCase))
                    single = url;
                else if (string.IsNullOrEmpty(loose)
                    && name.StartsWith("loose-file", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    loose = url;
                else if (string.IsNullOrEmpty(single)
                    && name.StartsWith("single-file", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    single = url;
            }
        }
        catch { }
        return (loose, single);
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



    // 支持 x.y.z[.w][-后缀]；数字部分参与比较，后缀只定性不定量
    private static (Version Numeric, string Suffix)? ParseReleaseVersion(string tagName)
    {
        var text = tagName.Trim().TrimStart('v', 'V');
        string suffix = "";
        var dash = text.IndexOf('-');
        var numeric = text;
        if (dash >= 0)
        {
            numeric = text[..dash];
            suffix = ReleaseSuffix(text);
        }
        if (!Version.TryParse(numeric, out var version)) return null;
        return (version, suffix);
    }

    // 取第一个 '-' 后的发布后缀，'+' 及之后（commit 哈希等构建元数据）丢弃
    private static string ReleaseSuffix(string versionText)
    {
        var dash = versionText.IndexOf('-');
        if (dash < 0) return "";
        var suffix = versionText[(dash + 1)..];
        var plus = suffix.IndexOf('+');
        return plus >= 0 ? suffix[..plus] : suffix;
    }

    // 数字优先；数字相同则无后缀 < 有后缀（fix 包视为更新），都有后缀按字典序
    private static int CompareRelease((Version Numeric, string Suffix) a, (Version Numeric, string Suffix) b)
    {
        var c = a.Numeric.CompareTo(b.Numeric);
        if (c != 0) return c;
        if (a.Suffix == b.Suffix) return 0;
        if (a.Suffix.Length == 0) return -1;
        if (b.Suffix.Length == 0) return 1;
        return string.Compare(a.Suffix, b.Suffix, StringComparison.Ordinal);
    }
}
