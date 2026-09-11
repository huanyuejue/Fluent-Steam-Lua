using System.Windows;
using System.Windows.Controls;
using iNKORE.UI.WPF.Modern.Controls;

namespace SteamLuaManager.Services;

public sealed class DialogService : IDialogService
{
    public async Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消")
    {
        try
        {
            // InvokeAsync + async lambda 得到 Task<Task>，必须 await 两次才真正等到用户点掉对话框，
            // 否则调用方以为弹完了继续弹下一个，撞上 ContentDialog 互斥直接丢框
            return await await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = title,
                        Content = new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 420
                        },
                        PrimaryButtonText = primaryText,
                        CloseButtonText = closeText,
                        DefaultButton = ContentDialogButton.Primary
                    };
                    return await dialog.ShowAsync() == ContentDialogResult.Primary;
                }
                catch (Exception ex)
                {
                    LogService.Warn("对话框", $"显示确认对话框失败: {ex.Message}");
                    return false;
                }
            });
        }
        catch (Exception ex)
        {
            LogService.Warn("对话框", $"显示确认对话框失败: {ex.Message}");
            return false;
        }
    }

    public async Task ShowAlertAsync(string title, string message)
    {
        try
        {
            await await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = title,
                        Content = new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 420
                        },
                        CloseButtonText = "确定",
                        DefaultButton = ContentDialogButton.Close
                    };
                    await dialog.ShowAsync();
                }
                catch (Exception ex)
                {
                    LogService.Warn("对话框", $"显示提示对话框失败: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            LogService.Warn("对话框", $"显示提示对话框失败: {ex.Message}");
        }
    }
}
