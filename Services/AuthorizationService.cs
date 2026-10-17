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
            return Fail(L.Get("Auth_NoTicketsPath"));
        if (!File.Exists(path))
            return Fail(L.Get("Auth_FileNotFound", path));

        var file = new FileInfo(path);
        if (file.Length == 0)
            return Fail(L.Get("Auth_FileEmpty", path));
        if (file.Length > MaxTicketsFileSize)
            return Fail(L.Get("Auth_FileTooBig"));

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            return Fail(L.Get("Auth_ReadFileFailed", ex.Message));
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
                    return Fail(L.Get("Auth_AppIdDup"));
                appIdSeen = true;
                if (!uint.TryParse(appIdMatch.Groups[1].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out appId) || appId == 0)
                    return Fail(L.Get("Auth_AppIdInvalid", appIdMatch.Groups[1].Value));
                continue;
            }

            var ticketMatch = TicketLineRegex.Match(line);
            if (ticketMatch.Success)
            {
                var name = ticketMatch.Groups[1].Value.ToLowerInvariant();
                if (!uint.TryParse(ticketMatch.Groups[2].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var declared))
                    return Fail(L.Get("Auth_BytesInvalid", name, ticketMatch.Groups[2].Value));
                var hex = ticketMatch.Groups[3].Value;
                if (hex.Length % 2 != 0)
                    return Fail(L.Get("Auth_HexMustEven", name));

                if (name == "appticket")
                {
                    if (appTicket != null) return Fail(L.Get("Auth_AppTicketDup"));
                    appTicket = (name, declared, hex);
                }
                else
                {
                    if (eticket != null) return Fail(L.Get("Auth_EticketDup"));
                    eticket = (name, declared, hex);
                }
                continue;
            }

            var nullMatch = TicketNullLineRegex.Match(line);
            if (nullMatch.Success)
            {
                var name = nullMatch.Groups[1].Value.ToLowerInvariant();
                return Fail(L.Get("Auth_TicketNull", name));
            }

            // 无法识别的行：畸形票据行可能含大段 hex，只报行特征不贴原文，票据材料不落日志
            var squashed = string.Concat(line.Where(c => Uri.IsHexDigit(c)));
            if (squashed.Length >= 64)
                return Fail(L.Get("Auth_LineSuspicious"));
            return Fail(L.Get("Auth_LineUnknown", Truncate(line)));
        }

        if (!appIdSeen)
            return Fail(L.Get("Auth_MissingAppId"));
        if (appTicket == null)
            return Fail(L.Get("Auth_MissingAppTicket"));
        if (eticket == null)
            return Fail(L.Get("Auth_MissingEticket"));

        var appTicketBytes = DecodeHex(appTicket.Value.Hex);
        if (appTicketBytes == null)
            return Fail(L.Get("Auth_AppTicketHexInvalid"));
        if (appTicketBytes.Length != appTicket.Value.DeclaredBytes)
            return Fail(L.Get("Auth_AppTicketLenMismatch", appTicket.Value.DeclaredBytes, appTicketBytes.Length));
        if (appTicketBytes.Length < 20)
            return Fail(L.Get("Auth_AppTicketTooShort"));

        var eticketBytes = DecodeHex(eticket.Value.Hex);
        if (eticketBytes == null)
            return Fail(L.Get("Auth_EticketHexInvalid"));
        if (eticketBytes.Length != eticket.Value.DeclaredBytes)
            return Fail(L.Get("Auth_EticketLenMismatch", eticket.Value.DeclaredBytes, eticketBytes.Length));

        // 内嵌 AppID 定位：Steam 只保证经 GetAppOwnershipTicketData 返回的偏移，
        // 票据布局随类型变化（常规票据 AppID 在偏移 16；部分 D 加密标题不在 16，
        // 实测 3489700 的票据 16 处为 7）。先走常规偏移，找不到再在票据内 4 字节
        // 对齐扫描声明 appid——32 位随机命中的概率可忽略，首个命中即采纳并记录偏移。
        var appIdOffset = LocateEmbeddedAppId(appTicketBytes, appId);
        if (appIdOffset < 0)
        {
            LogMismatchForensics(appId, appTicketBytes);
            return Fail(L.Get("Auth_AppIdNotEmbedded", appId));
        }
        if (appIdOffset != 16)
            Log(L.Get("LogCat_Auth"), L.Get("Auth_EmbeddedOffsetHit", appIdOffset));

        // AppTicket 内嵌 SteamID：偏移 8 处小端 uint64，必须非零
        var steamId = BitConverter.ToUInt64(appTicketBytes, 8);
        if (steamId == 0)
            return Fail(L.Get("Auth_NoSteamId"));

        Log(L.Get("LogCat_Auth"), L.Get("Auth_ParseOkLog", appId, appTicketBytes.Length, eticketBytes.Length));
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

    // 在票据内定位声明 appid：常规偏移 16 优先，其余 4 字节对齐位置依次扫描
    private static int LocateEmbeddedAppId(byte[] appTicketBytes, uint appId)
    {
        if (appTicketBytes.Length >= 20 && BitConverter.ToUInt32(appTicketBytes, 16) == appId)
            return 16;
        for (var off = 0; off + 4 <= appTicketBytes.Length; off += 4)
        {
            if (off == 16) continue;
            if (BitConverter.ToUInt32(appTicketBytes, off) == appId)
                return off;
        }
        return -1;
    }

    // 内嵌校验失败时记录取证信息：只记长度与扫描结论，不记票据字节内容，
    // 取证行会随日志附件外发，票据材料不得落盘到日志
    private static void LogMismatchForensics(uint appId, byte[] appTicketBytes)
    {
        try
        {
            Log(L.Get("LogCat_Auth"), L.Get("Auth_MismatchForensics", appId, appTicketBytes.Length));
        }
        catch { }
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
                L.Get("Auth_NoLuaDir"));
        }

        var activePath = Path.Combine(luaFolder, $"{appId}.lua");
        if (!File.Exists(activePath))
        {
            var disabledPath = Path.Combine(luaFolder, "Disable", $"{appId}.lua");
            if (File.Exists(disabledPath))
            {
                return new LuaManifestCheckResult(
                    LuaManifestCheckStatus.DisabledOnly, disabledPath, false,
                    L.Get("Auth_ManifestDisabledPath", appId));
            }

            return new LuaManifestCheckResult(
                LuaManifestCheckStatus.NotFound, null, false,
                L.Get("Auth_ManifestNotFound", appId));
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
                L.Get("Auth_ReadManifestFailed", activePath, ex.Message));
        }

        var hasAddAppId = AddAppIdRegex(appId).IsMatch(content);
        if (!hasAddAppId)
        {
            return new LuaManifestCheckResult(
                LuaManifestCheckStatus.NoAddAppId, activePath, false,
                L.Get("Auth_NoAddAppIdFound", appId));
        }

        var hasLegacy = HasLegacyTicketLines(content, appId);
        return new LuaManifestCheckResult(
            LuaManifestCheckStatus.Ready, activePath, hasLegacy,
            hasLegacy
                ? L.Get("Auth_HasLegacy")
                : L.Get("Auth_ReadyManifest"));
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
            return L.Get("Auth_LuaFileMissing", luaFilePath);

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
            Log(L.Get("LogCat_Auth"), L.Get("Auth_RemovedLegacyLog", luaFilePath, removed, appId));
            return null;
        }
        catch (Exception ex)
        {
            return L.Get("Auth_RemoveLegacyFailed", ex.Message);
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
