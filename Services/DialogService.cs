using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Controls;

namespace SteamLuaManager.Services;

public sealed class DialogService : IDialogService
{
    public Task<bool> ShowConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消") =>
        ShowDialogOnUiAsync(() => new ContentDialog
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
        }, "显示确认对话框");

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
                    LogService.Warn("对话框", $"{opName}失败: {ex.Message}");
                    return false;
                }
            });
        }
        catch (Exception ex)
        {
            LogService.Warn("对话框", $"{opName}失败: {ex.Message}");
            return false;
        }
    }

    // 删除存档确认：结构化面板 + 红色删除按钮；
    // 红色样式必须 BasedOn 默认 Button 样式，只覆颜色，否则圆角模板丢失变成方块
    public Task<bool> ShowDeleteSavesConfirmAsync(string displayName, int appId, IReadOnlyList<DeleteTarget> targets, string backupDir) =>
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
                        Text = $"删除《{displayName}》（{appId}）的全部存档吗？",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = "将删除：",
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 12, 0, 2)
                    });
                    foreach (var t in targets)
                    {
                        panel.Children.Add(new TextBlock
                        {
                            Text = $"- {DeleteKindLabel(t.Kind)}：{t.FileCount} 个文件（{FormatBytes(t.TotalBytes)}）",
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
                        Text = "注：本地目录模式下同步目录即云副本，没有额外远端。",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = secondary,
                        Margin = new Thickness(0, 6, 0, 0)
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = "后果",
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 12, 0, 2)
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = "下次启动该游戏时如同从未玩过，从头开始。",
                        TextWrapping = TextWrapping.Wrap
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = "备份",
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 12, 0, 2)
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = "删除前自动备份，恢复需手动拷回对应目录。删除期间请保持 Steam 关闭。",
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
                        Title = "删除存档",
                        Content = new ScrollViewer
                        {
                            Content = panel,
                            MaxHeight = 440,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                        },
                        PrimaryButtonText = "删除",
                        PrimaryButtonStyle = dangerStyle,
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close
                    };
        }, "显示删除确认对话框");

    private static string DeleteKindLabel(string kind) => kind switch
    {
        "sync" => "重定向存档",
        "cache" => "DLL 本地缓存",
        "userdata" => "Steam 用户数据",
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
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        }, "显示提示对话框");
}
