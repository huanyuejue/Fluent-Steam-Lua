using System.Text;

namespace SteamLuaManager.Services;

// 我从直提 demo 里搬过来的最小 VDF 文本解析器：引号串（含 \" \\ 转义）+ 裸 token + 花括号嵌套。
// Steam 的 loginusers.vdf / local.vdf 都是这个格式，值只可能是 string 或嵌套块。
public static class VdfParser
{
    public sealed class VdfDict : Dictionary<string, object?> { }

    public static Dictionary<string, object?> Parse(string text)
    {
        int i = 0;
        var root = new Dictionary<string, object?>();
        ParseBlock(text, ref i, root);
        return root;
    }

    private static void ParseBlock(string t, ref int i, Dictionary<string, object?> dict)
    {
        while (true)
        {
            SkipWs(t, ref i);
            if (i >= t.Length || t[i] == '}') { if (i < t.Length) i++; return; }
            var key = ReadToken(t, ref i);
            if (key == null) return;
            if (key.StartsWith('[')) continue; // 平台条件块如 [win32]，跳过
            SkipWs(t, ref i);
            if (i < t.Length && t[i] == '{')
            {
                i++;
                var sub = new VdfDict();
                ParseBlock(t, ref i, sub);
                dict[key] = sub;
            }
            else
            {
                dict[key] = ReadToken(t, ref i);
            }
        }
    }

    private static void SkipWs(string t, ref int i)
    {
        while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
    }

    private static string? ReadToken(string t, ref int i)
    {
        SkipWs(t, ref i);
        if (i >= t.Length) return null;
        if (t[i] == '"')
        {
            i++;
            var sb = new StringBuilder();
            while (i < t.Length && t[i] != '"')
            {
                if (t[i] == '\\' && i + 1 < t.Length) { i++; sb.Append(t[i++]); }
                else sb.Append(t[i++]);
            }
            if (i < t.Length) i++;
            return sb.ToString();
        }
        if (t[i] == '{' || t[i] == '}') return null;
        int start = i;
        while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != '{' && t[i] != '}') i++;
        return i > start ? t[start..i] : null;
    }
}
