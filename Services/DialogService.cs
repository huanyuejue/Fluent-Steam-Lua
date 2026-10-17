using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Controls;

namespace SteamLuaManager.Services;

public sealed class DialogService : IDialogService
{
    public Task<bool> ShowConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null) =>
        ShowDialogOnUiAsync(() => new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = primaryText ?? L.Get("Common_Confirm"),
            CloseButtonText = closeText ?? L.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        }, L.Get("Svc_DlgShowConfirm"));

    // 所有弹框走这里串行：InvokeAsync + async lambda 得到 Task<Task>，必须 await 两次才真正等到用户点掉对话框，
    // 否则调用方以为弹完了继续弹下一个，撞上 ContentDialog 互斥直接丢框；内外两层 try 保底返回 false
    private static async Task<bool> ShowDialogOnUiAsync(Func<ContentDialog> create, string opName)
    {
        try
        {
            return await await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    return await create().ShowAsync() == ContentDialogResult.Primary;
                }
                catch (Exception ex)
                {
                    LogService.Warn(L.Get("LogCat_Dialog"), L.Get("Svc_DlgOpFailed", opName, ex.Message));
                    return false;
                }
            });
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Dialog"), L.Get("Svc_DlgOpFailed", opName, ex.Message));
            return false;
        }
    }

    // 删除存档确认：结构化面板 + 红色删除按钮；
    // 红色样式必须 BasedOn 默认 Button 样式，只覆颜色，否则圆角模板丢失变成方块
    public Task<bool> ShowDeleteSavesConfirmAsync(string displayName, int appId, IReadOnlyList<DeleteTarget> targets, string backupDir, string? note = null) =>
        ShowDialogOnUiAsync(() =>
        {
                    var dangerStyle = new Style(typeof(Button),
                        Application.Current.TryFindResource(typeof(Button)) as Style);
                    dangerStyle.Setters.Add(new Setter(Control.BackgroundProperty,
                        new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C))));
                    dangerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
                    dangerStyle.Setters.Add(new Setter(Control.BorderBrushProperty,
                        new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C))));

                    var secondary = Application.Current.TryFindResource("TextFillColorSecondaryBrush") as Brush
                        ?? Brushes.Gray;
                    var panel = new StackPanel { MaxWidth = 480 };
                    panel.Children.Add(new TextBlock
                    {
                        Text = L.Get("Cloud_DlgDeleteAll", displayName, appId),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = L.Get("Cloud_DlgWillDelete"),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 12, 0, 2)
                    });
                    foreach (var t in targets)
                    {
                        panel.Children.Add(new TextBlock
                        {
                            Text = L.Get("Cloud_DlgTargetLine", DeleteKindLabel(t.Kind), t.FileCount, FormatBytes(t.TotalBytes)),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 6, 0, 0)
                        });
                        panel.Children.Add(new TextBlock
                        {
                            Text = t.Path,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 12,
                            Foreground = secondary
                        });
                    }
                    panel.Children.Add(new TextBlock
                    {
                        Text = note ?? L.Get("Cloud_DlgDefaultNote"),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = secondary,
                        Margin = new Thickness(0, 6, 0, 0)
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = L.Get("Cloud_DlgConsequence"),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 12, 0, 2)
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = L.Get("Cloud_DlgConsequenceBody"),
                        TextWrapping = TextWrapping.Wrap
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = L.Get("Cloud_DlgBackupSection"),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 12, 0, 2)
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = targets.Any(t => t.Kind == "cloud")
                            ? L.Get("Cloud_DlgBackupCloud")
                            : L.Get("Cloud_DlgBackupLocal"),
                        TextWrapping = TextWrapping.Wrap
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = backupDir,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = secondary
                    });

                    return new ContentDialog
                    {
                        Title = L.Get("Cloud_DlgDeleteTitle"),
                        Content = new ScrollViewer
                        {
                            Content = panel,
                            MaxHeight = 440,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                        },
                        PrimaryButtonText = L.Get("Common_Delete"),
                        PrimaryButtonStyle = dangerStyle,
                        CloseButtonText = L.Get("Common_Cancel"),
                        DefaultButton = ContentDialogButton.Close
                    };
        }, L.Get("Svc_DlgShowDelete"));

    private static string DeleteKindLabel(string kind) => kind switch
    {
        "sync" => L.Get("Cloud_KindSync"),
        "cache" => L.Get("Cloud_KindCache"),
        "userdata" => L.Get("Cloud_KindUserdata"),
        "cloud" => L.Get("Cloud_KindCloud"),
        _ => kind
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
    };

    public Task ShowAlertAsync(string title, string message) =>
        ShowDialogOnUiAsync(() => new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            CloseButtonText = L.Get("Common_Confirm"),
            DefaultButton = ContentDialogButton.Close
        }, L.Get("Svc_DlgShowAlert"));

    // 云启用三合一：备份确认 + 启用 + 重启选项一次问完；
    // 主按钮与备份勾选联动，不勾不让点，免得用户没备份就启用
    public async Task<(bool Confirmed, bool RestartNow)> ShowEnableCloudConfirmAsync()
    {
        ContentDialog? dialog = null;
        var backupBox = new CheckBox { Content = L.Get("Cloud_DlgBackupChecked"), Margin = new Thickness(0, 12, 0, 0) };
        // 默认不勾：重启会杀 Steam，用户可能正挂着游戏，重启选项必须显式选
        var restartBox = new CheckBox { Content = L.Get("Cloud_DlgRestartNow"), Margin = new Thickness(0, 6, 0, 0) };
        backupBox.Checked += (_, _) => { if (dialog != null) dialog.IsPrimaryButtonEnabled = true; };
        backupBox.Unchecked += (_, _) => { if (dialog != null) dialog.IsPrimaryButtonEnabled = false; };
        var panel = new StackPanel { MaxWidth = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = L.Get("Cloud_DlgEnableBody"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(backupBox);
        panel.Children.Add(restartBox);
        dialog = new ContentDialog
        {
            Title = L.Get("Cloud_DlgEnableTitle"),
            Content = panel,
            PrimaryButtonText = L.Get("Cloud_DlgEnableBtn"),
            CloseButtonText = L.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };
        try
        {
            var result = await await Application.Current.Dispatcher.InvokeAsync(() => dialog.ShowAsync());
            return (result == ContentDialogResult.Primary, restartBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Dialog"), L.Get("Svc_DlgShowEnableFail", ex.Message));
            return (false, false);
        }
    }
}
