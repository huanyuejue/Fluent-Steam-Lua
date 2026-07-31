using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SteamLuaManager.Services;

/// <summary>
/// 解析 Steam 的 appinfo.vdf（V40/V41 二进制格式）。
/// V40：key 为内联 null 结尾字符串；V41：key 为 u32 索引，映射到文件尾部的字符串表。
/// </summary>
public static class AppInfoVdf
{
    private const uint MagicV40 = 0x07564428;
    private const uint MagicV41 = 0x07564429;
    private const int EntryMetadataSize = 60;
    private const int MaxDepth = 64;

    public sealed record AppEntry(uint AppId, string? Type, string? Name);

    public static List<AppEntry> Parse(string path)
    {
        var result = new List<AppEntry>();
        if (!File.Exists(path)) return result;

        using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (fs.Length < 16) return result;

        var magic = ReadU32(fs);
        ReadU32(fs);
        if (magic != MagicV40 && magic != MagicV41) return result;

        List<string>? strings = null;
        if (magic == MagicV41)
        {
            var tableOffset = ReadI64(fs);
            strings = ReadStringTable(fs, tableOffset);
        }

        fs.Position = 16;
        while (fs.Position + 8 <= fs.Length)
        {
            var appId = ReadU32(fs);
            if (appId == 0) break;

            var size = ReadU32(fs);
            if (size < EntryMetadataSize || fs.Position + size > fs.Length) break;

            var entryEnd = fs.Position + size;
            fs.Position += EntryMetadataSize;

            var payloadLen = entryEnd - fs.Position;
            if (payloadLen <= 0)
            {
                fs.Position = entryEnd;
                continue;
            }

            var payload = ReadBytes(fs, payloadLen);
            var (type, name) = ParsePayload(payload, strings);

            result.Add(new AppEntry(appId, type, name));
            fs.Position = entryEnd;
        }

        return result;
    }

    private static (string? Type, string? Name) ParsePayload(byte[] payload, List<string>? strings)
    {
        using var ms = new MemoryStream(payload);
        if (ms.Length < 1) return (null, null);

        var rootType = ms.ReadByte();
        var rootName = ReadKey(ms, strings);
        if (rootType == -1 || rootName == null) return (null, null);

        var root = new Node { Name = rootName, Children = ReadChildren(ms, strings, 0) };
        if (root.Children == null) return (null, null);

        string? type = null;
        string? name = null;
        string? zhName = null;

        foreach (var child in root.Children)
        {
            if (child.Children == null) continue;

            if (string.Equals(child.Name, "common", StringComparison.OrdinalIgnoreCase))
            {
                type = FindValue(child.Children, "type");
                name = FindValue(child.Children, "name");
            }
            else if (string.Equals(child.Name, "localization", StringComparison.OrdinalIgnoreCase))
            {
                var zh = FindChild(child.Children, "schinese") ?? FindChild(child.Children, "tchinese");
                if (zh?.Children != null)
                {
                    zhName = FindValue(zh.Children, "name");
                }
            }
        }

        return (type, zhName ?? name);
    }

    private static Node? FindChild(List<Node> children, string key)
    {
        foreach (var child in children)
        {
            if (string.Equals(child.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }
        return null;
    }

    private static string? FindValue(List<Node> children, string key)
    {
        foreach (var child in children)
        {
            if (string.Equals(child.Name, key, StringComparison.OrdinalIgnoreCase) &&
                child.Value is string value)
            {
                return value;
            }
        }
        return null;
    }

    private static List<Node>? ReadChildren(Stream s, List<string>? strings, int depth)
    {
        if (depth > MaxDepth) return null;

        var children = new List<Node>();
        while (true)
        {
            var type = s.ReadByte();
            if (type < 0) return null;
            if (type == 8) break;

            var name = ReadKey(s, strings);
            if (name == null) return null;
            var node = new Node { Name = name };
            switch (type)
            {
                case 0:
                    node.Children = ReadChildren(s, strings, depth + 1);
                    if (node.Children == null) return null;
                    break;
                case 1:
                    node.Value = ReadNullString(s);
                    if (node.Value == null) return null;
                    break;
                case 2:
                    node.Value = ReadU32(s);
                    break;
                case 3:
                    node.Value = ReadF32(s);
                    break;
                case 4:
                case 6:
                    node.Value = ReadU32(s);
                    break;
                case 7:
                    node.Value = ReadU64(s);
                    break;
                default:
                    return null;
            }
            children.Add(node);
        }
        return children;
    }

    private static string? ReadKey(Stream s, List<string>? strings)
    {
        if (strings != null)
        {
            var index = ReadU32(s);
            return index < (uint)strings.Count ? strings[(int)index] : null;
        }
        return ReadNullString(s);
    }

    private static List<string> ReadStringTable(Stream s, long offset)
    {
        var result = new List<string>();
        if (offset <= 0 || offset >= s.Length) return result;

        s.Position = offset;
        var count = ReadU32(s);
        for (int i = 0; i < count && s.Position < s.Length; i++)
        {
            var value = ReadNullString(s);
            if (value == null) break;
            result.Add(value);
        }
        return result;
    }

    private static string? ReadNullString(Stream s)
    {
        var buffer = new MemoryStream();
        int b;
        while ((b = s.ReadByte()) > 0)
        {
            buffer.WriteByte((byte)b);
        }
        return b < 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static uint ReadU32(Stream s)
    {
        var data = new byte[4];
        s.Read(data, 0, 4);
        return BitConverter.ToUInt32(data, 0);
    }

    private static long ReadI64(Stream s)
    {
        var data = new byte[8];
        s.Read(data, 0, 8);
        return BitConverter.ToInt64(data, 0);
    }

    private static ulong ReadU64(Stream s)
    {
        var data = new byte[8];
        s.Read(data, 0, 8);
        return BitConverter.ToUInt64(data, 0);
    }

    private static float ReadF32(Stream s)
    {
        var data = new byte[4];
        s.Read(data, 0, 4);
        return BitConverter.ToSingle(data, 0);
    }

    private static byte[] ReadBytes(Stream s, long count)
    {
        var data = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = s.Read(data, read, (int)(count - read));
            if (n <= 0) break;
            read += n;
        }
        if (read < count) Array.Resize(ref data, read);
        return data;
    }

    private sealed class Node
    {
        public string Name = "";
        public List<Node>? Children;
        public object? Value;
    }
}
