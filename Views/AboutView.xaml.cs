using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.DependencyInjection;
using SteamLuaManager;
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
                await App.ShowUpdateLogDialogAsync();
            else
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
