using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class MainWindow : Window
{
    private readonly string[] _navOrder = ["Home", "ScriptDownload", "Manifest", "Extraction", "Authorization", "Trainer", "Achievement", "CloudSave", "OnlineFix", "CrackTool", "Settings", "About"];
    private string _prevTag = "Home";

    /// <summary>当前页面 tag，供全局操作日志标注上下文。</summary>
    public static string? CurrentPage { get; private set; }

    private readonly MainViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly ISteamPathService _steamPathService;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly ScriptDownloadViewModel _scriptDownloadViewModel;
    private readonly ExtractionViewModel _extractionViewModel;
    private readonly TrainerViewModel _trainerViewModel;
    private readonly AchievementViewModel _achievementViewModel;
    private readonly AuthorizationViewModel _authorizationViewModel;
    private readonly ManifestViewModel _manifestViewModel;
    private readonly CloudSaveViewModel _cloudSaveViewModel;
    private readonly OnlineFixViewModel _onlineFixViewModel;
    private readonly CrackToolViewModel _crackToolViewModel;
    private readonly HomeView _homeView;
    private readonly SettingsView _settingsView;
    private readonly ScriptDownloadView _scriptDownloadView;
    private readonly ExtractionView _extractionView;
    private readonly TrainerView _trainerView;
    private readonly AchievementView _achievementView;
    private readonly AuthorizationView _authorizationView;
    private readonly ManifestView _manifestView;
    private readonly CloudSaveView _cloudSaveView;
    private readonly OnlineFixView _onlineFixView;
    private readonly CrackToolView _crackToolView;
    private readonly AboutView _aboutView;
    private readonly IOpenSteamToolService _openSteamToolService;
    private TrayIconManager? _trayIcon;
    private bool _exitRequested;

    // 拖拽状态
    private bool _isDragging;
    private Point _dragStartPoint;
    private Border? _accountMenuTrigger;

    // 浮动按钮初始位置（右下角偏移）
    private const double FabInitRight = 24;
    private const double FabInitBottom = 24;
    private const double FabSize = 44;
    private const double FabPanelGap = 8;

    // 侧栏最小展开宽度（紧凑模式下的中文基线），以及图标+间距+左右内边距+右侧留白占用宽度
    private const double MinPaneLength = 105;
    private const double PaneChromeWidth = 80;

    public MainWindow(MainViewModel viewModel, SettingsViewModel settingsViewModel, ScriptDownloadViewModel scriptDownloadViewModel, ExtractionViewModel extractionViewModel, TrainerViewModel trainerViewModel, AchievementViewModel achievementViewModel, AuthorizationViewModel authorizationViewModel, ManifestViewModel manifestViewModel, CloudSaveViewModel cloudSaveViewModel, OnlineFixViewModel onlineFixViewModel, CrackToolViewModel crackToolViewModel, ISettingsService settingsService, ISteamPathService steamPathService, IOpenSteamToolService openSteamToolService)
    {
        InitializeComponent();
        CurrentPage = "Home";
        _openSteamToolService = openSteamToolService;
        _dropHintHideTimer.Tick += (_, _) => { _dropHintHideTimer.Stop(); HideDropHint(DropHintGrid); HideDropHint(DropAuthHintGrid); };

        _viewModel = viewModel;
        _settingsViewModel = settingsViewModel;
        _scriptDownloadViewModel = scriptDownloadViewModel;
        _extractionViewModel = extractionViewModel;
        _trainerViewModel = trainerViewModel;
        _achievementViewModel = achievementViewModel;
        _authorizationViewModel = authorizationViewModel;
        _manifestViewModel = manifestViewModel;
        _cloudSaveViewModel = cloudSaveViewModel;
        _onlineFixViewModel = onlineFixViewModel;
        _crackToolViewModel = crackToolViewModel;
        _settingsService = settingsService;
        _steamPathService = steamPathService;
        DataContext = _viewModel;

        var iconUri = new Uri("pack://application:,,,/Assets/app.ico");
        var decoder = BitmapDecoder.Create(iconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bestFrame = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).First();
        Icon = bestFrame;

        _homeView = new HomeView { DataContext = _viewModel };
        _settingsView = new SettingsView { DataContext = settingsViewModel };
        _scriptDownloadView = new ScriptDownloadView { DataContext = scriptDownloadViewModel };
        _extractionView = new ExtractionView { DataContext = extractionViewModel };
        _trainerView = new TrainerView { DataContext = trainerViewModel };
        _achievementView = new AchievementView { DataContext = achievementViewModel };
        _authorizationView = new AuthorizationView { DataContext = authorizationViewModel };
        _manifestView = new ManifestView { DataContext = manifestViewModel };
        _cloudSaveView = new CloudSaveView { DataContext = cloudSaveViewModel };
        _onlineFixView = new OnlineFixView { DataContext = onlineFixViewModel };
        _crackToolView = new CrackToolView { DataContext = crackToolViewModel };
        _aboutView = new AboutView();
        ContentTransition.Content = _homeView;
        BuildSteamMenu();

        settingsViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;
        Closed += MainWindow_Closed;
        // 标题与 FAB 菜单只在窗口构建时求值一次，切语言需重新生成才不会残留旧语言
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        // 用户选择最小化到托盘：拦截关闭并隐藏到系统托盘
        if (!_exitRequested && _settingsService.Load().MinimizeToTray)
        {
            e.Cancel = true;
            EnsureTrayIcon();
            _trayIcon!.Visible = true;
            Hide();
            _trayIcon.ShowBalloonOnce(L.Get("Main_TrayTitle"), L.Get("Main_TrayMinimized"));
            return;
        }

        DisposeTrayIcon();
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null) return;
        _trayIcon = new TrayIconManager();
        _trayIcon.RestoreRequested += RestoreFromTray;
        _trayIcon.OpenSettingsRequested += OpenSettingsFromTray;
        _trayIcon.ExitRequested += () =>
        {
            _exitRequested = true;
            DisposeTrayIcon();
            Application.Current.Shutdown();
        };
    }

    private void OpenSettingsFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        NavView.SelectedItem = SettingsItem;
    }

    /// <summary>供 Key 弹窗等外部调用：切到指定页面（仅支持已知 Tag）。</summary>
    public void NavigateTo(string tag)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                NavView.SelectedItem = tag switch
                {
                    "Home" => HomeItem,
                    "ScriptDownload" => ScriptDownloadItem,
                    "Manifest" => ManifestItem,
                    "Extraction" => ExtractionItem,
                    "Authorization" => AuthorizationItem,
                    "Trainer" => TrainerItem,
                    "Achievement" => AchievementItem,
                    "CloudSave" => CloudSaveItem,
                    "OnlineFix" => OnlineFixItem,
                    "CrackTool" => CrackToolItem,
                    "Settings" => SettingsItem,
                    "About" => AboutItem,
                    _ => null
                };
            });
        }
        catch { }
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon == null) return;
        _trayIcon.RestoreRequested -= RestoreFromTray;
        _trayIcon.OpenSettingsRequested -= OpenSettingsFromTray;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void SettingsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SettingsViewModel svm) return;
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.SelectedBackdrop):
                UpdateBackdrop(svm.SelectedBackdrop);
                break;
            case nameof(SettingsViewModel.IsFabVisible):
                FabCanvas.Visibility = svm.IsFabVisible ? Visibility.Visible : Visibility.Collapsed;
                if (!svm.IsFabVisible)
                {
                    SteamPanel.Visibility = Visibility.Collapsed;
                    AccountSubmenu.Visibility = Visibility.Collapsed;
                }
                break;
            case nameof(SettingsViewModel.IsCardRefreshVisible):
                _viewModel.IsCardRefreshVisible = svm.IsCardRefreshVisible;
                break;
            case nameof(SettingsViewModel.SelectedTheme):
                UpdateBackdropTheme(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light);
                UpdateBackdrop(svm.SelectedBackdrop);
                break;
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settingsViewModel.PropertyChanged -= SettingsViewModel_PropertyChanged;
        LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
        DisposeIfNeeded(_viewModel);
        DisposeIfNeeded(_settingsViewModel);
        DisposeIfNeeded(_scriptDownloadViewModel);
        DisposeIfNeeded(_extractionViewModel);
        DisposeIfNeeded(_trainerViewModel);
        DisposeIfNeeded(_achievementViewModel);
        DisposeIfNeeded(_authorizationViewModel);
        DisposeIfNeeded(_manifestViewModel);
        DisposeIfNeeded(_cloudSaveViewModel);
        DisposeIfNeeded(_onlineFixViewModel);
        DisposeIfNeeded(_crackToolViewModel);
    }

    private static void DisposeIfNeeded(object obj)
    {
        if (obj is IDisposable d) d.Dispose();
    }

    private void RepositionFab()
    {
        if (FabCanvas.ActualWidth <= 0 || FabCanvas.ActualHeight <= 0) return;
        var fabLeft = FabCanvas.ActualWidth - FabInitRight - FabSize;
        var fabTop = FabCanvas.ActualHeight - FabInitBottom - FabSize;
        Canvas.SetLeft(SteamFab, Math.Max(0, fabLeft));
        Canvas.SetTop(SteamFab, Math.Max(0, fabTop));
        PositionSteamPanel();
    }

    private void PositionSteamPanel()
    {
        var panelW = SteamPanel.ActualWidth > 0 ? SteamPanel.ActualWidth : 130;
        var panelH = SteamPanel.ActualHeight > 0 ? SteamPanel.ActualHeight : 120;
        var canvasW = FabCanvas.ActualWidth;
        var canvasH = FabCanvas.ActualHeight;

        var fabLeft = Canvas.GetLeft(SteamFab);
        var fabTop = Canvas.GetTop(SteamFab);
        var fabCenterX = fabLeft + FabSize / 2;

        // 水平：优先居中，右侧溢出则靠右对齐，左侧溢出则靠左
        var panelLeft = fabCenterX - panelW / 2;
        if (panelLeft + panelW > canvasW - FabPanelGap)
            panelLeft = canvasW - panelW - FabPanelGap;
        if (panelLeft < FabPanelGap)
            panelLeft = FabPanelGap;

        // 垂直：优先在 FAB 上方，顶部空间不够则放在下方
        var panelTop = fabTop - FabPanelGap - panelH;
        if (panelTop < FabPanelGap)
            panelTop = fabTop + FabSize + FabPanelGap;

        Canvas.SetLeft(SteamPanel, panelLeft);
        Canvas.SetTop(SteamPanel, panelTop);
    }

    private void SteamPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PositionSteamPanel();
    }

    private void RefreshTitle()
    {
        var status = _steamPathService.DetectSteamToolType() switch
        {
            SteamToolType.OpenSteamTool => L.Get("Home_KernelOST"),
            SteamToolType.SteamTools => L.Get("Home_KernelMismatch", nameof(SteamToolType.SteamTools)),
            SteamToolType.GreenLuma => L.Get("Home_KernelMismatch", nameof(SteamToolType.GreenLuma)),
            _ => L.Get("Home_KernelMissing")
        };
        Title = L.Get("Main_TitleFormat", status);
    }

    // 菜单项文本由 record 固化，切语言时整体重建 ItemsSource 才能替换为新语言
    private void BuildSteamMenu()
    {
        SteamMenuList.ItemsSource = new[]
        {
            new FabMenuItem("start", L.Get("Main_FabStart"), "\uE768"),
            FabMenuItem.Separator(),
            new FabMenuItem("restart", L.Get("Main_FabRestart"), "\uE777"),
            FabMenuItem.Separator(),
            new FabMenuItem("account", L.Get("Main_FabAccount"), "\uE77B", HasSubmenu: true),
            FabMenuItem.Separator(),
            new FabMenuItem("kernel", L.Get("Main_FabKernel"), "\uE737")
        };
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshTitle();
        BuildSteamMenu();
        // 导航项宽度取自 Content（由 {l:Loc} 绑定填充），绑定刷新与本次回调的先后顺序 WPF 不保证，
        // 延后到本轮回调队列末尾再量宽，确保读到的是新语言文本
        Dispatcher.BeginInvoke(new Action(UpdatePaneLength), DispatcherPriority.Loaded);
    }

    // 侧栏展开宽度按当前语言最长标签测算：中文短、英文长，固定值不是挤出省略号就是留一大片空白
    private void UpdatePaneLength()
    {
        var maxLabel = 0.0;
        foreach (var obj in NavView.MenuItems.Cast<object>().Concat(NavView.FooterMenuItems.Cast<object>()))
        {
            if (obj is NavigationViewItem item)
                maxLabel = Math.Max(maxLabel, MeasureNavLabel(item.Content as string));
        }
        NavView.OpenPaneLength = Math.Max(MinPaneLength, Math.Ceiling(maxLabel + PaneChromeWidth));
    }

    // 用与菜单项同一套字体度量文本自然宽度，避免不同语言字形差异带来的估算偏差
    private double MeasureNavLabel(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var probe = new TextBlock
        {
            Text = text,
            FontFamily = HomeItem.FontFamily,
            FontSize = HomeItem.FontSize,
            FontWeight = HomeItem.FontWeight,
            FontStyle = HomeItem.FontStyle,
            FontStretch = HomeItem.FontStretch
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.LoadedCommand.CanExecute(null))
            await _viewModel.LoadedCommand.ExecuteAsync(null);

        RefreshTitle();
        UpdatePaneLength();

        var settings = _settingsService.Load();
        UpdateBackdropTheme(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light);
        UpdateBackdrop(settings.SelectedBackdrop);
        _settingsViewModel.SelectedBackdrop = settings.SelectedBackdrop;
        FabCanvas.Visibility = settings.IsFabVisible ? Visibility.Visible : Visibility.Collapsed;

        var kernelStatus = _viewModel.OpenSteamToolStatus;
        if (kernelStatus == L.Get("Home_KernelMissing"))
        {
            await ShowModernDialogAsync(
                L.Get("Main_KernelMissingTitle"),
                L.Get("Main_KernelMissingBody"));
        }
        else if (kernelStatus == L.Get("Home_KernelMismatch", nameof(SteamToolType.SteamTools)) ||
                 kernelStatus == L.Get("Home_KernelMismatch", nameof(SteamToolType.GreenLuma)))
        {
            var foreignName = kernelStatus.Contains("GreenLuma") ? "GreenLuma" : "SteamTools";
            var goPurify = await ShowModernConfirmAsync(
                L.Get("Main_ForeignTitle", foreignName),
                L.Get("Main_ForeignBody", foreignName),
                L.Get("Main_Purify"));
            if (goPurify)
                OpenSteamToolWindow();
        }

        // OST 已安装时检测逻辑优先返回，开机即便混装了第三方也不会报；
        // 这里单独扫一次，有残留就提示净化（不处理则每次启动都提示，直至清干净）
        if (_openSteamToolService.IsInstalled)
        {
            try
            {
                var foreign = await _openSteamToolService.ScanForeignKernelsAsync();
                if (foreign.Count > 0)
                {
                    var goPurifyMixed = await ShowModernConfirmAsync(
                        L.Get("Main_ConflictTitle"),
                        L.Get("Main_ConflictBody", string.Join("\n", foreign.Select(f => $"• {f.RelativePath}（{f.Owner}）"))),
                        L.Get("Main_Purify"));
                    if (goPurifyMixed)
                        OpenSteamToolWindow();
                }
            }
            catch (Exception ex)
            {
                LogService.Warn(L.Get("LogCat_Kernel"), L.Get("Main_LogConflictScanFailed", ex.Message));
            }
        }

        HomeItem.IsSelected = true;
        RepositionFab();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        if (_settingsViewModel.SelectedTheme != "System") return;

        Dispatcher.Invoke(() =>
        {
            var isLight = GetSystemIsLightTheme();
            ThemeManager.Current.ApplicationTheme = isLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
            UpdateBackdropTheme(isLight);
        });
    }

    private static bool GetSystemIsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 1;
        }
        catch { }
        return false;
    }

    private async Task ShowModernDialogAsync(string title, string message)
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
            CloseButtonText = L.Get("Common_Confirm"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private async Task<bool> ShowModernConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null)
    {
        primaryText ??= L.Get("Common_Confirm");
        closeText ??= L.Get("Common_Cancel");
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
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // 背景材质只影响美观，任何异常都回退纯色不断运行；
    // 材质在部分机器（远程桌面/基础显示驱动/异 DPI）首帧布局阶段翻车，
    // 之前是 XAML 硬编码导致崩在 Show 之前，连兜底都来不及
    public void UpdateBackdrop(string backdropTypeName)
    {
        if (!Enum.TryParse<BackdropType>(backdropTypeName, true, out var backdropType))
            backdropType = BackdropType.None;

        try
        {
            WindowHelper.SetSystemBackdropType(this, backdropType);
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Backdrop"), L.Get("Main_LogBackdropFailed", backdropType, ex.Message));
            backdropType = BackdropType.None;
        }

        if (backdropType == BackdropType.None)
        {
            var isLight = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light;
            Background = isLight
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));
        }
        else
        {
            Background = null;
        }
    }

    private void UpdateBackdropTheme(bool isLight)
    {
        if (isLight)
        {
            BackdropHelper.RemoveDarkMode(this);
            WindowHelper.SetAcrylic10Color(this, Color.FromArgb(0xF0, 0xF5, 0xF5, 0xF5));
        }
        else
        {
            WindowHelper.SetAcrylic10Color(this, Color.FromArgb(0xCC, 0x1E, 0x1E, 0x1E));
            BackdropHelper.ApplyDarkMode(this);
        }
        UpdateFabPanelBackground();
    }

    private void UpdateFabPanelBackground()
    {
        var isLight = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light;
        var color = isLight
            ? Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2)
            : Color.FromArgb(0xFF, 0x2D, 0x2D, 0x2D);
        var brush = new SolidColorBrush(color) { Opacity = 0.85 };
        SteamPanel.Background = brush;
        AccountSubmenu.Background = brush;
        SteamFab.Background = brush;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RepositionFab();
    }

    private async void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SteamPanel.Visibility != Visibility.Visible) return;
        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source == FabCanvas) return;
            source = GetParentSafe(source);
        }
        AccountSubmenu.Visibility = Visibility.Collapsed;
        ((Storyboard)SteamPanel.Resources["ClosePanel"]).Begin(SteamPanel);
        await Task.Delay(100);
        SteamPanel.Visibility = Visibility.Collapsed;
    }

    // OriginalSource 可能是 TextBlock 内的 Run 等 ContentElement（非 Visual），
    // 直接调 VisualTreeHelper.GetParent 会抛"不是 Visual 或 Visual3D"，非 Visual 走逻辑树。
    private static DependencyObject? GetParentSafe(DependencyObject obj)
    {
        try
        {
            if (obj is System.Windows.Media.Visual || obj is System.Windows.Media.Media3D.Visual3D)
                return System.Windows.Media.VisualTreeHelper.GetParent(obj);
        }
        catch { }
        return LogicalTreeHelper.GetParent(obj);
    }

    private void NavView_SelectionChanged(object sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;

        SteamPanel.Visibility = Visibility.Collapsed;

        if (tag != "ScriptDownload")
        {
            // 先取消在飞的入库任务再清 UI，否则后台继续跑但进度无处显示
            _scriptDownloadViewModel.CancelDownload();
            _scriptDownloadViewModel.LogLines.Clear();
            _scriptDownloadViewModel.SearchResults.Clear();
            _scriptDownloadViewModel.ResetSearchState();
            _scriptDownloadViewModel.StatusMessage = "";
        }
        if (tag != "Extraction")
        {
            _extractionViewModel.LogLines.Clear();
            _extractionViewModel.StatusMessage = "";
        }
        if (tag != "OnlineFix")
        {
            _onlineFixViewModel.CancelDownload();
            _onlineFixViewModel.LogLines.Clear();
            _onlineFixViewModel.SearchResults.Clear();
            _onlineFixViewModel.ResetState();
            _onlineFixViewModel.StatusMessage = "";
        }
        if (tag != "CrackTool")
        {
            _crackToolViewModel.CancelCrack();
            _crackToolViewModel.LogLines.Clear();
            _crackToolViewModel.ResetState();
            _crackToolViewModel.StatusMessage = "";
        }
        if (tag != "Settings")
        {
            // 三个测速结果面板的可见性都绑在集合 Count 上，切出设置页统一清空收起
            _settingsViewModel.SpeedTestResults.Clear();
            _settingsViewModel.MirrorSpeedTestResults.Clear();
            _settingsViewModel.ManifestTestResults.Clear();
            _settingsViewModel.StatusMessage = "";
        }
        var prevIndex = Array.IndexOf(_navOrder, _prevTag);
        var newIndex = Array.IndexOf(_navOrder, tag);
        if (prevIndex >= 0 && newIndex >= 0)
        {
            if (newIndex > prevIndex)
            {
                ContentTransition.Transition = TransitionType.Down;
            }
            else if (newIndex < prevIndex)
            {
                ContentTransition.Transition = TransitionType.Up;
            }
        }
        _prevTag = tag;

        SwitchView(tag);
    }

    private void SwitchView(string tag)
    {
        UserControl? newView = tag switch
        {
            "Home" => _homeView,
            "Settings" => _settingsView,
            "ScriptDownload" => _scriptDownloadView,
            "Manifest" => _manifestView,
            "Extraction" => _extractionView,
            "Trainer" => _trainerView,
            "Achievement" => _achievementView,
            "Authorization" => _authorizationView,
            "CloudSave" => _cloudSaveView,
            "OnlineFix" => _onlineFixView,
            "CrackTool" => _crackToolView,
            "About" => _aboutView,
            _ => null
        };

        if (newView is null || newView == ContentTransition.Content) return;
        ContentTransition.Content = newView;
        CurrentPage = tag;
        LogService.Info(L.Get("LogCat_Nav"), L.Get("Main_LogSwitched", tag));

        if (tag == "Trainer")
        {
            _ = _trainerViewModel.LoadSectionsCommand.ExecuteAsync(null);
        }
        else if (tag == "ScriptDownload")
        {
            _scriptDownloadViewModel.OnNavigatedTo();
        }
        else if (tag == "Achievement")
        {
            _ = _achievementViewModel.EnsureLoadedAsync();
        }
        else if (tag == "Manifest")
        {
            _manifestViewModel.OnNavigatedTo();
        }
        else if (tag == "CloudSave")
        {
            _cloudSaveViewModel.OnNavigatedTo();
        }
        else if (tag == "CrackTool")
        {
            _crackToolViewModel.OnNavigatedTo();
        }
    }

    private readonly DispatcherTimer _dropHintHideTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <summary>判断文件是否为授权票据文件（tickets.txt，或内容含票据标记的 .txt）。</summary>
    private static bool IsTicketFile(string path)
    {
        try
        {
            if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(Path.GetFileNameWithoutExtension(path), "tickets",
                    StringComparison.OrdinalIgnoreCase))
                return true;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[1024];
            var n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) return false;
            var head = Encoding.UTF8.GetString(buf, 0, n);
            return head.Contains("appid:", StringComparison.OrdinalIgnoreCase) ||
                   head.Contains("appticket", StringComparison.OrdinalIgnoreCase) ||
                   head.Contains("eticket", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTicketDrop(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        return data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsTicketFile);
    }

    // 拖拽遮罩显示：快速淡入（100ms EaseOut），连续 DragOver 不重启动画
    private static void ShowDropHint(Grid grid)
    {
        if (grid.Visibility == Visibility.Visible) return;
        grid.Opacity = 0;
        grid.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        grid.BeginAnimation(OpacityProperty, fade);
    }

    // 拖拽遮罩隐藏：即时收起并复位动画状态
    private static void HideDropHint(Grid grid)
    {
        grid.Visibility = Visibility.Collapsed;
        grid.BeginAnimation(OpacityProperty, null);
        grid.Opacity = 1;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        var isTicket = IsTicketDrop(e.Data);
        if (isTicket && CurrentPage != "Authorization")
        {
            // 授权票据仅在授权页支持拖拽导入：其他页面不显示遮罩、不响应
            e.Effects = DragDropEffects.None;
            HideDropHint(DropAuthHintGrid);
            HideDropHint(DropHintGrid);
            _dropHintHideTimer.Stop();
            return;
        }
        e.Effects = DragDropEffects.Copy;
        if (isTicket)
        {
            HideDropHint(DropHintGrid);
            ShowDropHint(DropAuthHintGrid);
        }
        else
        {
            HideDropHint(DropAuthHintGrid);
            ShowDropHint(DropHintGrid);
        }
        _dropHintHideTimer.Stop();
        _dropHintHideTimer.Start();
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        _dropHintHideTimer.Stop();
        HideDropHint(DropHintGrid);
        HideDropHint(DropAuthHintGrid);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        _dropHintHideTimer.Stop();
        HideDropHint(DropHintGrid);
        HideDropHint(DropAuthHintGrid);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;

        var tickets = CurrentPage == "Authorization"
            ? files.Where(IsTicketFile).ToArray()
            : Array.Empty<string>();
        var others = files.Where(f => !IsTicketFile(f)).ToArray();

        if (tickets.Length > 0)
        {
            LogService.Info(L.Get("LogCat_Op"), L.Get("Main_LogDragTickets", tickets.Length, string.Join("; ", tickets)));
            foreach (var ticket in tickets)
                await _authorizationViewModel.ImportFileCommand.ExecuteAsync(ticket);
        }
        if (others.Length > 0)
        {
            LogService.Info(L.Get("LogCat_Op"), L.Get("Main_LogDragFiles", others.Length, string.Join("; ", others)));
            await _viewModel.HandleDropAsync(others);
        }
    }

    // ========== 浮动按钮 ==========

    private void SteamFab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _dragStartPoint = e.GetPosition(FabCanvas);
        SteamFab.CaptureMouse();
    }

    private void SteamFab_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(FabCanvas);
        var dx = pos.X - _dragStartPoint.X;
        var dy = pos.Y - _dragStartPoint.Y;

        if (Math.Abs(dx) > 5 || Math.Abs(dy) > 5)
            _isDragging = true;

        if (!_isDragging) return;

        const double margin = 8;
        var newLeft = Canvas.GetLeft(SteamFab) + dx;
        var newTop = Canvas.GetTop(SteamFab) + dy;

        newLeft = Math.Max(margin, Math.Min(FabCanvas.ActualWidth - FabSize - margin, newLeft));
        newTop = Math.Max(margin, Math.Min(FabCanvas.ActualHeight - FabSize - margin, newTop));

        Canvas.SetLeft(SteamFab, newLeft);
        Canvas.SetTop(SteamFab, newTop);
        _dragStartPoint = pos;
        PositionSteamPanel();
    }

    private void SteamFab_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed)
            _isDragging = false;
    }

    private async void SteamFab_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SteamFab.ReleaseMouseCapture();

        if (!_isDragging)
        {
            var show = SteamPanel.Visibility != Visibility.Visible;
            if (show)
            {
                SteamPanel.Visibility = Visibility.Visible;
                PositionSteamPanel();
                ((Storyboard)SteamPanel.Resources["OpenPanel"]).Begin(SteamPanel);
            }
            else
            {
                AccountSubmenu.Visibility = Visibility.Collapsed;
                ((Storyboard)SteamPanel.Resources["ClosePanel"]).Begin(SteamPanel);
                await Task.Delay(100);
                SteamPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void FabMenuItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border b) return;

        b.Background = (Brush)Application.Current.FindResource("SystemControlHighlightListMediumBrush");
        if (b.Tag is not FabMenuItem item) return;

        switch (item.Action)
        {
            case "account":
                _accountMenuTrigger = b;
                ShowAccountSubmenu(b);
                break;
        }
    }

    private void FabMenuItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border b) return;

        b.Background = Brushes.Transparent;
        if (b.Tag is not FabMenuItem item) return;

        if (item.Action == "account")
            _ = DelayedHideSubmenuAsync(AccountSubmenu, b);
    }

    private async void FabMenuItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: FabMenuItem item }) return;
        LogService.Info(L.Get("LogCat_Op"), L.Get("Main_LogFabMenu", item.Header));

        switch (item.Action)
        {
            case "start":
                SteamPanel.Visibility = Visibility.Collapsed;
                var startResult = SteamProcess.LaunchSteam(_steamPathService);
                if (!startResult.Ok)
                    await ShowModernDialogAsync(startResult.Title, startResult.Message);
                break;
            case "restart":
                SteamPanel.Visibility = Visibility.Collapsed;
                var restartResult = SteamProcess.RestartSteam(_steamPathService);
                if (!restartResult.Ok)
                    await ShowModernDialogAsync(restartResult.Title, restartResult.Message);
                break;
            case "kernel":
                SteamPanel.Visibility = Visibility.Collapsed;
                OpenSteamToolWindow();
                break;
        }
    }

    // OST 管理独立成窗后不再走二级菜单：模态打开，同一时刻只允许一个实例
    private void OpenSteamToolWindow()
    {
        var win = new OpenSteamToolWindow(_openSteamToolService, _steamPathService) { Owner = this };
        win.ShowDialog();
    }

    // ========== Steam 账号切换 ==========

    private sealed record FabMenuItem(string Action, string Header, string Glyph, bool HasSubmenu = false, bool IsSeparator = false)
    {
        public static FabMenuItem Separator() => new("separator", string.Empty, string.Empty, IsSeparator: true);
    }

    private class SteamAccount
    {
        public string SteamId { get; set; } = "";
        public string AccountName { get; set; } = "";
        public string PersonaName { get; set; } = "";
        public string? AvatarPath { get; set; }
        public bool IsSeparator { get; set; }

        public static SteamAccount Separator() => new() { IsSeparator = true };
    }

    private List<SteamAccount>? _cachedAccounts;

    private void AccountSubmenu_MouseEnter(object sender, MouseEventArgs e)
    {
        // 鼠标进入子菜单，保持显示
    }

    private void AccountSubmenu_MouseLeave(object sender, MouseEventArgs e)
    {
        // 鼠标离开子菜单，延迟隐藏
        if (_accountMenuTrigger != null)
            _ = DelayedHideSubmenuAsync(AccountSubmenu, _accountMenuTrigger);
    }

    private async Task DelayedHideSubmenuAsync(Border submenu, Border trigger)
    {
        await Task.Delay(200);
        if (submenu.Visibility != Visibility.Visible) return;
        if (submenu.IsMouseOver || trigger.IsMouseOver) return;
        ((Storyboard)submenu.Resources["CloseSubmenu"]).Begin(submenu);
        await Task.Delay(80);
        submenu.Visibility = Visibility.Collapsed;
    }

    private void ShowAccountSubmenu(Border trigger)
    {
        _cachedAccounts = ParseLoginUsersVdf();
        if (_cachedAccounts == null || _cachedAccounts.Count == 0)
        {
            AccountSubmenu.Visibility = Visibility.Collapsed;
            return;
        }

        var steamPath = _steamPathService.DetectSteamPath();
        if (!string.IsNullOrEmpty(steamPath))
        {
            foreach (var acc in _cachedAccounts)
            {
                var avatarPath = System.IO.Path.Combine(steamPath, "config", "avatarcache", $"{acc.SteamId}.png");
                if (System.IO.File.Exists(avatarPath))
                    acc.AvatarPath = avatarPath;
            }
        }

        var displayAccounts = new List<SteamAccount>();
        for (var i = 0; i < _cachedAccounts.Count; i++)
        {
            if (i > 0)
                displayAccounts.Add(SteamAccount.Separator());
            displayAccounts.Add(_cachedAccounts[i]);
        }

        AccountList.ItemsSource = displayAccounts;
        PositionSubmenuRelative(AccountSubmenu, trigger, AccountList, 160, _cachedAccounts.Count * 48 + Math.Max(0, _cachedAccounts.Count - 1) * 5 + 8);
        AccountSubmenu.Visibility = Visibility.Visible;
        ((Storyboard)AccountSubmenu.Resources["OpenSubmenu"]).Begin(AccountSubmenu);
    }

    private async void AccountItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is SteamAccount acc)
        {
            AccountSubmenu.Visibility = Visibility.Collapsed;
            SteamPanel.Visibility = Visibility.Collapsed;
            await SwitchSteamAccountAsync(acc);
        }
    }

    private void FabSubmenuItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
            b.Background = (Brush)Application.Current.FindResource("SystemControlHighlightListMediumBrush");
    }

    private void FabSubmenuItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b)
            b.Background = Brushes.Transparent;
    }

    private async Task SwitchSteamAccountAsync(SteamAccount target)
    {
        try
        {
            SteamProcess.KillSteamProcesses();
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true))
            {
                if (key == null)
                {
                    await ShowModernDialogAsync(L.Get("Main_DialogError"), L.Get("Main_AccRegFailed"));
                    return;
                }
                key.SetValue("AutoLoginUser", target.AccountName, RegistryValueKind.String);
            }
            var launchResult = SteamProcess.LaunchSteam(_steamPathService);
            if (!launchResult.Ok)
                await ShowModernDialogAsync(launchResult.Title, launchResult.Message);
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync(L.Get("Main_DialogError"), L.Get("Main_AccSwitchFailed", ex.Message));
        }
    }

    private List<SteamAccount>? ParseLoginUsersVdf()
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath)) return null;

        var vdfPath = System.IO.Path.Combine(steamPath, "config", "loginusers.vdf");
        if (!System.IO.File.Exists(vdfPath)) return null;

        try
        {
            var content = System.IO.File.ReadAllText(vdfPath);
            var accounts = new List<SteamAccount>();

            foreach (Match blockMatch in Regex.Matches(content, "\\\"(\\d+)\\\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline))
            {
                var body = blockMatch.Groups["body"].Value;
                var accountName = GetVdfValue(body, "AccountName");
                var personaName = GetVdfValue(body, "PersonaName");
                if (string.IsNullOrEmpty(accountName))
                    continue;

                accounts.Add(new SteamAccount
                {
                    SteamId = blockMatch.Groups[1].Value,
                    AccountName = accountName,
                    PersonaName = string.IsNullOrEmpty(personaName) ? accountName : personaName,
                });
            }
            return accounts;
        }
        catch { return null; }
    }

    private static string GetVdfValue(string block, string key)
    {
        var match = Regex.Match(block, $"\\\"{Regex.Escape(key)}\\\"\\s+\\\"([^\\\"]*)\\\"");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private void PositionSubmenuRelative(Border submenu, Border trigger, ItemsControl list, double subW, double subH)
    {
        var panelLeft = Canvas.GetLeft(SteamPanel);
        var panelTop = Canvas.GetTop(SteamPanel);
        var panelW = SteamPanel.ActualWidth > 0 ? SteamPanel.ActualWidth : 130;
        var canvasW = FabCanvas.ActualWidth;
        var canvasH = FabCanvas.ActualHeight;
        var fabTop = Canvas.GetTop(SteamFab);

        var triggerPos = trigger.TranslatePoint(new Point(0, 0), FabCanvas);

        var subLeft = panelLeft + panelW + 4 + subW <= canvasW - 8
            ? panelLeft + panelW + 4
            : panelLeft - subW - 4;

        var subTop = triggerPos.Y;
        bool panelAboveFab = panelTop < fabTop;
        if (panelAboveFab)
        {
            if (subTop + subH >= fabTop)
                subTop = fabTop - subH - 4;
        }
        else
        {
            if (subTop + subH >= canvasH - 8)
                subTop = canvasH - subH - 8;
        }
        subTop = Math.Max(8, subTop);

        // 展开原点跟随实际位置：右侧展开从左边缘长出，左侧展开从右边缘长出
        submenu.RenderTransformOrigin = subLeft > panelLeft ? new Point(0, 0.5) : new Point(1, 0.5);

        Canvas.SetLeft(submenu, Math.Max(8, subLeft));
        Canvas.SetTop(submenu, subTop);
    }
}
