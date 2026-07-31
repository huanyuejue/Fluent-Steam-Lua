using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.DependencyInjection;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

public partial class AboutView : UserControl
{
    private const string ProjectUrl = "https://github.com/huanyuejue/Fluent-Steam-Lua";

    public string VersionText { get; }

    public AboutView()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText = version is not null
            ? $"版本 {version.Major}.{version.Minor}.{version.Build}"
            : "版本 1.0.0";
        InitializeComponent();
        DataContext = this;
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var updateService = App.ServiceProvider?.GetRequiredService<IUpdateService>();
            if (updateService == null) return;
            var result = await updateService.CheckForUpdateAsync();

            if (result.HasUpdate)
            {
                var content = new StackPanel
                {
                    MaxWidth = 420
                };
                content.Children.Add(new TextBlock
                {
                    Text = $"当前版本：{result.CurrentVersion}\n最新版本：{result.TagName}",
                    TextWrapping = TextWrapping.Wrap
                });

                if (!string.IsNullOrWhiteSpace(result.ReleaseNotes))
                {
                    var notes = new TextBlock
                    {
                        Text = result.ReleaseNotes,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 8, 0, 0)
                    };
                    content.Children.Add(new ScrollViewer
                    {
                        MaxHeight = 300,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Padding = new Thickness(0, 0, 4, 0),
                        Content = notes
                    });
                }

                var dialog = new ContentDialog
                {
                    Title = "发现新版本",
                    Content = content,
                    PrimaryButtonText = "打开下载页",
                    CloseButtonText = "稍后再说",
                    DefaultButton = ContentDialogButton.Primary
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    Process.Start(new ProcessStartInfo(result.ReleaseUrl) { UseShellExecute = true });
                return;
            }

            await ShowDialogAsync("已是最新版本", $"当前已是最新版本：{result.CurrentVersion}");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("检查更新失败", $"无法获取最新版本信息：{ex.Message}");
        }
    }

    private static async Task ShowDialogAsync(string title, string message)
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
}
