using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Tomlyn;
using Tomlyn.Model;

namespace SteamLuaManager.Services;

public class SteamPathService : ISteamPathService
{
    private const string RegistryPath = @"SOFTWARE\WOW6432Node\Valve\Steam";
    private const string InstallPathKey = "InstallPath";
    private const string LuaSubFolder = @"config\lua";
    private const string ConfigFileName = "opensteamtool.toml";
    private const string ExampleConfigFileName = "opensteamtool.example.toml";
    private string? _customPath;

    // 配置文件解析缓存（按最后写入时间失效，支持修改 toml 后即时生效）
    private string? _cachedConfigFile;
    private DateTime _cachedConfigTime = DateTime.MinValue;
    private List<string>? _cachedLuaPaths;

    private string? DetectSteamPathInternal()
    {
        return !string.IsNullOrEmpty(_customPath) ? _customPath : DetectSteamPath();
    }

    public string? DetectSteamPath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            if (key?.GetValue(InstallPathKey) is string installPath)
            {
                if (File.Exists(Path.Combine(installPath, "steam.exe")))
                    return installPath;
            }
        }
        catch
        {
        }

        string[] commonPaths = new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"D:\Program Files (x86)\Steam",
            @"E:\Steam",
            @"E:\Program Files (x86)\Steam"
        };

        foreach (var path in commonPaths)
        {
            if (File.Exists(Path.Combine(path, "steam.exe")))
                return path;
        }

        return null;
    }

    /// <summary>返回当前生效的 OpenSteamTool 配置文件路径。
    /// 优先 opensteamtool.toml；仅存在 example 演示模板时先重命名为 toml 再返回。</summary>
    public string? GetLuaConfigFile()
    {
        var basePath = DetectSteamPathInternal();
        if (string.IsNullOrEmpty(basePath)) return null;

        var primary = Path.Combine(basePath, ConfigFileName);
        if (File.Exists(primary)) return primary;

        var example = Path.Combine(basePath, ExampleConfigFileName);
        if (!File.Exists(example)) return null;

        // example 只是演示模板，实际生效需重命名为 opensteamtool.toml
        try
        {
            File.Move(example, primary);
            _cachedConfigFile = null;
            LogService.Info("Steam路径", $"已将 {ExampleConfigFileName} 重命名为 {ConfigFileName}（正式配置生效）");
            return primary;
        }
        catch (Exception ex)
        {
            LogService.Warn("Steam路径", $"重命名 {ExampleConfigFileName} 为 {ConfigFileName} 失败: {ex.Message}");
            return example;
        }
    }

    /// <summary>解析配置文件 [lua] paths 数组（null = 未配置或不可用）。</summary>
    private List<string>? GetConfiguredLuaPaths()
    {
        var configFile = GetLuaConfigFile();
        if (configFile == null) return null;

        var lastWrite = File.GetLastWriteTimeUtc(configFile);
        if (configFile == _cachedConfigFile && lastWrite == _cachedConfigTime)
            return _cachedLuaPaths;

        _cachedConfigFile = configFile;
        _cachedConfigTime = lastWrite;
        _cachedLuaPaths = null;

        try
        {
            var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(configFile));
            if (model != null &&
                model.TryGetValue("lua", out var luaRaw) &&
                luaRaw is TomlTable luaTable &&
                luaTable.TryGetValue("paths", out var pathsRaw) &&
                pathsRaw is TomlArray pathsArray)
            {
                var list = pathsArray.OfType<string>()
                    .Select(p => p.Trim())
                    .Where(p => p.Length > 0)
                    .ToList();
                _cachedLuaPaths = list;
                if (list.Count > 0)
                {
                    LogService.Info("Steam路径", $"从 {configFile} 读取到自定义 Lua 目录: {string.Join(", ", list)}");
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("Steam路径", $"读取 Lua 目录配置失败: {ex.Message}");
        }

        return _cachedLuaPaths;
    }

    public string? GetLuaFolder()
    {
        var basePath = DetectSteamPathInternal();
        if (string.IsNullOrEmpty(basePath)) return null;

        var configured = GetConfiguredLuaPaths();
        string luaPath;
        if (configured is { Count: > 0 })
        {
            // 取第一个实际存在的目录；都不存在则取第一个
            var existing = configured.FirstOrDefault(Directory.Exists);
            luaPath = existing ?? configured[0];
        }
        else
        {
            luaPath = Path.Combine(basePath, LuaSubFolder);
        }

        if (!Directory.Exists(luaPath))
        {
            try { Directory.CreateDirectory(luaPath); }
            catch (Exception ex) { LogService.Warn("Steam路径", $"创建 Lua 目录失败: {ex.Message}"); return null; }
        }
        return luaPath;
    }

    /// <summary>将用户指定路径写入配置文件 [lua] paths（保留原注释，文本级修改）。</summary>
    public bool SetConfiguredLuaPath(string path)
    {
        try
        {
            var basePath = DetectSteamPathInternal();
            if (string.IsNullOrEmpty(basePath)) return false;

            var configFile = GetLuaConfigFile() ?? Path.Combine(basePath, ExampleConfigFileName);
            var lines = File.Exists(configFile)
                ? File.ReadAllLines(configFile).ToList()
                : new List<string>();

            var escaped = path.Replace("\\", "/").Replace("\"", "\\\"");
            var newLine = $"paths = [\"{escaped}\"]";

            var luaSectionStart = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim() == "[lua]")
                {
                    luaSectionStart = i;
                    break;
                }
            }

            if (luaSectionStart < 0)
            {
                lines.Add("[lua]");
                lines.Add(newLine);
            }
            else
            {
                var sectionEnd = lines.Count;
                for (var i = luaSectionStart + 1; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith('[') && t.EndsWith(']'))
                    {
                        sectionEnd = i;
                        break;
                    }
                }

                var pathsLine = -1;
                for (var i = luaSectionStart + 1; i < sectionEnd; i++)
                {
                    var s = lines[i].TrimStart();
                    if (!s.StartsWith('#') && s.StartsWith("paths", StringComparison.OrdinalIgnoreCase))
                    {
                        pathsLine = i;
                        break;
                    }
                }

                if (pathsLine >= 0)
                    lines[pathsLine] = newLine;
                else
                    lines.Insert(sectionEnd, newLine);
            }

            File.WriteAllLines(configFile, lines);
            _cachedConfigFile = null;
            LogService.Info("Steam路径", $"已将 Lua 目录写入 {configFile}: {path}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("Steam路径", $"写入 Lua 目录配置失败: {ex}");
            return false;
        }
    }

    /// <summary>删除配置文件 [lua] paths 指定，恢复默认 config\lua（保留注释）。</summary>
    public bool ResetConfiguredLuaPath()
    {
        try
        {
            var configFile = GetLuaConfigFile();
            if (configFile == null || !File.Exists(configFile)) return true;

            var lines = File.ReadAllLines(configFile).ToList();

            var luaSectionStart = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim() == "[lua]")
                {
                    luaSectionStart = i;
                    break;
                }
            }

            if (luaSectionStart < 0) return true;

            var sectionEnd = lines.Count;
            for (var i = luaSectionStart + 1; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith('[') && t.EndsWith(']'))
                {
                    sectionEnd = i;
                    break;
                }
            }

            for (var i = sectionEnd - 1; i > luaSectionStart; i--)
            {
                var s = lines[i].TrimStart();
                if (!s.StartsWith('#') && s.StartsWith("paths", StringComparison.OrdinalIgnoreCase))
                {
                    lines.RemoveAt(i);
                    break;
                }
            }

            File.WriteAllLines(configFile, lines);
            _cachedConfigFile = null;
            LogService.Info("Steam路径", $"已重置 Lua 目录配置（删除 {configFile} 中的 [lua] paths）");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("Steam路径", $"重置 Lua 目录配置失败: {ex}");
            return false;
        }
    }

    public void SetCustomPath(string path) => _customPath = path;
    public string? GetCustomPath() => _customPath;

    public SteamToolType DetectSteamToolType()
    {
        var steamPath = !string.IsNullOrEmpty(_customPath) ? _customPath : DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return SteamToolType.None;

        // OpenSteamTool (开源) — 独有标识
        if (File.Exists(Path.Combine(steamPath, "OpenSteamTool.dll")) ||
            File.Exists(Path.Combine(steamPath, "opensteamtool.toml")))
            return SteamToolType.OpenSteamTool;

        // SteamTools (闭源) — 独有标识
        if (File.Exists(Path.Combine(steamPath, "hid.dll")) ||
            File.Exists(Path.Combine(steamPath, "steam.cfg")) ||
            Directory.Exists(Path.Combine(steamPath, @"config\stplug-in")))
            return SteamToolType.SteamTools;

        return SteamToolType.None;
    }

    public List<string> GetAllLibraryPaths()
    {
        var paths = new List<string>();
        var steamPath = !string.IsNullOrEmpty(_customPath) ? _customPath : DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return paths;

        paths.Add(steamPath);

        var vdfPath = Path.Combine(steamPath, @"steamapps\libraryfolders.vdf");
        if (!File.Exists(vdfPath)) return paths;

        try
        {
            var content = File.ReadAllText(vdfPath);

            // 新版格式: "1"\n{\n\t"path"\t"C:\\..."
            var sectionMatches = Regex.Matches(content,
                @"""\d+""\s*\{[^}]*""path""\s+""([^""]+)""",
                RegexOptions.Singleline);
            foreach (Match match in sectionMatches)
            {
                var libPath = match.Groups[1].Value.Replace("\\\\", "\\");
                if (!paths.Contains(libPath))
                    paths.Add(libPath);
            }

            // 旧版格式: "1"  "path" (当新版未匹配到时)
            if (sectionMatches.Count == 0)
            {
                var flatMatches = Regex.Matches(content, @"""\d+""\s+""([^""]+)""");
                foreach (Match match in flatMatches)
                {
                    var libPath = match.Groups[1].Value.Replace("\\\\", "\\");
                    if (!paths.Contains(libPath))
                        paths.Add(libPath);
                }
            }
        }
        catch (Exception ex) { LogService.Warn("Steam路径", $"解析 libraryfolders.vdf 失败: {ex.Message}"); }

        return paths;
    }

    public string? FindAppManifest(int appId)
    {
        foreach (var libPath in GetAllLibraryPaths())
        {
            var acfPath = Path.Combine(libPath, @"steamapps", $"appmanifest_{appId}.acf");
            if (File.Exists(acfPath))
                return acfPath;
        }
        return null;
    }
}
