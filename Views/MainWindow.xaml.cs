using System.Diagnostics;
using System.ComponentModel;
using System.Linq;
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
    private readonly string[] _navOrder = ["Home", "ScriptDownload", "Extraction", "Trainer", "Achievement", "Settings", "About"];
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
    private readonly HomeView _homeView;
    private readonly SettingsView _settingsView;
    private readonly ScriptDownloadView _scriptDownloadView;
    private readonly ExtractionView _extractionView;
    private readonly TrainerView _trainerView;
    private readonly AchievementView _achievementView;
    private readonly AboutView _aboutView;
    private readonly IOpenSteamToolService _openSteamToolService;
    private CancellationTokenSource? _kernelCts;
    private TrayIconManager? _trayIcon;
    private bool _exitRequested;

    // 拖拽状态
    private bool _isDragging;
    private Point _dragStartPoint;
    private Border? _accountMenuTrigger;
    private Border? _kernelMenuTrigger;

    // 浮动按钮初始位置（右下角偏移）
    private const double FabInitRight = 24;
    private const double FabInitBottom = 24;
    private const double FabSize = 44;
    private const double FabPanelGap = 8;

    public MainWindow(MainViewModel viewModel, SettingsViewModel settingsViewModel, ScriptDownloadViewModel scriptDownloadViewModel, ExtractionViewModel extractionViewModel, TrainerViewModel trainerViewModel, AchievementViewModel achievementViewModel, ISettingsService settingsService, ISteamPathService steamPathService, IOpenSteamToolService openSteamToolService)
    {
        InitializeComponent();
        CurrentPage = "Home";
        _openSteamToolService = openSteamToolService;
        _dropHintHideTimer.Tick += (_, _) => { _dropHintHideTimer.Stop(); DropHintGrid.Visibility = Visibility.Collapsed; };
        _viewModel = viewModel;
        _settingsViewModel = settingsViewModel;
        _scriptDownloadViewModel = scriptDownloadViewModel;
        _extractionViewModel = extractionViewModel;
        _trainerViewModel = trainerViewModel;
        _achievementViewModel = achievementViewModel;
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
        _aboutView = new AboutView();
        ContentTransition.Content = _homeView;
        SteamMenuList.ItemsSource = new[]
        {
            new FabMenuItem("start", "启动 Steam", "\uE768"),
            FabMenuItem.Separator(),
            new FabMenuItem("restart", "重启 Steam", "\uE777"),
            FabMenuItem.Separator(),
            new FabMenuItem("account", "切换 Steam 账号", "\uE77B", HasSubmenu: true),
            FabMenuItem.Separator(),
            new FabMenuItem("kernel", "OpenSteamTool管理", "\uE737", HasSubmenu: true)
        };

        settingsViewModel.PropertyChanged += SettingsViewModel_PropertyChanged;
        Closed += MainWindow_Closed;
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
            _trayIcon.ShowBalloonOnce("Fluent Steam Lua 管理工具", "程序已最小化到系统托盘，单击托盘图标恢复");
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
                    KernelSubmenu.Visibility = Visibility.Collapsed;
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
        if (_viewModel is IDisposable viewModelDisposable)
            viewModelDisposable.Dispose();
        if (_settingsViewModel is IDisposable settingsViewModelDisposable)
            settingsViewModelDisposable.Dispose();
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
            SteamToolType.OpenSteamTool => "使用 OpenSteamTool 内核",
            SteamToolType.SteamTools => "检测到不适配的 SteamTools",
            _ => "未安装 OpenSteamTool"
        };
        Title = $"Fluent Steam Lua 管理工具 - {status}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.LoadedCommand.CanExecute(null))
            await _viewModel.LoadedCommand.ExecuteAsync(null);

        RefreshTitle();

        var settings = _settingsService.Load();
        UpdateBackdropTheme(ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light);
        UpdateBackdrop(settings.SelectedBackdrop);
        _settingsViewModel.SelectedBackdrop = settings.SelectedBackdrop;
        FabCanvas.Visibility = settings.IsFabVisible ? Visibility.Visible : Visibility.Collapsed;

        switch (_viewModel.OpenSteamToolStatus)
        {
            case "未安装 OpenSteamTool":
                await ShowModernDialogAsync(
                    "未安装 OpenSteamTool",
                    "未检测到 OpenSteamTool，本软件目前仅适配 OpenSteamTool。\n\n" +
                    "请确保已在 Steam 目录中正确安装 OpenSteamTool 后再使用。\n\n" +
                    "可在右下角悬浮按钮中的「OpenSteamTool 管理」中进行安装。");
                break;

            case "检测到不适配的 SteamTools":
                await ShowModernDialogAsync(
                    "不适配的 SteamTools",
                    "检测到 SteamTools（闭源），该内核与本软件不适配。\n\n" +
                    "本软件目前仅适配 OpenSteamTool（开源内核）。\n" +
                    "请卸载 SteamTools 后安装 OpenSteamTool 再使用。");
                break;
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
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private async Task<bool> ShowModernConfirmAsync(string title, string message, string primaryText = "确定", string closeText = "取消")
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
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void ShowKernelOverlay(string status)
    {
        KernelOverlayGrid.Visibility = Visibility.Visible;
        KernelOverlayStatus.Text = status;
        KernelOverlayHint.Visibility = Visibility.Collapsed;
        KernelOverlayProgressBar.Visibility = Visibility.Collapsed;
        KernelOverlayPercent.Visibility = Visibility.Collapsed;
        KernelOverlayRing.IsActive = true;
    }

    private void UpdateKernelOverlayProgress(int percent)
    {
        KernelOverlayRing.IsActive = false;
        KernelOverlayRing.Visibility = Visibility.Collapsed;
        KernelOverlayProgressBar.Visibility = Visibility.Visible;
        KernelOverlayPercent.Visibility = Visibility.Visible;
        KernelOverlayProgressBar.Value = percent;
        KernelOverlayPercent.Text = $"{percent}%";
    }

    private void ShowKernelDownloadHint()
    {
        KernelOverlayHint.Visibility = Visibility.Visible;
    }

    private void HideKernelOverlay()
    {
        KernelOverlayRing.IsActive = false;
        KernelOverlayGrid.Visibility = Visibility.Collapsed;
    }

    private void KernelCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _kernelCts?.Cancel();
    }

    private void UpdateBackdrop(string backdropTypeName)
    {
        if (!Enum.TryParse<BackdropType>(backdropTypeName, true, out var backdropType))
            return;

        WindowHelper.SetSystemBackdropType(this, backdropType);

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
        KernelSubmenu.Background = brush;
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
            source = VisualTreeHelper.GetParent(source);
        }
        AccountSubmenu.Visibility = Visibility.Collapsed;
        KernelSubmenu.Visibility = Visibility.Collapsed;
        ((Storyboard)SteamPanel.Resources["ClosePanel"]).Begin(SteamPanel);
        await Task.Delay(100);
        SteamPanel.Visibility = Visibility.Collapsed;
    }

    private void NavView_SelectionChanged(object sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;

        SteamPanel.Visibility = Visibility.Collapsed;

        if (tag != "ScriptDownload")
        {
            _scriptDownloadViewModel.LogLines.Clear();
            _scriptDownloadViewModel.SearchResults.Clear();
            _scriptDownloadViewModel.StatusMessage = "";
        }
        if (tag != "Extraction")
        {
            _extractionViewModel.LogLines.Clear();
            _extractionViewModel.StatusMessage = "";
        }
        if (tag != "Settings")
        {
            _settingsViewModel.SpeedTestResults.Clear();
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
            "Extraction" => _extractionView,
            "Trainer" => _trainerView,
            "Achievement" => _achievementView,
            "About" => _aboutView,
            _ => null
        };

        if (newView is null || newView == ContentTransition.Content) return;
        ContentTransition.Content = newView;
        CurrentPage = tag;
        LogService.Info("导航", $"切换到 {tag}");

        if (tag == "Trainer")
        {
            _ = _trainerViewModel.LoadSectionsCommand.ExecuteAsync(null);
        }
        else if (tag == "Achievement")
        {
            _ = _achievementViewModel.EnsureLoadedAsync();
        }
    }

    private readonly DispatcherTimer _dropHintHideTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        DropHintGrid.Visibility = Visibility.Visible;
        _dropHintHideTimer.Stop();
        _dropHintHideTimer.Start();
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        _dropHintHideTimer.Stop();
        DropHintGrid.Visibility = Visibility.Collapsed;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            LogService.Info("操作", $"拖拽入库 {files.Length} 个文件: {string.Join("; ", files)}");
            await _viewModel.HandleDropAsync(files);
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
                KernelSubmenu.Visibility = Visibility.Collapsed;
                ShowAccountSubmenu(b);
                break;
            case "kernel":
                _kernelMenuTrigger = b;
                AccountSubmenu.Visibility = Visibility.Collapsed;
                ShowKernelSubmenu(b);
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
        else if (item.Action == "kernel")
            _ = DelayedHideSubmenuAsync(KernelSubmenu, b);
    }

    private async void FabMenuItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: FabMenuItem item }) return;
        LogService.Info("操作", $"悬浮菜单: {item.Header}");

        switch (item.Action)
        {
            case "start":
                SteamPanel.Visibility = Visibility.Collapsed;
                await LaunchSteamAsync();
                break;
            case "restart":
                SteamPanel.Visibility = Visibility.Collapsed;
                KillSteamProcesses();
                await LaunchSteamAsync();
                break;
        }
    }

    private async Task LaunchSteamAsync()
    {
        try
        {
            var path = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(path))
            {
                await ShowModernDialogAsync("提示", "未检测到 Steam 安装路径，请先在设置页面配置");
                return;
            }

            var exePath = System.IO.Path.Combine(path, "steam.exe");
            if (!System.IO.File.Exists(exePath))
            {
                await ShowModernDialogAsync("提示", $"未找到 steam.exe：{exePath}");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"启动 Steam 失败：{ex.Message}");
        }
    }

    private static void KillSteamProcesses()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("steam"))
            {
                if (proc.Id != 0)
                    proc.Kill();
            }
        }
        catch { }
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
        public string AvatarHash { get; set; } = "";
        public string? AvatarPath { get; set; }
        public bool MostRecent { get; set; }
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
            KillSteamProcesses();
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", writable: true))
            {
                if (key == null)
                {
                    await ShowModernDialogAsync("错误", "无法打开注册表 Steam 项");
                    return;
                }
                key.SetValue("AutoLoginUser", target.AccountName, RegistryValueKind.String);
            }
            await LaunchSteamAsync();
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"切换账号失败：{ex.Message}");
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
                    AvatarHash = GetVdfValue(body, "AvatarHash"),
                    MostRecent = GetVdfValue(body, "MostRecent") == "1"
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

    // ========== OpenSteamTool 内核管理 ==========

    private sealed record KernelMenuItem(string Action, string Header, string Description, string IconGlyph, Brush Foreground, Brush DescriptionForeground, bool IsEnabled = true, bool IsSeparator = false)
    {
        public static KernelMenuItem Separator() => new("separator", string.Empty, string.Empty, string.Empty, Brushes.Transparent, Brushes.Transparent, IsSeparator: true);
    }

    private void KernelSubmenu_MouseEnter(object sender, MouseEventArgs e)
    {
    }

    private void KernelSubmenu_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_kernelMenuTrigger != null)
            _ = DelayedHideSubmenuAsync(KernelSubmenu, _kernelMenuTrigger);
    }

    private async void ShowKernelSubmenu(Border trigger)
    {
        var steamPath = _openSteamToolService.GetSteamPath();
        if (steamPath == null)
        {
            KernelSubmenu.Visibility = Visibility.Collapsed;
            return;
        }

        var localVersion = await _openSteamToolService.GetLocalVersionAsync() ?? "未知";
        var isLight = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light;
        var primaryBrush = isLight
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A))
            : new SolidColorBrush(Color.FromArgb(0xFF, 0xE5, 0xE5, 0xE5));
        var criticalBrush = isLight
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0xC0, 0x00, 0x00))
            : new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x55, 0x55));
        var secondaryBrush = isLight
            ? new SolidColorBrush(Color.FromArgb(0x9E, 0x00, 0x00, 0x00))
            : new SolidColorBrush(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF));
        var disabledBrush = isLight
            ? new SolidColorBrush(Color.FromArgb(0x5C, 0x00, 0x00, 0x00))
            : new SolidColorBrush(Color.FromArgb(0x5E, 0xFF, 0xFF, 0xFF));
        var isInstalled = _openSteamToolService.IsInstalled;

        KernelList.ItemsSource = new[]
        {
            new KernelMenuItem("install", "安装", isInstalled ? "已安装" : "下载并安装到 Steam 目录", "\uE737",
                !isInstalled ? primaryBrush : disabledBrush,
                !isInstalled ? secondaryBrush : disabledBrush,
                !isInstalled),
            KernelMenuItem.Separator(),
            new KernelMenuItem("update", "更新", isInstalled ? $"本地版本：{localVersion}" : "请先安装", "\uE777",
                isInstalled ? primaryBrush : disabledBrush,
                isInstalled ? secondaryBrush : disabledBrush,
                isInstalled),
            KernelMenuItem.Separator(),
            new KernelMenuItem("uninstall", "卸载", isInstalled ? "移除 OpenSteamTool" : "未安装", "\uE74D",
                isInstalled ? criticalBrush : disabledBrush,
                isInstalled ? secondaryBrush : disabledBrush,
                isInstalled)
        };

        PositionSubmenuRelative(KernelSubmenu, trigger, KernelList, 180, 160);
        KernelSubmenu.Visibility = Visibility.Visible;
        ((Storyboard)KernelSubmenu.Resources["OpenSubmenu"]).Begin(KernelSubmenu);
    }

    private async void KernelItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not KernelMenuItem item || !item.IsEnabled || item.IsSeparator) return;
        KernelSubmenu.Visibility = Visibility.Collapsed;
        SteamPanel.Visibility = Visibility.Collapsed;
        LogService.Info("操作", $"内核管理: {item.Header}");

        switch (item.Action)
        {
            case "install":
                await InstallKernelAsync();
                break;
            case "update":
                await UpdateKernelAsync();
                break;
            case "uninstall":
                await UninstallKernelAsync();
                break;
        }
    }

    private void KernelMenuItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border { Tag: KernelMenuItem item } border || !item.IsEnabled) return;
        border.Background = (Brush)Application.Current.FindResource("SystemControlHighlightListMediumBrush");
    }

    private async Task InstallKernelAsync()
    {
        if (_openSteamToolService.IsInstalled)
        {
            var confirmed = await ShowModernConfirmAsync(
                "确认安装",
                "OpenSteamTool 已安装，是否仍要重新安装？这将覆盖现有文件。",
                "重新安装");
            if (!confirmed) return;
        }

        try
        {
            var (version, downloadUrl, _) = await _openSteamToolService.GetRemoteInfoAsync();
            if (string.IsNullOrEmpty(downloadUrl))
            {
                await ShowModernDialogAsync("错误", "无法获取最新版本下载链接");
                return;
            }

            ShowKernelOverlay("正在下载 OpenSteamTool...");
            _kernelCts = new CancellationTokenSource();
            try
            {
                var status = new Progress<string>(msg => KernelOverlayStatus.Text = msg);
                var progress = new Progress<int>(pct => UpdateKernelOverlayProgress(pct));
                ShowKernelDownloadHint();
                await _openSteamToolService.InstallAsync(downloadUrl, status, progress, _kernelCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _kernelCts?.Cancel();
                _kernelCts?.Dispose();
                _kernelCts = null;
                HideKernelOverlay();
            }

            await ShowModernDialogAsync("安装完成", $"OpenSteamTool {version} 安装成功！\n请重启 Steam 后生效。");
            RefreshTitle();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"安装失败：{ex.Message}");
        }
    }

    private async Task UpdateKernelAsync()
    {
        if (!_openSteamToolService.IsInstalled)
        {
            await ShowModernDialogAsync("提示", "未检测到 OpenSteamTool，请先安装。");
            return;
        }

        var localVersion = await _openSteamToolService.GetLocalVersionAsync() ?? "未知";
        var localDisplay = localVersion;

        try
        {
            var (remoteVersion, downloadUrl, releaseUrl) = await _openSteamToolService.GetRemoteInfoAsync();
            if (string.IsNullOrEmpty(downloadUrl))
            {
                await ShowModernDialogAsync("错误", "无法获取最新版本信息");
                return;
            }

            if (localVersion != "未知")
            {
                var localVer = Version.TryParse(localVersion, out var lv) ? lv : null;
                var remoteVer = Version.TryParse(remoteVersion, out var rv) ? rv : null;
                if (localVer != null && remoteVer != null && localVer >= remoteVer)
                {
                    await ShowModernDialogAsync("无需更新", $"当前已是最新版本。\n本地：{localVersion}\n仓库：{remoteVersion}");
                    return;
                }
            }

            var updateDialog = new ContentDialog
            {
                Title = "更新可用",
                Content = new TextBlock
                {
                    Text = $"发现新版本！\n\n当前版本：{localDisplay}\n最新版本：{remoteVersion}\n\n是否更新？",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                PrimaryButtonText = "更新",
                SecondaryButtonText = "跳转发布页",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            var dialogResult = await updateDialog.ShowAsync();
            if (dialogResult == ContentDialogResult.Secondary)
            {
                Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
                return;
            }
            if (dialogResult != ContentDialogResult.Primary) return;

            ShowKernelOverlay("正在下载 OpenSteamTool...");
            _kernelCts = new CancellationTokenSource();
            try
            {
                var status = new Progress<string>(msg => KernelOverlayStatus.Text = msg);
                var progress = new Progress<int>(pct => UpdateKernelOverlayProgress(pct));
                ShowKernelDownloadHint();
                await _openSteamToolService.InstallAsync(downloadUrl, status, progress, _kernelCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _kernelCts?.Cancel();
                _kernelCts?.Dispose();
                _kernelCts = null;
                HideKernelOverlay();
            }

            await ShowModernDialogAsync("更新完成", $"已更新至 {remoteVersion}！\n请重启 Steam 后生效。");
            RefreshTitle();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"检查更新失败：{ex.Message}");
        }
    }

    private async Task UninstallKernelAsync()
    {
        if (!_openSteamToolService.IsInstalled)
        {
            await ShowModernDialogAsync("提示", "未检测到已安装的 OpenSteamTool。");
            return;
        }

        var confirmed = await ShowModernConfirmAsync(
            "确认卸载",
            "确定要卸载 OpenSteamTool 吗？\n这将删除以下文件：\n• dwmapi.dll\n• xinput1_4.dll\n• OpenSteamTool.dll",
            "卸载");
        if (!confirmed) return;

        try
        {
            await _openSteamToolService.UninstallAsync();
            await ShowModernDialogAsync("卸载完成", "OpenSteamTool 已卸载。\n重启 Steam 后生效。");
            RefreshTitle();
        }
        catch (Exception ex)
        {
            await ShowModernDialogAsync("错误", $"卸载失败：{ex.Message}");
        }
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

        Canvas.SetLeft(submenu, Math.Max(8, subLeft));
        Canvas.SetTop(submenu, subTop);
    }
}
