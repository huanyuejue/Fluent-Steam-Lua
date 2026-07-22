using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;
using SteamLuaManager.Views;

namespace SteamLuaManager;

public partial class App : Application
{
    public static IServiceProvider? ServiceProvider { get; private set; }

    private static ApplicationTheme GetSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 1 ? ApplicationTheme.Light : ApplicationTheme.Dark;
        }
        catch { }
        return ApplicationTheme.Dark;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();

        var settingsService = ServiceProvider.GetRequiredService<ISettingsService>();
        var settings = settingsService.Load();
        switch (settings.SelectedTheme)
        {
            case "Light":
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
                break;
            case "Dark":
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
                break;
            default:
                ThemeManager.Current.ApplicationTheme = GetSystemTheme();
                break;
        }

        mainWindow.Show();

        if (settings.AutoCheckUpdateEnabled)
            _ = CheckUpdateOnStartupAsync(mainWindow);

        _ = Task.Run(async () =>
        {
            try
            {
                var depotService = ServiceProvider.GetRequiredService<ISteamDepotService>();
                await depotService.EnsureAllSourcesAsync();
            }
            catch { }
        });

        base.OnStartup(e);
    }

    private static async Task CheckUpdateOnStartupAsync(Window owner)
    {
        if (ServiceProvider == null) return;
        try
        {
            await Task.Delay(1200);
            var updateService = ServiceProvider.GetRequiredService<IUpdateService>();
            var result = await updateService.CheckForUpdateAsync();
            if (!result.HasUpdate) return;

            await owner.Dispatcher.InvokeAsync(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = "发现新版本",
                    Content = new TextBlock
                    {
                        Text = $"当前版本：{result.CurrentVersion}\n最新版本：{result.TagName}\n\n是否打开 Release 页面下载更新？",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 420
                    },
                    PrimaryButtonText = "打开下载页",
                    CloseButtonText = "稍后再说",
                    DefaultButton = ContentDialogButton.Primary
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    Process.Start(new ProcessStartInfo(result.ReleaseUrl) { UseShellExecute = true });
            });
        }
        catch { }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISteamPathService, SteamPathService>();
        services.AddSingleton<ILuaFileManager, LuaFileManager>();
        services.AddSingleton<ISteamApiService, SteamApiService>();
        services.AddSingleton<ISteamManifestService, SteamManifestService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IHttpClientProvider, HttpClientProvider>();
        services.AddSingleton<ISteamDumperService, SteamDumperService>();
        services.AddSingleton<ISteamDepotService, SteamDepotService>();
        services.AddSingleton<IOpenSteamToolService, OpenSteamToolService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ScriptDownloadViewModel>();
        services.AddTransient<ExtractionViewModel>();
        services.AddTransient<MainWindow>();
    }
}
