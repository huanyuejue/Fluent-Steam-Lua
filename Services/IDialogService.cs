namespace SteamLuaManager.Services;

public interface IDialogService
{
    Task<bool> ShowConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null);
    Task<bool> ShowDeleteSavesConfirmAsync(string displayName, int appId, IReadOnlyList<DeleteTarget> targets, string backupDir, string? note = null);
    Task ShowAlertAsync(string title, string message);
    // 云启用合并确认：（是否确认启用，启用后是否立即重启 Steam）
    Task<(bool Confirmed, bool RestartNow)> ShowEnableCloudConfirmAsync();
}
