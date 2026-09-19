namespace SteamLuaManager.Services;

public interface IDialogService
{
    Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消");
    Task<bool> ShowDeleteSavesConfirmAsync(string displayName, int appId, IReadOnlyList<DeleteTarget> targets, string backupDir);
    Task ShowAlertAsync(string title, string message);
}
