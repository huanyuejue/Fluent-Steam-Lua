using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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

    private Mutex? _singleInstanceMutex;

    /// <summary>单实例互斥量名称（fix 在 worker 子进程逻辑之后）。</summary>
    private const string SingleInstanceMutexName = "FluentSteamLuaManager_SingleInstance";

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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

        // 授权提取 worker 子进程：一次性提取 AppTicket / ETicket 并写入结果文件
        if (e.Args.Length >= 3 && e.Args[0] == "--ticket-worker")
        {
            // 日志输出遵循用户设置：主进程传入第 4 个参数（1=开启）决定是否记录
            if (e.Args.Length >= 4 && e.Args[3] == "1")
            {
                LogService.SetEnabled(true);
                LogService.Info("提取", $"worker 启动 args=[{string.Join("|", e.Args)}]");
            }
            int code;
            try
            {
                code = TicketWorker.Run(e.Args[1], e.Args[2]);
            }
            catch (Exception ex)
            {
                LogService.Error("提取", $"worker 未捕获异常: {ex}");
                code = -1;
            }
            if (LogService.IsEnabled)
                LogService.Info("提取", $"worker 结束 exit={code}");
            Shutdown(code);
            return;
        }

        // 单实例限制：已有实例时激活其窗口并退出（worker 子进程不参与）
        var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            System.Windows.MessageBox.Show("程序已在运行，重复启动失败。",
                "Fluent Steam Lua 管理工具",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }
        _singleInstanceMutex = mutex;

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

    private static void ActivateExistingInstance()
    {
        try
        {
            var hwnd = FindWindow(null, "Fluent Steam Lua 管理工具");
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, 9); // SW_RESTORE
            SetForegroundWindow(hwnd);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogService.Info("系统", "程序退出");
        LogService.Shutdown();
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
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

            await owner.Dispatcher.InvokeAsync(() => ShowUpdateLogDialogAsync(result));
        }
        catch { }
    }

    /// <summary>弹出更新日志对话框（forceShow 时忽略版本比较，始终显示，供调试/查看更新日志用）。</summary>
    public static async Task ShowUpdateLogDialogAsync(bool forceShow = false)
    {
        if (ServiceProvider == null) return;
        try
        {
            var updateService = ServiceProvider.GetRequiredService<IUpdateService>();
            var result = await updateService.CheckForUpdateAsync();
            if (!forceShow && !result.HasUpdate) return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            await dispatcher.InvokeAsync(() => ShowUpdateLogDialogAsync(result));
        }
        catch { }
    }

    private static async Task ShowUpdateLogDialogAsync(UpdateCheckResult result)
    {
        var content = new StackPanel
        {
            MaxWidth = 440
        };

        // 版本信息卡片
        var versionCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 16)
        };
        versionCard.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        versionCard.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        versionCard.BorderThickness = new Thickness(1);
        var versionStack = new StackPanel();
        versionStack.Children.Add(BuildVersionRow("当前版本", result.CurrentVersion.ToString()));
        versionStack.Children.Add(BuildVersionRow("最新版本", result.TagName));
        versionCard.Child = versionStack;
        content.Children.Add(versionCard);

        // 更新日志标题
        var logTitle = new TextBlock
        {
            Text = "更新日志",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        logTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        content.Children.Add(logTitle);

        // 日志区域卡片
        var logBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            MaxHeight = 300
        };
        logBorder.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
        logBorder.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        logBorder.BorderThickness = new Thickness(1);
        logBorder.Padding = new Thickness(16, 12, 16, 12);

        if (!string.IsNullOrWhiteSpace(result.ReleaseNotes))
        {
            var notes = new TextBlock
            {
                Text = result.ReleaseNotes,
                TextWrapping = TextWrapping.Wrap,
                // 右侧预留空间，避免滚动条遮住日志文字
                Padding = new Thickness(0, 0, 16, 0)
            };
            logBorder.Child = new ScrollViewer
            {
                MaxHeight = 280,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = notes
            };
        }
        else
        {
            logBorder.Child = new TextBlock
            {
                Text = "暂无更新日志",
                Opacity = 0.6
            };
        }
        content.Children.Add(logBorder);

        var dialog = new ContentDialog
        {
            Title = result.HasUpdate ? "发现新版本" : "版本信息",
            Content = content,
            PrimaryButtonText = "打开下载页",
            CloseButtonText = "稍后再说",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            Process.Start(new ProcessStartInfo(result.ReleaseUrl) { UseShellExecute = true });
    }

    private static TextBlock BuildVersionRow(string label, string value)
    {
        var row = new TextBlock
        {
            FontSize = 14,
            Margin = new Thickness(0, 2, 0, 2),
            Text = $"{label}：{value}"
        };
        row.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return row;
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
        services.AddSingleton<ISteamAppInfoService, SteamAppInfoService>();
        services.AddSingleton<IOpenSteamToolService, OpenSteamToolService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<ITrainerService, TrainerService>();
        services.AddSingleton<ITrainerAutoLaunchService, TrainerAutoLaunchService>();
        services.AddSingleton<ISteamAchievementService, SteamAchievementService>();
        services.AddSingleton<SteamTicketExtractor>();
        services.AddSingleton<IAuthorizationService, AuthorizationService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ScriptDownloadViewModel>();
        services.AddTransient<ExtractionViewModel>();
        services.AddTransient<TrainerViewModel>();
        services.AddTransient<AchievementViewModel>();
        services.AddTransient<AuthorizationViewModel>();
        services.AddTransient<MainWindow>();
    }
}
