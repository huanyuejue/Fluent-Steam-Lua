using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public static class TrainerExeParser
{
    private const long MaxFileSizeBytes = 100L * 1024 * 1024;

    public static (string gameName, List<CheatOption> options) Parse(string exePath)
    {
        var metadata = ReadMetadata(exePath);
        return (metadata.DisplayGameName, metadata.Options);
    }

    // Read embedded text as data; trainer executables are never loaded or run.
    public static TrainerMetadata ReadMetadata(string exePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var fileInfo = new FileInfo(exePath);
        var metadata = new TrainerMetadata
        {
            SourceLength = fileInfo.Length,
            SourceWriteTimeUtcTicks = fileInfo.LastWriteTimeUtc.Ticks
        };
        if (fileInfo.Length > MaxFileSizeBytes)
        {
            LogService.Warn(L.Get("LogCat_Trainer"), L.Get("Svc_TlaFileTooLarge", exePath, fileInfo.Length / (1024 * 1024)));
            return metadata;
        }

        var strings = ExtractStrings(File.ReadAllBytes(exePath), ct);
        metadata.ChineseGameName = ExtractChineseGameName(strings);
        metadata.EnglishGameName = ExtractEnglishGameName(strings);
        metadata.OriginalGameName = string.IsNullOrEmpty(metadata.ChineseGameName)
            ? metadata.EnglishGameName : metadata.ChineseGameName;
        metadata.Options = ExtractOptions(strings, ct);
        return metadata;
    }

    private static HashSet<string> ExtractStrings(byte[] bytes, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        // Resource strings can start on either byte alignment.
        for (var alignment = 0; alignment < 2; alignment++)
        {
            var text = new StringBuilder();
            for (var offset = alignment; offset + 1 < bytes.Length; offset += 2)
            {
                if ((offset & 0xffff) == alignment) ct.ThrowIfCancellationRequested();
                var c = (char)(bytes[offset] | bytes[offset + 1] << 8);
                if (IsTextCharacter(c)) text.Append(c);
                else FlushText(text, result);
            }
            FlushText(text, result);
        }
        // Some native trainers store their English labels as ASCII.
        var ascii = new StringBuilder();
        for (var i = 0; i < bytes.Length; i++)
        {
            if ((i & 0xffff) == 0) ct.ThrowIfCancellationRequested();
            var b = bytes[i];
            if (b is >= 0x20 and <= 0x7e || b is 10 or 13) ascii.Append((char)b);
            else FlushText(ascii, result);
        }
        FlushText(ascii, result);
        return result;
    }

    private static void FlushText(StringBuilder text, HashSet<string> strings)
    {
        if (text.Length >= 6) strings.Add(text.ToString());
        text.Clear();
    }

    private static bool IsTextCharacter(char c) =>
        c is >= ' ' and <= '~' or '\n' or '\r' or '\t'
            or >= '\u3400' and <= '\u9fff'
            or >= '\u3000' and <= '\u303f'
            or >= '\uff00' and <= '\uffef'
            or >= '\u00a0' and <= '\u024f'
            or >= '\u2010' and <= '\u2026' or '\u2122';

    private static string ExtractChineseGameName(IEnumerable<string> strings)
    {
        var title = strings.Where(s => s.Contains("项修改器", StringComparison.Ordinal))
            .OrderByDescending(s => s.Length).FirstOrDefault();
        if (title == null) return "";
        var bracketed = Regex.Match(title, @"《(.+?)》");
        var name = bracketed.Success ? bracketed.Groups[1].Value.Trim() : title;
        name = Regex.Split(name, @"[（(]|\s*[vV]\d+\.\d+|\d+\s*项修改器")[0].Trim();
        return HasChinese(name) ? name : "";
    }

    private static string ExtractEnglishGameName(IEnumerable<string> strings)
    {
        foreach (var title in strings.Where(s => !HasChinese(s) && s.Contains("Trainer", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(s => Regex.IsMatch(s, @"\b[vV]\d|\bPlus\s+\d"))
                     .ThenByDescending(s => s.Length))
        {
            var match = Regex.Match(title.Trim(), @"^(?<name>.+?)(?:\s+[vV]\d|\s+Plus\s+\d+|\s+\+\d+|\s+Trainer\b)", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim();
            if (name.Equals("FLiNG", StringComparison.OrdinalIgnoreCase) || name.Contains('\n') || name.Contains('\r')) continue;
            return name;
        }
        // Bilingual titles sometimes embed the English name in parentheses
        // instead of storing a separate English window title.
        foreach (var title in strings.Where(s => s.Contains("项修改器", StringComparison.Ordinal)))
            foreach (Match match in Regex.Matches(title, @"[（(]([^）)]+)[）)]"))
            {
                var name = match.Groups[1].Value.Trim();
                if (!HasChinese(name) && name.Any(char.IsLetter) && !Regex.IsMatch(name, @"^[vV]?\d")) return name;
            }
        return "";
    }

    private static List<CheatOption> ExtractOptions(IEnumerable<string> strings, CancellationToken ct)
    {
        var byKey = new Dictionary<string, CheatOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in strings)
        {
            ct.ThrowIfCancellationRequested();
            var cleaned = Regex.Replace(text.Replace("\\n", "\n").Replace("\\r", ""), @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            foreach (var line in cleaned.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Regex.Match(line.Trim(), @"^(?<mod>(?:Ctrl|Alt|Shift)\+)?(?<type>数字键|Num(?:pad)?|F)\s*(?<num>[\d+*./-]+)\s*[-–—]\s*(?<desc>.+)$", RegexOptions.IgnoreCase);
                if (!match.Success) continue;
                var mod = string.Join('+', match.Groups["mod"].Value.TrimEnd('+')
                    .Split('+', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ? "Ctrl"
                        : part.Equals("alt", StringComparison.OrdinalIgnoreCase) ? "Alt" : "Shift"));
                var type = match.Groups["type"].Value.Equals("F", StringComparison.OrdinalIgnoreCase) ? "F" : "Num ";
                var key = type + match.Groups["num"].Value;
                var fullKey = string.IsNullOrEmpty(mod) ? key : mod + "+" + key;
                var description = WebUtility.HtmlDecode(Regex.Replace(match.Groups["desc"].Value, "<[^>]+>", ""));
                description = Regex.Replace(description, @"\s+--\S+.*$", "").Trim();
                if (string.IsNullOrWhiteSpace(description)) continue;
                if (!byKey.TryGetValue(fullKey, out var option))
                    byKey[fullKey] = option = new CheatOption { Modifiers = mod, KeyName = key, Description = description };
                if (HasChinese(description))
                {
                    if (string.IsNullOrEmpty(option.ChineseDescription)) option.ChineseDescription = description;
                }
                else if (string.IsNullOrEmpty(option.EnglishDescription)) option.EnglishDescription = description;
            }
        }
        // Pair by hotkey, retaining separate actions even when descriptions match.
        return byKey.Values.OrderBy(o => string.IsNullOrEmpty(o.Modifiers) ? 0 : 1)
            .ThenBy(o => o.Modifiers, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.KeyName.StartsWith("F", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(o => KeyOrder(o.KeyName)).ToList();
    }

    private static int KeyOrder(string key)
    {
        var value = key.StartsWith("Num ", StringComparison.OrdinalIgnoreCase) ? key[4..] : key[1..];
        return int.TryParse(value, out var number) ? number == 0 ? 10 : number : value.Length == 1 ? 100 + value[0] : 999;
    }

    public static bool HasChinese(string? text) => text?.Any(c => c is >= '\u3400' and <= '\u9fff') == true;
}
