using System.Windows;
using System.Windows.Controls;
using iNKORE.UI.WPF.Modern.Controls;

namespace SteamLuaManager.Services;

public sealed class DialogService : IDialogService
{
    public async Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消")
    {
        var tcs = new TaskCompletionSource<bool>();
        await Application.Current.Dispatcher.InvokeAsync(async () =>
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
                var result = await dialog.ShowAsync();
                tcs.TrySetResult(result == ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                LogService.Warn("对话框", $"显示确认对话框失败: {ex.Message}");
                tcs.TrySetResult(false);
            }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task ShowAlertAsync(string title, string message)
    {
        await Application.Current.Dispatcher.InvokeAsync(async () =>
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
}
