using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SteamLuaManager.Models;
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

    // 启动环境打点：窗口打不开 99% 是环境问题，屏幕/DPI/背景材质先记下来，
    // 下次再有人报同类问题直接比对，不用猜
    private static void LogStartupEnvironment()
    {
        try
        {
            LogService.Info(L.Get("LogCat_System"), L.Get("App_LogScreen", SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight, RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly));
        }
        catch (Exception ex) { LogService.Warn(L.Get("LogCat_System"), L.Get("App_LogScreenFailed", ex.Message)); }
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
                try
                {
                    var workerLang = new SettingsService().Load().Language;
                    LocalizationService.Instance.SetLanguage(workerLang);
                }
                catch { }
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
                LogService.Info(L.Get("LogCat_Extract"), L.Get("App_WorkerArgs", string.Join("|", e.Args)));
            }
            int code;
            try
            {
                try
                {
                    var workerLang = new SettingsService().Load().Language;
                    LocalizationService.Instance.SetLanguage(workerLang);
                }
                catch { }
                code = TicketWorker.Run(e.Args[1], e.Args[2]);
            }
            catch (Exception ex)
            {
                LogService.Error(L.Get("LogCat_Extract"), L.Get("App_WorkerUnhandled", ex));
                code = -1;
            }
            if (LogService.IsEnabled)
                LogService.Info(L.Get("LogCat_Extract"), L.Get("App_WorkerExit", code));
            Shutdown(code);
            return;
        }

        // 单实例限制：已有实例时激活其窗口并退出（worker 子进程不参与）
        var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            System.Windows.MessageBox.Show(L.Get("App_AlreadyRunning"),
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

        LocalizationService.Instance.SetLanguage(settings.Language);
        LogService.SetEnabled(settings.EnableLogging);
        try
        {
            // 新版首次启动打扫更新残留（备份目录、暂存目录、临时包），静默失败
            ServiceProvider.GetRequiredService<IAppUpdateService>().CleanupLeftoverUpdateFiles();
        }
        catch { }
        LogService.Info(L.Get("LogCat_System"), L.Get("App_StartupVersion", System.Reflection.Assembly.GetExecutingAssembly().GetName().Version));
        LogService.Info(L.Get("LogCat_System"), L.Get("App_OsInfo", Environment.OSVersion.VersionString, Environment.Version, Environment.Is64BitProcess ? "x64" : "x86"));
        var steamPathService = ServiceProvider.GetRequiredService<ISteamPathService>();
        LogService.Info(L.Get("LogCat_System"), L.Get("App_SteamPathLog", steamPathService.DetectSteamPath() ?? L.Get("App_PathNotFound")));
        RegisterGlobalLogging();
        LogStartupEnvironment();
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

        // 首帧布局崩溃兜底：个别机器（远程桌面/基础显示驱动/异 DPI）上
        // 亚克力背景或硬件渲染会导致 Show 直接炸，降级再试一次，
        // 还不行才弹框（带日志路径），不再静默死
        try
        {
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            LogService.Error(L.Get("LogCat_System"), L.Get("App_MainShowFailed", ex));
            try
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                mainWindow.UpdateBackdrop("None");
                mainWindow.Show();
            }
            catch (Exception ex2)
            {
                LogService.Error(L.Get("LogCat_System"), L.Get("App_CompatFailed", ex2));
                System.Windows.MessageBox.Show(
                    L.Get("App_WindowFailedBody", ex2.Message),
                    "Fluent Steam Lua 管理工具",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }

        var autoLaunch = ServiceProvider.GetRequiredService<ITrainerAutoLaunchService>();
        autoLaunch.Start();
        mainWindow.Closed += (_, _) => autoLaunch.Dispose();

        if (settings.AutoCheckUpdateEnabled)
            _ = CheckUpdateOnStartupAsync(mainWindow);

        if (settings.AutoCheckKernelUpdateEnabled)
            _ = CheckKernelUpdateOnStartupAsync();

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
        LogService.Info(L.Get("LogCat_System"), L.Get("App_Exit"));
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
            LogService.Exception(L.Get("App_UiUnhandled"), args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogService.Exception(L.Get("App_AppDomain"),
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? L.Get("App_UnknownError")));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // SteamKit 内部服务器发现任务在无梯子时被 RST 是预期网络噪声，降为一行 Warn，
            // 其他未观察异常保持原样上报
            if (IsSteamNetworkNoise(args.Exception))
                LogService.Warn(L.Get("LogCat_Net"), L.Get("App_SteamNoise"));
            else
                LogService.Exception(L.Get("App_BackgroundError"), args.Exception);
            args.SetObserved();
        };
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
            new RoutedEventHandler(OnGlobalButtonClick));
    }

    private static bool IsSteamNetworkNoise(Exception ex)
    {
        // 调用栈含 SteamKit2 且异常链里有网络异常才算；其他一律按真实 bug 上报
        if (!ex.ToString().Contains("SteamKit2", StringComparison.Ordinal))
            return false;
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException ||
                e is System.Net.Http.HttpRequestException ||
                e is System.IO.IOException)
                return true;
        }
        return false;
    }

    private static void OnGlobalButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase button) return;
        var desc = DescribeButtonContent(button.Content);
        if (string.IsNullOrEmpty(desc))
            desc = string.IsNullOrEmpty(button.Name) ? button.GetType().Name : button.Name;
        LogService.Info(L.Get("LogCat_Op"), L.Get("App_ClickLog", Views.MainWindow.CurrentPage ?? "?", desc));
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
            LogService.Info(L.Get("LogCat_Update"), L.Get("App_UpdateCheckDone", result.CurrentVersion, result.TagName, result.HasUpdate));
            if (!result.HasUpdate) return;

            await owner.Dispatcher.InvokeAsync(() => ShowUpdateLogDialogAsync(result));
        }
        catch { }
    }

    /// <summary>启动时检测配套内核更新，有新版本时通知主页显示横幅（静默失败）。</summary>
    private static async Task CheckKernelUpdateOnStartupAsync()
    {
        if (ServiceProvider == null) return;
        try
        {
            // 错开软件自检与主页初始化，避免启动期网络拥塞
            await Task.Delay(3000);
            var kernelService = ServiceProvider.GetRequiredService<IOpenSteamToolService>();
            if (!kernelService.IsInstalled) return;
            var localVersion = await kernelService.GetLocalVersionAsync();
            if (string.IsNullOrEmpty(localVersion)) return;
            var (remoteVersion, _, _) = await kernelService.GetRemoteInfoAsync();
            // fork 仓库 tag 不带 v 前缀，老版本本地号带 v，统一去掉前缀再比对
            var localVer = Version.TryParse(localVersion.Trim().TrimStart('v', 'V'), out var lv) ? lv : null;
            var remoteVer = Version.TryParse(remoteVersion.Trim().TrimStart('v', 'V'), out var rv) ? rv : null;
            if (localVer == null || remoteVer == null || remoteVer <= localVer) return;
            LogService.Info(L.Get("LogCat_Update"), L.Get("App_KernelUpdateFound", localVersion, remoteVersion));
            WeakReferenceMessenger.Default.Send(new KernelUpdateAvailableMessage(localVersion, remoteVersion));
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
        versionStack.Children.Add(BuildVersionRow(L.Get("App_CurrentVersion"), result.CurrentVersion.ToString()));
        versionStack.Children.Add(BuildVersionRow(L.Get("App_LatestVersion"), result.TagName));
        versionCard.Child = versionStack;
        content.Children.Add(versionCard);

        // 更新日志标题
        var logTitle = new TextBlock
        {
            Text = L.Get("App_Changelog"),
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
                Text = L.Get("App_NoChangelog"),
                Opacity = 0.6
            };
        }
        content.Children.Add(logBorder);

        var dialog = new ContentDialog
        {
            Title = result.HasUpdate ? L.Get("App_UpdateAvailableTitle") : L.Get("App_VersionInfoTitle"),
            Content = content,
            PrimaryButtonText = result.HasUpdate ? L.Get("App_UpdateNow") : L.Get("App_OpenDownloadPage"),
            CloseButtonText = L.Get("App_Later"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (result.HasUpdate)
            dialog.SecondaryButtonText = L.Get("App_OpenDownloadPage");

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult == ContentDialogResult.Secondary
            || (!result.HasUpdate && dialogResult == ContentDialogResult.Primary))
            Process.Start(new ProcessStartInfo(result.ReleaseUrl) { UseShellExecute = true });
        else if (result.HasUpdate && dialogResult == ContentDialogResult.Primary)
            await RunAppUpdateAsync(result);
    }

    /// <summary>自更新流程：下载分包→暂存→启动更新器→退出，失败与取消留在框内提示。</summary>
    private static async Task RunAppUpdateAsync(UpdateCheckResult result)
    {
        if (ServiceProvider == null) return;
        IAppUpdateService appUpdate;
        try
        {
            appUpdate = ServiceProvider.GetRequiredService<IAppUpdateService>();
        }
        catch { return; }

        var versionText = new TextBlock
        {
            Text = L.Get("App_VersionRow", result.CurrentVersion, result.TagName),
            Margin = new Thickness(0, 0, 0, 4)
        };
        versionText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var statusText = new TextBlock
        {
            Text = L.Get("App_PreparingUpdate"),
            TextWrapping = TextWrapping.Wrap
        };
        statusText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var progressBar = new System.Windows.Controls.ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            IsIndeterminate = true,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var stack = new StackPanel { MaxWidth = 440 };
        stack.Children.Add(versionText);
        stack.Children.Add(statusText);
        stack.Children.Add(progressBar);

        // 进度框只留关闭键充当取消；成功时随进程退出而消失，无需编程式关闭
        var dialog = new ContentDialog
        {
            Title = L.Get("App_UpdatingTitle"),
            Content = stack,
            CloseButtonText = L.Get("App_Cancel")
        };
        using var cts = new CancellationTokenSource();
        dialog.Closing += (_, _) => { try { cts.Cancel(); } catch { } };
        var showTask = dialog.ShowAsync();

        var status = new Progress<string>(msg => statusText.Text = msg);
        var progress = new Progress<int>(pct =>
        {
            progressBar.IsIndeterminate = false;
            progressBar.Value = pct;
        });
        try
        {
            var staged = await appUpdate.DownloadAndStageAsync(result, status, progress, cts.Token);
            statusText.Text = L.Get("App_StartingUpdater");
            progressBar.IsIndeterminate = true;
            if (!appUpdate.TryStartUpdater(staged))
            {
                statusText.Text = L.Get("App_UpdaterStartFailed");
                progressBar.IsIndeterminate = false;
                progressBar.Value = 0;
                dialog.CloseButtonText = L.Get("App_Close");
                await showTask;
                return;
            }
            // 更新程序已接管，直接退出
            Application.Current?.Shutdown();
            await showTask;
        }
        catch (OperationCanceledException)
        {
            LogService.Info(L.Get("LogCat_Update"), L.Get("App_UserCancelledUpdate"));
            await showTask;
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Update"), L.Get("App_UpdateFailedLog", ex.Message));
            statusText.Text = L.Get("App_UpdateFailedBody", ex.Message);
            progressBar.IsIndeterminate = false;
            progressBar.Value = 0;
            dialog.CloseButtonText = L.Get("App_Close");
            await showTask;
        }
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
        services.AddSingleton<IArchiveImportService, ArchiveImportService>();
        services.AddSingleton<ISteamApiService, SteamApiService>();
        services.AddSingleton<ISteamManifestService, SteamManifestService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IHttpClientProvider, HttpClientProvider>();
        services.AddSingleton<ISteamDepotService, SteamDepotService>();
        services.AddSingleton<ISteamManifestRepoService, SteamManifestRepoService>();
        services.AddSingleton<ISteamAppInfoService, SteamAppInfoService>();
        services.AddSingleton<ISteamAccountService, SteamAccountService>();
        services.AddSingleton<IOpenSteamToolService, OpenSteamToolService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IFeedbackService, FeedbackService>();
        services.AddSingleton<IAppUpdateService, AppUpdateService>();
        services.AddSingleton<ICloudRedirectService, CloudRedirectService>();
        services.AddSingleton<ITrainerService, TrainerService>();
        services.AddSingleton<ITrainerAutoLaunchService, TrainerAutoLaunchService>();
        services.AddSingleton<ISteamAchievementService, SteamAchievementService>();
        services.AddSingleton<SteamTicketExtractor>();
        services.AddSingleton<IAuthorizationService, AuthorizationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IManifestHubService, ManifestHubService>();
        services.AddSingleton<HubManifestFetcher>();
        services.AddSingleton<CodeCdnManifestFetcher>();
        services.AddSingleton<CdnServerListService>();
        services.AddSingleton<HubManifestMonitorService>();
        services.AddSingleton<CodeManifestMonitorService>();
        services.AddSingleton<IManifestHubKeyService, ManifestHubKeyService>();
        services.AddSingleton<IOnlineFixService, OnlineFixService>();
        services.AddSingleton<ICrackToolService, CrackToolService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ScriptDownloadViewModel>();
        services.AddTransient<ExtractionViewModel>();
        services.AddTransient<TrainerViewModel>();
        services.AddTransient<AchievementViewModel>();
        services.AddTransient<AuthorizationViewModel>();
        services.AddTransient<CloudSaveViewModel>();
        services.AddTransient<OnlineFixViewModel>();
        services.AddTransient<CrackToolViewModel>();
        services.AddTransient<ManifestViewModel>();
        services.AddTransient<MainWindow>();
    }
}
