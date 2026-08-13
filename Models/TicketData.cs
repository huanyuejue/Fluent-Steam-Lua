namespace SteamLuaManager.Models;

/// <summary>一份经过校验的 Denuvo 授权票据数据。UI 与日志只呈现字节数，绝不输出完整 hex。</summary>
public sealed record TicketData(
    uint AppId,
    ulong SteamId,
    byte[] AppTicket,
    byte[] ETicket,
    string? SourcePath);

/// <summary>提取流程的结果（已落盘到 cache 目录）。</summary>
public sealed record TicketExtractionResult(
    TicketData Ticket,
    string OutputPath,
    string? Warning);

/// <summary>导入流程的结果。</summary>
public sealed record TicketImportResult(
    uint AppId,
    string RegistryPath,
    int AppTicketBytes,
    int ETicketBytes,
    string? Warning);
