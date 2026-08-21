using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

/// <inheritdoc cref="IAuthorizationService"/>
public sealed class AuthorizationService : IAuthorizationService
{
    private const int MaxTicketsFileSize = 2 * 1024 * 1024; // 2 MB 安全上限

    private readonly ISteamPathService _steamPathService;
    private readonly SteamTicketExtractor _extractor;

    private static readonly Regex AppIdLineRegex = new(
        @"^\s*appid\s*:\s*(\d+)\s*$", RegexOptions.IgnoreCase);

    private static readonly Regex TicketLineRegex = new(
        @"^\s*(appticket|eticket)\s*\(\s*(\d+)\s*bytes\s*\)\s*:\s*([0-9a-fA-F]+)\s*$",
        RegexOptions.IgnoreCase);

    private static readonly Regex TicketNullLineRegex = new(
        @"^\s*(appticket|eticket)\s*:\s*null\s*$", RegexOptions.IgnoreCase);

    private static readonly Regex AnyAddAppIdRegex = new(
        @"^\s*addappid\s*\(", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public AuthorizationService(ISteamPathService steamPathService, SteamTicketExtractor extractor)
    {
        _steamPathService = steamPathService;
        _extractor = extractor;
    }

    // ==================== 解析与校验 ====================

    public TicketParseResult ParseTicketsFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Fail("未提供 tickets.txt 文件路径");
        if (!File.Exists(path))
            return Fail($"文件不存在：{path}");

        var file = new FileInfo(path);
        if (file.Length == 0)
            return Fail($"文件为空：{path}");
        if (file.Length > MaxTicketsFileSize)
            return Fail("文件超过 2 MB 上限，请确认为有效的 tickets.txt");

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            return Fail($"读取文件失败：{ex.Message}");
        }

        uint appId = 0;
        bool appIdSeen = false;
        (string Name, uint DeclaredBytes, string Hex)? appTicket = null;
        (string Name, uint DeclaredBytes, string Hex)? eticket = null;

        foreach (var rawLine in lines)
        {
            var line = (rawLine ?? string.Empty).Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var appIdMatch = AppIdLineRegex.Match(line);
            if (appIdMatch.Success)
            {
                if (appIdSeen)
                    return Fail("tickets.txt 中 appid 字段只能出现一次");
                appIdSeen = true;
                if (!uint.TryParse(appIdMatch.Groups[1].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out appId) || appId == 0)
                    return Fail($"appid 无效：{appIdMatch.Groups[1].Value}");
                continue;
            }

            var ticketMatch = TicketLineRegex.Match(line);
            if (ticketMatch.Success)
            {
                var name = ticketMatch.Groups[1].Value.ToLowerInvariant();
                var declared = uint.Parse(ticketMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                var hex = ticketMatch.Groups[3].Value;
                if (hex.Length % 2 != 0)
                    return Fail($"{name} 十六进制长度必须为偶数");

                if (name == "appticket")
                {
                    if (appTicket != null) return Fail("tickets.txt 中 appticket 字段只能出现一次");
                    appTicket = (name, declared, hex);
                }
                else
                {
                    if (eticket != null) return Fail("tickets.txt 中 eticket 字段只能出现一次");
                    eticket = (name, declared, hex);
                }
                continue;
            }

            var nullMatch = TicketNullLineRegex.Match(line);
            if (nullMatch.Success)
            {
                var name = nullMatch.Groups[1].Value.ToLowerInvariant();
                return Fail($"tickets.txt 中 {name} 数据为 null（该授权不完整，无法导入）");
            }

            return Fail($"无法识别的行：{Truncate(line)}");
        }

        if (!appIdSeen)
            return Fail("tickets.txt 缺少 appid 字段");
        if (appTicket == null)
            return Fail("tickets.txt 缺少 appticket 字段");
        if (eticket == null)
            return Fail("tickets.txt 缺少 eticket 字段");

        var appTicketBytes = DecodeHex(appTicket.Value.Hex);
        if (appTicketBytes == null)
            return Fail("appticket 十六进制数据无效");
        if (appTicketBytes.Length != appTicket.Value.DeclaredBytes)
            return Fail($"appticket 声明 {appTicket.Value.DeclaredBytes} 字节，实际解码 {appTicketBytes.Length} 字节，长度不一致");
        if (appTicketBytes.Length < 20)
            return Fail("appticket 过短（少于 20 字节），不是有效的所有权票据");

        var eticketBytes = DecodeHex(eticket.Value.Hex);
        if (eticketBytes == null)
            return Fail("eticket 十六进制数据无效");
        if (eticketBytes.Length != eticket.Value.DeclaredBytes)
            return Fail($"eticket 声明 {eticket.Value.DeclaredBytes} 字节，实际解码 {eticketBytes.Length} 字节，长度不一致");

        // AppTicket 内嵌校验：偏移 16 处小端 uint32 应等于 appid
        var embeddedAppId = BitConverter.ToUInt32(appTicketBytes, 16);
        if (embeddedAppId != appId)
            return Fail($"appticket 内嵌 AppID {embeddedAppId} 与文件声明 {appId} 不一致，请确认 tickets.txt 与游戏对应");

        // AppTicket 内嵌 SteamID：偏移 8 处小端 uint64，必须非零
        var steamId = BitConverter.ToUInt64(appTicketBytes, 8);
        if (steamId == 0)
            return Fail("无法从 appticket 中读取有效的 SteamID");

        Log("授权", $"解析 tickets.txt 成功：AppID {appId}，AppTicket {appTicketBytes.Length}B，ETicket {eticketBytes.Length}B");
        return new TicketParseResult(
            Ok: true, Error: null,
            AppId: appId, SteamId: steamId,
            AppTicket: appTicketBytes, ETicket: eticketBytes);
    }

    private static byte[]? DecodeHex(string hex)
    {
        try
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)((FromHex(hex[i * 2]) << 4) | FromHex(hex[i * 2 + 1]));
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static int FromHex(char c)
    {
        var value = c is >= '0' and <= '9' ? c - '0'
            : c is >= 'a' and <= 'f' ? c - 'a' + 10
            : c is >= 'A' and <= 'F' ? c - 'A' + 10
            : throw new FormatException();
        return value;
    }

    private static TicketParseResult Fail(string error) => new(false, error);

    private static string Truncate(string value, int max = 48)
        => value.Length <= max ? value : value[..max] + "...";

    // ==================== Lua 清单入库检测 ====================

    public LuaManifestCheckResult CheckLuaManifest(uint appId)
    {
        var luaFolder = _steamPathService.GetLuaFolder();
        if (string.IsNullOrEmpty(luaFolder) || !Directory.Exists(luaFolder))
        {
            return new LuaManifestCheckResult(
                LuaManifestCheckStatus.NotConfigured, null, false,
                "未检测到当前生效的 Lua 清单目录，请先安装并配置 OpenSteamTool。");
        }

        var activePath = Path.Combine(luaFolder, $"{appId}.lua");
        if (!File.Exists(activePath))
        {
            var disabledPath = Path.Combine(luaFolder, "Disable", $"{appId}.lua");
            if (File.Exists(disabledPath))
            {
                return new LuaManifestCheckResult(
                    LuaManifestCheckStatus.DisabledOnly, disabledPath, false,
                    $"AppID {appId} 的清单存在但已被禁用（位于 Disable 目录），请先启用该游戏后再使用授权。");
            }

            return new LuaManifestCheckResult(
                LuaManifestCheckStatus.NotFound, null, false,
                $"当前未检测到 AppID {appId} 的生效游戏清单。该授权只适用于通过 Lua 清单入库的游戏，请先入库并启用游戏后，再使用授权信息。");
        }

        string content;
        try
        {
            content = File.ReadAllText(activePath);
        }
        catch (Exception ex)
        {
            return new LuaManifestCheckResult(
                LuaManifestCheckStatus.NoAddAppId, activePath, false,
                $"读取 {activePath} 失败：{ex.Message}，已停止写入授权。");
        }

        var hasAddAppId = AddAppIdRegex(appId).IsMatch(content);
        if (!hasAddAppId)
        {
            return new LuaManifestCheckResult(
                LuaManifestCheckStatus.NoAddAppId, activePath, false,
                $"找到了同名 Lua 文件，但其中未检测到 AppID {appId} 的 addappid 入库配置，已停止写入授权。");
        }

        var hasLegacy = HasLegacyTicketLines(content, appId);
        return new LuaManifestCheckResult(
            LuaManifestCheckStatus.Ready, activePath, hasLegacy,
            hasLegacy
                ? "已检测到生效 Lua 清单，但其中仍包含旧授权语句（setAppTicket/setETicket），需移除后继续。"
                : "已检测到生效 Lua 清单与 addappid 入库配置。");
    }

    private static Regex AddAppIdRegex(uint appId)
        => new($@"\baddappid\s*\(\s*{appId}\s*(?:,|\))", RegexOptions.IgnoreCase);

    private static bool HasLegacyTicketLines(string content, uint appId)
    {
        var pattern = $@"^\s*(?!--\s*)(?:setappticket|seteticket)\s*\(\s*{appId}\b";
        return Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    // ==================== 旧授权语句清理 ====================

    public string? RemoveLegacyTicketLines(string luaFilePath, uint appId)
    {
        if (string.IsNullOrEmpty(luaFilePath) || !File.Exists(luaFilePath))
            return $"Lua 文件不存在：{luaFilePath}";

        try
        {
            var lines = File.ReadAllLines(luaFilePath);
            var pattern = $@"^\s*(?!--\s*)(?:setappticket|seteticket)\s*\(\s*{appId}\b";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            var kept = new List<string>(lines.Length);
            var removed = 0;
            foreach (var line in lines)
            {
                if (regex.IsMatch(line))
                {
                    removed++;
                    continue;
                }
                kept.Add(line);
            }

            if (removed == 0)
                return null; // 无需清理

            WriteAllTextAtomic(luaFilePath, string.Join("\r\n", kept));
            Log("授权", $"已从 {luaFilePath} 移除 {removed} 行旧授权语句（AppID {appId}）");
            return null;
        }
        catch (Exception ex)
        {
            return $"移除旧授权语句失败：{ex.Message}";
        }
    }

    private static void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        var tmp = Path.Combine(dir!, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { }
    }

    // ==================== 导入 ====================

    public TicketImportResult ImportTicket(TicketData ticket)
    {
        var (ok, error, warning) = SteamCredentialStore.WriteTickets(
            ticket.AppId, ticket.AppTicket, ticket.ETicket, ticket.SteamId);

        if (!ok)
            return new TicketImportResult(ticket.AppId, SteamCredentialStore.GetAppsKey(ticket.AppId),
                ticket.AppTicket.Length, ticket.ETicket.Length, error);

        return new TicketImportResult(ticket.AppId, SteamCredentialStore.GetAppsKey(ticket.AppId),
            ticket.AppTicket.Length, ticket.ETicket.Length, warning);
    }

    // ==================== 提取 ====================

    public Task<TicketExtractionResult> ExtractAsync(uint appId, CancellationToken ct = default)
        => _extractor.ExtractAsync(appId, ct);

    public string GetDefaultTicketsPath(uint appId)
        => _extractor.GetDefaultTicketsPath(appId);

    private static void Log(string category, string message)
        => LogService.Info(category, message);
}
