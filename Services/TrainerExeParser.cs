using System.IO;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public static class TrainerExeParser
{
    public static (string gameName, List<CheatOption> options) Parse(string exePath)
    {
        var bytes = File.ReadAllBytes(exePath);
        var strings = ExtractUtf16Strings(bytes);
        var gameName = ExtractGameName(strings);
        var options = ExtractOptions(strings);
        return (gameName, options);
    }

    private static HashSet<string> ExtractUtf16Strings(byte[] bytes)
    {
        var result = new HashSet<string>();
        int n = bytes.Length / 2;

        for (int i = 0; i < n; i++)
        {
            int cp = bytes[i * 2] + bytes[i * 2 + 1] * 256;
            if (cp == 0) continue;

            bool okStart = IsValidStart(cp);
            if (!okStart) continue;

            var chars = new List<char>();
            while (i < n)
            {
                int c = bytes[i * 2] + bytes[i * 2 + 1] * 256;
                if (c == 0) break;
                if (!IsValidChar(c)) break;
                chars.Add((char)c);
                i++;
            }

            if (chars.Count >= 6)
                result.Add(new string(chars.ToArray()));
        }

        return result;
    }

    private static bool IsValidStart(int cp)
    {
        return (cp >= 0x20 && cp <= 0x7E)
            || (cp >= 0x4E00 && cp <= 0x9FFF)
            || (cp >= 0x3400 && cp <= 0x4DBF);
    }

    private static bool IsValidChar(int cp)
    {
        return (cp >= 0x20 && cp <= 0x7E)
            || (cp >= 0x4E00 && cp <= 0x9FFF)
            || (cp >= 0x3400 && cp <= 0x4DBF)
            || (cp >= 0x3000 && cp <= 0x303F)
            || (cp >= 0xFF00 && cp <= 0xFFEF)
            || cp == 0xB7;
    }

    private static string ExtractGameName(HashSet<string> strings)
    {
        // Find string containing "项修改器"
        var titleStr = strings
            .Where(s => s.Contains("项修改器"))
            .OrderByDescending(s => s.Length)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(titleStr)) return "";

        string? gameName = null;

        // Pattern 1: 《中文名》vX.X
        var m = Regex.Match(titleStr, @"《(.+?)》");
        if (m.Success) gameName = m.Groups[1].Value.Trim();

        // Pattern 2: 中文名（English）》vX.X
        if (string.IsNullOrEmpty(gameName))
        {
            m = Regex.Match(titleStr, @"^(.+?)[（(]");
            if (m.Success) gameName = m.Groups[1].Value.Trim();
        }

        // Pattern 3: anything before version number
        if (string.IsNullOrEmpty(gameName))
        {
            m = Regex.Match(titleStr, @"^(.+?)[vV]\d+\.\d+");
            if (m.Success) gameName = m.Groups[1].Value.Trim();
        }

        // Pattern 4: CJK chars only
        if (string.IsNullOrEmpty(gameName))
        {
            m = Regex.Match(titleStr, @"^([\u4e00-\u9fff\u3400-\u4dbf：，·]+)");
            if (m.Success) gameName = m.Groups[1].Value.Trim();
        }

        // Clean up common suffixes
        if (!string.IsNullOrEmpty(gameName))
        {
            gameName = Regex.Replace(gameName, @"[\d]+项修改器.*$", "");
            gameName = Regex.Replace(gameName, @" 项修改器.*$", "");
            gameName = Regex.Replace(gameName, @" Plus [\d]+ Trainer.*$", "");
            gameName = Regex.Replace(gameName, @" v[\d.]+.*$", "");
            gameName = Regex.Replace(gameName, @"[\d]+[ ]*[Tt]rainer.*$", "");
            gameName = gameName.Trim();
        }

        return gameName ?? "";
    }

    private static List<CheatOption> ExtractOptions(HashSet<string> strings)
    {
        var records = new List<(string key, string name)>();

        foreach (var s in strings)
        {
            var cleaned = s.Replace("\\n", " ").Replace("\\r", "");

            var m = Regex.Match(cleaned, @"^((?:Ctrl|Alt|Shift)\+)?(数字键|Num|F)\s*([\d+*./-]+)\s*-\s*(.+)$");
            if (!m.Success) continue;

            var mod = m.Groups[1].Value;
            var keyType = m.Groups[2].Value;
            var keyNum = m.Groups[3].Value;
            var rest = m.Groups[4].Value;

            // Build key display
            string keyDisplay = keyType switch
            {
                "数字键" => mod + "Num " + keyNum,
                "F" => mod + "F" + keyNum,
                _ => mod + keyType + " " + keyNum
            };

            // Remove HTML tags
            rest = Regex.Replace(rest, "<[^>]+>", "").Trim();

            // Extract Chinese name (remove --cmd suffix)
            var m2 = Regex.Match(rest, @"^(.+?)\s*--\S+");
            var chName = m2.Success ? m2.Groups[1].Value.Trim() : rest.Trim();

            // Skip if no CJK chars
            if (!HasCjk(chName)) continue;

            records.Add((keyDisplay, chName));
        }

        // Deduplicate: prefer modified version (Ctrl+/Alt+/Shift+) over plain
        var nameMap = new Dictionary<string, (string key, string name)>();
        foreach (var r in records)
        {
            bool hasMod = Regex.IsMatch(r.key, @"^(Ctrl|Alt|Shift)\+");
            if (nameMap.TryGetValue(r.name, out var existing))
            {
                bool existingHasMod = Regex.IsMatch(existing.key, @"^(Ctrl|Alt|Shift)\+");
                if (hasMod && !existingHasMod)
                    nameMap[r.name] = r;
            }
            else
            {
                nameMap[r.name] = r;
            }
        }

        // Sort: plain keys first, then combos; numeric order
        var numOrder = new Dictionary<string, string>
        {
            ["1"] = "0001", ["2"] = "0002", ["3"] = "0003", ["4"] = "0004",
            ["5"] = "0005", ["6"] = "0006", ["7"] = "0007", ["8"] = "0008",
            ["9"] = "0009", ["0"] = "0010", ["+"] = "0011", ["-"] = "0012",
            ["*"] = "0013", ["/"] = "0014", ["."] = "0015"
        };

        var sorted = nameMap.Values
            .OrderBy(r =>
            {
                bool hasMod = Regex.IsMatch(r.key, @"^(Ctrl|Alt|Shift)\+");
                var valMatch = Regex.Match(r.key, @"(?:Num |F)([\d+*./-]+)$");
                var val = valMatch.Success ? valMatch.Groups[1].Value : "";
                var order = numOrder.GetValueOrDefault(val, val.PadLeft(4, '0'));
                var prefix = Regex.Replace(r.key, @"[\d+*./-]+$", "");
                return $"{ (hasMod ? 1 : 0) }|{prefix}|{order}";
            })
            .ToList();

        var result = new List<CheatOption>();
        foreach (var r in sorted)
        {
            string? modifier = null;
            string keyName;
            var plusIdx = r.key.IndexOf('+');
            if (plusIdx > 0)
            {
                var possibleMod = r.key[..plusIdx];
                if (possibleMod is "Ctrl" or "Alt" or "Shift")
                {
                    modifier = possibleMod;
                    keyName = r.key[(plusIdx + 1)..];
                }
                else
                {
                    keyName = r.key;
                }
            }
            else
            {
                keyName = r.key;
            }

            result.Add(new CheatOption
            {
                Modifiers = modifier ?? "",
                KeyName = keyName,
                Description = r.name
            });
        }

        return result;
    }

    private static bool HasCjk(string text)
    {
        return text.Any(c =>
        {
            int cp = (int)c;
            return (cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3400 && cp <= 0x4DBF);
        });
    }
}
