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
        // 全局未处理异常日志
        var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            System.IO.File.WriteAllText(logPath, args.ExceptionObject.ToString());
        DispatcherUnhandledException += (_, args) =>
        {
            System.IO.File.WriteAllText(logPath, args.Exception.ToString());
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            System.IO.File.WriteAllText(logPath, args.Exception.ToString());

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

        var autoLaunch = ServiceProvider.GetRequiredService<ITrainerAutoLaunchService>();
        autoLaunch.Start();
        mainWindow.Closed += (_, _) => autoLaunch.Dispose();

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
        services.AddSingleton<ISteamDepotService, SteamDepotService>();
        services.AddSingleton<IOpenSteamToolService, OpenSteamToolService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<ITrainerService, TrainerService>();
        services.AddSingleton<ITrainerAutoLaunchService, TrainerAutoLaunchService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ScriptDownloadViewModel>();
        services.AddTransient<ExtractionViewModel>();
        services.AddTransient<TrainerViewModel>();
        services.AddTransient<MainWindow>();
    }
}
