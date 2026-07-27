using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public static class VdfHelper
{
    public static Dictionary<string, string> ParseDepotKeys(string vdfContent)
    {
        var keys = new Dictionary<string, string>();

        var start = vdfContent.IndexOf("\"depots\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return keys;

        start = vdfContent.IndexOf('{', start);
        if (start < 0) return keys;
        start++;

        var depth = 1;
        var end = start;
        while (end < vdfContent.Length && depth > 0)
        {
            if (vdfContent[end] == '{') depth++;
            else if (vdfContent[end] == '}') depth--;
            end++;
        }
        if (depth != 0) return keys;

        var section = vdfContent.Substring(start, end - start - 1);

        var pos = 0;
        while (pos < section.Length)
        {
            var numMatch = Regex.Match(section.Substring(pos), @"""(\d+)""");
            if (!numMatch.Success) break;

            var depotId = numMatch.Groups[1].Value;
            var afterNum = pos + numMatch.Index + numMatch.Length;

            // Check if followed by quoted value (flat format: "id" "key")
            var valMatch = Regex.Match(section.Substring(afterNum), @"^\s+""((?:[^""\\]|\\.)*)""");
            if (valMatch.Success && valMatch.Index == 0)
            {
                keys[depotId] = valMatch.Groups[1].Value;
                pos = afterNum + valMatch.Length;
                continue;
            }

            // Check if followed by block (nested format: "id" { "DecryptionKey" "key" })
            var braceIdx = section.IndexOf('{', afterNum);
            if (braceIdx < 0) break;

            depth = 1;
            var blockEnd = braceIdx + 1;
            while (blockEnd < section.Length && depth > 0)
            {
                if (section[blockEnd] == '{') depth++;
                else if (section[blockEnd] == '}') depth--;
                blockEnd++;
            }

            var blockContent = section.Substring(braceIdx + 1, blockEnd - braceIdx - 2);
            var keyMatch = Regex.Match(blockContent, @"""DecryptionKey""\s+""([^""]+)""");
            if (keyMatch.Success)
                keys[depotId] = keyMatch.Groups[1].Value;

            pos = blockEnd;
        }

        return keys;
    }
}
