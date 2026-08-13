using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

/// <summary>Lua 清单入库状态检测结果。</summary>
public sealed record LuaManifestCheckResult(
    LuaManifestCheckStatus Status,
    string? LuaFilePath,
    bool HasLegacyTicketLines,
    string? Message);

public enum LuaManifestCheckStatus
{
    /// <summary>Lua 目录不可用/未配置。</summary>
    NotConfigured,

    /// <summary>主目录与 Disable 目录均无该 AppID 的 Lua 文件（未入库）。</summary>
    NotFound,

    /// <summary>仅存在于 Disable 目录（已禁用的清单）。</summary>
    DisabledOnly,

    /// <summary>文件存在但未包含对应 AppID 的 addappid 入库配置。</summary>
    NoAddAppId,

    /// <summary>已入库并包含 addappid，可继续导入。</summary>
    Ready,
}

/// <summary>tickets.txt 解析结果（失败时票据字段为 null）。</summary>
public sealed record TicketParseResult(
    bool Ok,
    string? Error,
    uint AppId = 0,
    ulong SteamId = 0,
    byte[]? AppTicket = null,
    byte[]? ETicket = null);

/// <summary>
/// 授权功能服务：
/// - 解析并校验 tickets.txt
/// - 检测/清理 Lua 清单中的旧授权语句
/// - 将票据写入 Steam 凭据注册表存储（含回滚）
/// - 启动 worker 子进程提取当前账号的 AppTicket / ETicket 并落盘
/// </summary>
public interface IAuthorizationService
{
    /// <summary>解析并严格校验一份 tickets.txt，不接触注册表与 Lua。</summary>
    TicketParseResult ParseTicketsFile(string path);

    /// <summary>检测目标 AppID 是否已通过生效 Lua 清单入库（Disable 目录不算入库）。</summary>
    LuaManifestCheckResult CheckLuaManifest(uint appId);

    /// <summary>从 Lua 文件中移除与指定 AppID 匹配的旧 setAppTicket / setETicket 语句行（原子替换）。
    /// 成功返回 null，失败返回错误信息。</summary>
    string? RemoveLegacyTicketLines(string luaFilePath, uint appId);

    /// <summary>将票据写入注册表（AppTicket/ETicket REG_BINARY，新值覆盖旧值，含回读校验与失败回滚）。
    /// SteamID 匹配冲突仅在返回值 Warning 中提示，不自动修改注册表 SteamID。</summary>
    TicketImportResult ImportTicket(TicketData ticket);

    /// <summary>通过 worker 子进程提取 AppTicket / ETicket，并落盘到 cache\denuvo\{appid}\tickets.txt。</summary>
    Task<Models.TicketExtractionResult> ExtractAsync(uint appId, CancellationToken ct = default);

    /// <summary>提取结果文件的默认保存路径。</summary>
    string GetDefaultTicketsPath(uint appId);
}
