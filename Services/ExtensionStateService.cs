using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SteamLuaManager.Services;

/// <summary>
/// 读取 Steam 内置浏览器（CEF）的扩展注册信息，判断入库扩展是否已安装并启用。
///
/// Steam 的 CEF 用户数据目录由 steamwebhelper 的 -cachedir 参数决定，
/// 默认为 %LocalAppData%\Steam\htmlcache，扩展清单记录在其中的
/// Default\Secure Preferences 里。该文件带 HMAC 保护，因此这里只读不改：
/// 篡改会导致 Chrome 把整份扩展配置判为被修改并重置。
/// </summary>
public interface IExtensionStateService
{
    /// <summary>查询扩展当前状态。</summary>
    ExtensionInstallState Query(string extensionId);
}

public sealed record ExtensionInstallState(
    bool Installed,
    bool Enabled,
    string? InstallPath,
    string? Name,
    string? Version);

public sealed class ExtensionStateService : IExtensionStateService
{
    /// <summary>
    /// 扩展 ID 集合。
    /// 带 key 的正式包用固定 ID（程序目录变动也不影响）；
    /// 早期未带 key 的版本由 Chrome 按路径推导，换目录后会变，故一并兼容。
    /// </summary>
    public static readonly string[] KnownExtensionIds =
    {
        "kgdjhdhilpddemficmfanmehanglaaha",
        "mkalijidhpmfbigdpnlbckjiahmkjkhi"
    };
    private const string SecurePreferencesRelative = @"Default\Secure Preferences";

    public ExtensionInstallState Query(string extensionId)
    {
        try
        {
            var cacheDir = GetSteamCacheDir();
            if (string.IsNullOrEmpty(cacheDir)) return new ExtensionInstallState(false, false, null, null, null);

            var prefsPath = Path.Combine(cacheDir, SecurePreferencesRelative);
            if (!File.Exists(prefsPath)) return new ExtensionInstallState(false, false, null, null, null);

            // 必须走 ReadAllText：该文件里混有非 ASCII 字符，按字节流直接解析会因编码判定失败
            using var doc = JsonDocument.Parse(File.ReadAllText(prefsPath));
            if (!doc.RootElement.TryGetProperty("extensions", out var extensions)) return new ExtensionInstallState(false, false, null, null, null);
            if (!extensions.TryGetProperty("settings", out var settings)) return new ExtensionInstallState(false, false, null, null, null);
            // 依次尝试已知的 ID，命中任意一个即认为已安装
            if (!TryGetEntry(settings, extensionId, out var entry)) return new ExtensionInstallState(false, false, null, null, null);

            // location: 1=INTERNAL 4=UNPACKED 5=COMPONENT 9=EXTERNAL_REGISTRY
            var location = entry.TryGetProperty("location", out var loc) ? loc.GetInt32() : 0;
            // state: 0=DISABLED 1=ENABLED 2=TERMINATED 3=BLACKLISTED
            var state = entry.TryGetProperty("state", out var st) ? st.GetInt32() : 0;

            var path = entry.TryGetProperty("path", out var p) ? p.GetString() : null;

            // Steam 记录的 manifest 快照经常是空的，名称与版本改从部署目录的清单读，
            // 那是扩展实际加载的文件，内容一定与运行中的一致
            string? name = null;
            string? version = null;
            if (entry.TryGetProperty("manifest", out var manifest))
            {
                if (manifest.TryGetProperty("name", out var n)) name = n.GetString();
                if (manifest.TryGetProperty("version", out var v)) version = v.GetString();
            }
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
            {
                var fromDisk = ReadManifestInfo(path);
                name ??= fromDisk.Name;
                version ??= fromDisk.Version;
            }

            // location 4（解压加载）或 5（组件）都算已安装；路径仍存在才算真正可用
            var installed = location is 1 or 4 or 5 or 8 or 9 or 10;

            // 配置里的记录可能被 Chrome 留下而扩展实际已被移除（换 ID、目录被清空等），
            // 因此还要核对目标目录里确实有 manifest，否则会误报成已安装
            var manifestAlive = true;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                manifestAlive = File.Exists(Path.Combine(path, "manifest.json"));
            }
            else if (!string.IsNullOrEmpty(path))
            {
                manifestAlive = false;
            }

            var alive = installed && manifestAlive;
            return new ExtensionInstallState(alive, alive && state == 1, path, name, version);
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Settings"), $"读取 Steam 扩展状态失败: {ex.Message}");
            return new ExtensionInstallState(false, false, null, null, null);
        }
    }

    /// <summary>
    /// 在扩展清单里查找指定 ID。
    /// 传入的是我们自己的 ID 时，允许回退到其它历史 ID（同一扩展在不同版本下 ID 可能不同）；
    /// 传入无关 ID 时则只做精确匹配，否则任何查询都会误判为已安装。
    /// </summary>
    /// <summary>从扩展目录的 manifest.json 读名称与版本，读不到时返回空。</summary>
    private static (string? Name, string? Version) ReadManifestInfo(string? extensionPath)
    {
        try
        {
            if (string.IsNullOrEmpty(extensionPath)) return (null, null);
            var manifest = Path.Combine(extensionPath, "manifest.json");
            if (!File.Exists(manifest)) return (null, null);

            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            return (name, version);
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool TryGetEntry(JsonElement settings, string extensionId, out JsonElement entry)
    {
        if (settings.TryGetProperty(extensionId, out entry)) return true;

        if (Array.IndexOf(KnownExtensionIds, extensionId) >= 0)
        {
            foreach (var known in KnownExtensionIds)
            {
                if (!string.Equals(known, extensionId, StringComparison.Ordinal)
                    && settings.TryGetProperty(known, out entry))
                {
                    return true;
                }
            }
        }

        entry = default;
        return false;
    }

    /// <summary>
    /// 定位 Steam 的 CEF 缓存目录。优先读运行中 steamwebhelper 的 -cachedir 参数，
    /// 读不到再退回默认位置。
    /// </summary>
    private static string? GetSteamCacheDir()
    {
        try
        {
            var fromProcess = QueryFromRunningHelper();
            if (!string.IsNullOrEmpty(fromProcess)) return fromProcess;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var fallback = Path.Combine(localAppData, "Steam", "htmlcache");
            return Directory.Exists(fallback) ? fallback : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? QueryFromRunningHelper()
    {
        // steamwebhelper 启动参数 -cachedir 的实测值固定指向
        // %LocalAppData%\Steam\htmlcache，与 Steam 安装目录无关
        foreach (var root in GetLocalAppDataRoots())
        {
            try
            {
                var candidate = Path.Combine(root, "Steam", "htmlcache");
                if (Directory.Exists(candidate)) return candidate;
            }
            catch
            {
                // 单个候选失败就跳过
            }
        }
        return null;
    }

    /// <summary>
    /// 列出可能存放 LocalAppData 的根目录。
    /// 某些精简环境下 GetFolderPath 会返回空串，这时用 USERPROFILE 兜底拼出路径，
    /// 否则整条查找链会因为路径不完整而失效。
    /// </summary>
    private static IEnumerable<string> GetLocalAppDataRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? fromApi = null;
        try
        {
            fromApi = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        catch
        {
            // 取不到时走下面的兜底
        }
        if (!string.IsNullOrWhiteSpace(fromApi) && seen.Add(fromApi))
        {
            yield return fromApi;
        }

        // 从 USERPROFILE 推导：%USERPROFILE%\AppData\Local
        string? profile = null;
        try
        {
            profile = Environment.GetEnvironmentVariable("USERPROFILE");
        }
        catch
        {
            // 读不到就跳过
        }
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var derived = Path.Combine(profile, "AppData", "Local");
            if (seen.Add(derived)) yield return derived;
        }
    }
}
