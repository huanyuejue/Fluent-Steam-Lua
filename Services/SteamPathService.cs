using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteamLuaManager.Services;

public class SteamPathService : ISteamPathService
{
    private const string RegistryPath = @"SOFTWARE\WOW6432Node\Valve\Steam";
    private const string InstallPathKey = "InstallPath";
    private const string LuaSubFolder = @"config\lua";
    private string? _customPath;

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

    public string? GetLuaFolder()
    {
        var basePath = !string.IsNullOrEmpty(_customPath) ? _customPath : DetectSteamPath();
        if (string.IsNullOrEmpty(basePath)) return null;

        var luaPath = Path.Combine(basePath, LuaSubFolder);
        if (!Directory.Exists(luaPath))
        {
            try { Directory.CreateDirectory(luaPath); }
            catch { return null; }
        }
        return luaPath;
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
        catch { }

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
