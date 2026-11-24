using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

// Upstream language sections take precedence. Bundled translations apply only
// to the exact reviewed source section, including when a release body is edited.
public static class ReleaseNotesLocalization
{
    private sealed record KnownTranslation(string Project, string Tag, string SourceDigest, string ResourceKey);
    private sealed record Heading(int Line, int Level, string Title, string? Language, bool IsNotes);

    private static readonly KnownTranslation[] KnownTranslations =
    {
        new("Fluent-Steam-Lua", "1.5.5.2", "ED3A87C72AB7987AC9EBC94C1300D2CF8806FCB6DF15B3B4B5154B38DBCB14F7", "ReleaseNotes_App_1_5_5_2"),
        new("OpenSteamTool", "1.5.5.4", "173869E2B24612E3AFD249A5C885F5F2008332D211FE82ABD7EA4405D1CB0D9A", "ReleaseNotes_Kernel_1_5_5_4")
    };

    public static string Select(string? body, string project, string? tag, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        language = LocalizationService.Normalize(language ?? LocalizationService.Instance.CurrentLanguage);
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var headings = new List<Heading>();
        for (var i = 0; i < lines.Length; i++)
        {
            var match = Regex.Match(lines[i].Trim(), @"^(#{1,6})\s+(.+?)\s*#*$");
            if (!match.Success) continue;
            var title = match.Groups[2].Value;
            headings.Add(new Heading(i, match.Groups[1].Length, title, HeadingLanguage(title), IsNotesHeading(title)));
        }

        foreach (var heading in headings.Where(h => h.Language == language))
        {
            // A bare English/中文 subheading is a language section only inside
            // the release notes, never an unrelated installation section.
            var parent = headings.LastOrDefault(h => h.Line < heading.Line && h.Level < heading.Level);
            if (heading.IsNotes || parent?.IsNotes == true
                || headings.All(h => !h.IsNotes) && Regex.IsMatch(heading.Title, @"^(?:English|EN|中文|Chinese|ZH)$", RegexOptions.IgnoreCase))
            {
                var section = Extract(lines, heading);
                if (!string.IsNullOrWhiteSpace(section)) return section;
            }
        }

        var generic = headings.FirstOrDefault(h => h.IsNotes);
        var original = generic == null ? body.Trim() : Extract(lines, generic);
        if (project.Equals("Fluent-Steam-Lua", StringComparison.OrdinalIgnoreCase)) original = TrimAppInstallationFooter(original);
        if (language != "en" || !ContainsChinese(original)) return original;

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeSource(original))));
        var known = KnownTranslations.FirstOrDefault(k => k.Project.Equals(project, StringComparison.OrdinalIgnoreCase)
            && k.Tag.Equals((tag ?? "").Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase)
            && k.SourceDigest == digest);
        if (known != null)
        {
            var translated = LocalizationService.Instance.GetStringForLanguage(known.ResourceKey, language);
            if (translated != known.ResourceKey) return translated;
        }
        // Preserve every source change and the caller's existing release link.
        // This notice does not claim that unknown upstream text was translated.
        return LocalizationService.Instance.GetStringForLanguage("ReleaseNotes_EnglishUnavailable", language) + "\n\n" + original;
    }

    private static string Extract(string[] lines, Heading heading)
    {
        var end = lines.Length;
        for (var i = heading.Line + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var match = Regex.Match(line, @"^(#{1,6})\s+");
            if (match.Success && match.Groups[1].Length <= heading.Level)
            { end = i; break; }
        }
        return string.Join('\n', lines[(heading.Line + 1)..end]).Trim();
    }

    private static string? HeadingLanguage(string title)
    {
        if (Regex.IsMatch(title, @"\b(?:english|en)\b|英文|英语", RegexOptions.IgnoreCase)) return "en";
        if (Regex.IsMatch(title, @"\b(?:chinese|zh|schinese)\b|中文|简体", RegexOptions.IgnoreCase)) return "zh";
        return null;
    }

    private static bool IsNotesHeading(string title) =>
        Regex.IsMatch(title, @"更新内容|更新日志|变更日志|\b(?:changelog|release notes|what['’]s new|updates|changes)\b", RegexOptions.IgnoreCase);

    private static string NormalizeSource(string source) =>
        string.Join('\n', source.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim())).Trim();

    private static bool ContainsChinese(string text) => text.Any(c => c is >= '\u3400' and <= '\u9fff');

    // The app's release template puts its .NET installation footer after a
    // separator. Ordinary separators inside release notes do not end a section.
    private static string TrimAppInstallationFooter(string source)
    {
        var footer = Regex.Match(source, @"(?m)^\s*-{3,}\s*\n\s*>\s*包体不包含运行所需依赖");
        return footer.Success ? source[..footer.Index].TrimEnd() : source;
    }
}
