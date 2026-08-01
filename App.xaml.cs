using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        // worker 子进程模式：仅用于成就/统计数据的读取与写回，
        // 通过进程启动时设置 SteamAppId 避免单进程上下文固定问题
        if (e.Args.Length >= 1 && e.Args[0] == "--worker")
        {
            int code;
            try
            {
                code = StatsWorker.Run(e.Args);
            }
            catch
            {
                code = -1;
            }
            Shutdown(code);
            return;
        }

        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();

        var settingsService = ServiceProvider.GetRequiredService<ISettingsService>();
        var settings = settingsService.Load();

        LogService.SetEnabled(settings.EnableLogging);
        LogService.Info("系统", $"程序启动，版本 {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        LogService.Info("系统", $"操作系统: {Environment.OSVersion.VersionString}, .NET: {Environment.Version}, 进程: {(Environment.Is64BitProcess ? "x64" : "x86")}");
        var steamPathService = ServiceProvider.GetRequiredService<ISteamPathService>();
        LogService.Info("系统", $"Steam 路径: {steamPathService.DetectSteamPath() ?? "未检测到"}");
        RegisterGlobalLogging();
        var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();

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

    protected override void OnExit(ExitEventArgs e)
    {
        LogService.Info("系统", "程序退出");
        LogService.Shutdown();
        base.OnExit(e);
    }

    private void RegisterGlobalLogging()
    {
        DispatcherUnhandledException += (_, args) =>
            LogService.Exception("UI未处理异常", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogService.Exception("AppDomain异常",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "未知异常"));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogService.Exception("后台任务异常", args.Exception);
            args.SetObserved();
        };
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
            new RoutedEventHandler(OnGlobalButtonClick));
    }

    private static void OnGlobalButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button) return;
        var desc = DescribeButtonContent(button.Content);
        if (string.IsNullOrEmpty(desc))
            desc = string.IsNullOrEmpty(button.Name) ? button.GetType().Name : button.Name;
        LogService.Info("操作", $"[{Views.MainWindow.CurrentPage ?? "?"}] 点击按钮: {desc}");
    }

    private static string? DescribeButtonContent(object? content)
    {
        switch (content)
        {
            case string s when !string.IsNullOrWhiteSpace(s):
                return s;
            case TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text):
                return tb.Text;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    var desc = DescribeButtonContent(child);
                    if (!string.IsNullOrEmpty(desc)) return desc;
                }
                break;
        }
        return null;
    }

    private static async Task CheckUpdateOnStartupAsync(Window owner)
    {
        if (ServiceProvider == null) return;
        try
        {
            await Task.Delay(1200);
            var updateService = ServiceProvider.GetRequiredService<IUpdateService>();
            var result = await updateService.CheckForUpdateAsync();
            LogService.Info("更新", $"检查更新完成: 当前版本 {result.CurrentVersion}, 最新版本 {result.TagName}, 有更新={result.HasUpdate}");
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
        services.AddSingleton<ISteamAchievementService, SteamAchievementService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ScriptDownloadViewModel>();
        services.AddTransient<ExtractionViewModel>();
        services.AddTransient<TrainerViewModel>();
        services.AddTransient<AchievementViewModel>();
        services.AddTransient<MainWindow>();
    }
}
