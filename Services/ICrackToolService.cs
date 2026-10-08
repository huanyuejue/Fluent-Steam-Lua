namespace SteamLuaManager.Services;

public interface ICrackToolService
{
    // 一键破解：脱壳 + 部署 emu；AppID 必填；WebApiKey 选填（有则补全成就/库存信息）
    // unpackOnly=true 时只脱壳（Steamless），保留 Steam 启动依赖，AppID 可空
    Task<bool> CrackAsync(string inputPath, string appId, string? webApiKey, IProgress<string>? log, CancellationToken ct = default, bool unpackOnly = false);
    // 还原：删 emu 文件、恢复备份
    Task<bool> RestoreAsync(string inputPath, IProgress<string>? log, CancellationToken ct = default);
    // 更新 Goldberg emu（走 GitHub，需网络）
    Task<bool> UpdateEmuAsync(IProgress<string>? log, CancellationToken ct = default);
    // emu 是否已下载；已安装版本号（未安装返回 null）
    bool IsEmuInstalled();
    string? GetEmuVersion();
    string CacheRoot { get; }
}
