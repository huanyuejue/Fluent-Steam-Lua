using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Text;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class HomeView : UserControl
{
    private GameInfo? _activeMenuGame;
    private MainViewModel? _activeMenuViewModel;
    private Border? _cardSubmenuTrigger;
    private Button? _activeMenuButton;

    public HomeView()
    {
        InitializeComponent();
        PreviewMouseLeftButtonDown += HomeView_PreviewMouseLeftButtonDown;
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel oldVm)
                oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            if (e.NewValue is MainViewModel newVm)
                newVm.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    // 筛选/排序/搜索整体替换列表时，给当前可见视图播一次短淡入；
    // 分页追加走 Add 不经过这里，不会打断滚动
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Games)) return;
        FrameworkElement? target = null;
        if (CardScrollViewer.IsVisible) target = CardScrollViewer;
        else if (ListScrollViewer.IsVisible) target = ListScrollViewer;
        else if (TableScrollViewer.IsVisible) target = TableScrollViewer;
        if (target == null) return;
        target.Opacity = 0;
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.SearchText = e.QueryText ?? string.Empty;
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            vm.SearchText = sender.Text ?? string.Empty;
        }
    }

    // 视图切回/切换淡入：每次可见都播 250ms；Loaded 会反复触发，先摘后挂防 handler 越积越多；
    // 注意绝不能动 Tag（三个视图的 Tag 是 Card/List/Table 模式码，供追加门闩识别）
    private void ViewModeContainer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        fe.IsVisibleChanged -= ViewModeContainer_IsVisibleChanged;
        fe.IsVisibleChanged += ViewModeContainer_IsVisibleChanged;
    }

    private void ViewModeContainer_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement fe || e.NewValue is not true) return;
        fe.Opacity = 0;
        var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fe.BeginAnimation(OpacityProperty, animation);
    }

    // 首屏补足：内容撑不满视口（无滚动条、下方留白）时补一页，直到填满或取完；
    // 布局完成后 Extent 变化会再次触发 ScrollChanged，链式补足，不会一次全加
    private void GamesScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TryFillViewport(sender);
    }

    // 分页渐进：无滚动条时补页填满；有滚动条时滚到底部前 600px 追加下一页
    private void GamesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer sv) return;
        if (sv.ScrollableHeight <= 0)
        {
            TryFillViewport(sender);
            return;
        }
        if (sv.VerticalOffset + sv.ViewportHeight < sv.ScrollableHeight - 600) return;
        TryAppendGames(sender);
    }

    private void TryFillViewport(object sender)
    {
        if (sender is not System.Windows.Controls.ScrollViewer sv) return;
        if (!sv.IsVisible || sv.ViewportWidth <= 0 || sv.ViewportHeight <= 0) return;
        if (sv.ScrollableHeight > 0) return;
        TryAppendGames(sender);
    }

    // 分页门闩（按视图隔离）：Extent 单调推进才放行——同一布局周期的重复事件、
    // 切页/启动瞬间的过期小读数到此为止；数量缩水=换了新源，门闩复位
    private readonly Dictionary<object, (double Extent, int Count)> _appendGates = new();
    private bool _fillRecheckQueued;

    private static bool IsKnownViewMode(string? mode) =>
        mode is "Card" or "List" or "Table";

    private void TryAppendGames(object sender)
    {
        if (sender is not System.Windows.Controls.ScrollViewer sv) return;
        if (DataContext is not MainViewModel vm) return;
        // 非当前模式的视图不参与追加（切页/启动瞬间的过期读数到此为止），排一次后台复核防饿死；
        // 模式名对不上已知三态时直接放行，避免手改配置文件导致永久空白
        if (sender is FrameworkElement fe && fe.Tag is string tag
            && IsKnownViewMode(tag) && IsKnownViewMode(vm.SelectedViewMode)
            && !string.Equals(tag, vm.SelectedViewMode, StringComparison.Ordinal))
        {
            QueueFillRecheck(sender);
            return;
        }
        _appendGates.TryGetValue(sender, out var gate);
        if (vm.Games.Count < gate.Count)
            gate = (double.MinValue, vm.Games.Count);
        if (sv.ExtentHeight <= gate.Extent) return;
        vm.LoadMoreGames();
        _appendGates[sender] = (sv.ExtentHeight, vm.Games.Count);
    }

    private void QueueFillRecheck(object sender)
    {
        if (_fillRecheckQueued) return;
        _fillRecheckQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _fillRecheckQueued = false;
            TryFillViewport(sender);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is GameInfo game && DataContext is MainViewModel vm)
        {
            if (CardMenuPanel.Visibility == Visibility.Visible && _activeMenuButton == btn)
            {
                _ = HideCardMenuAsync();
                return;
            }

            _activeMenuGame = game;
            _activeMenuViewModel = vm;
            _activeMenuButton = btn;
            CardSubmenuPanel.Visibility = Visibility.Collapsed;
            CardMenuList.ItemsSource = new[]
            {
                new CardMenuItem(game.IsDisabled ? "enable" : "disable", game.IsDisabled ? L.Get("Home_CardEnable") : L.Get("Home_CardDisable")),
                CardMenuItem.Separator(),
                new CardMenuItem("edit", L.Get("Home_CardEditLua")),
                CardMenuItem.Separator(),
                new CardMenuItem("delete", L.Get("Home_CardDeleteLua")),
                CardMenuItem.Separator(),
                new CardMenuItem("batchmanage", L.Get("Home_CardBatch")),
                CardMenuItem.Separator(),
                new CardMenuItem("pin", L.Get("Home_CardPin"), HasSubmenu: true),
                CardMenuItem.Separator(),
                new CardMenuItem("info", L.Get("Home_CardInfo"), HasSubmenu: true)
            };

            UpdateCardMenuBackground();
            CardMenuPanel.Visibility = Visibility.Hidden;
            PositionCardMenu(btn);
            ShowPanel(CardMenuPanel);
        }
    }

    private void HomeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CardMenuPanel.Visibility != Visibility.Visible) return;

        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source == CardMenuPanel || source == CardSubmenuPanel || source == _activeMenuButton)
                return;
            source = GetParentSafe(source);
        }
        _ = HideCardMenuAsync();
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

    private void PositionCardMenu(Button btn)
    {
        var point = btn.TranslatePoint(new Point(0, btn.ActualHeight + 4), CardMenuCanvas);
        CardMenuPanel.UpdateLayout();
        CardMenuPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var panelWidth = GetMeasuredWidth(CardMenuPanel, 172);
        var panelHeight = GetMeasuredHeight(CardMenuPanel, 180);
        var canvasWidth = CardMenuCanvas.ActualWidth;
        var canvasHeight = CardMenuCanvas.ActualHeight;

        // 默认左对齐到按钮，右侧不足时再向左收进窗口内。
        var left = point.X;
        left = Math.Clamp(left, 8, Math.Max(8, canvasWidth - panelWidth - 8));

        var top = point.Y;
        if (top + panelHeight > canvasHeight - 8)
            top = Math.Max(8, point.Y - btn.ActualHeight - panelHeight - 4);
        top = Math.Clamp(top, 8, Math.Max(8, canvasHeight - panelHeight - 8));

        Canvas.SetLeft(CardMenuPanel, left);
        Canvas.SetTop(CardMenuPanel, top);
    }

    private void PositionCardSubmenu(Border trigger)
    {
        CardSubmenuPanel.UpdateLayout();
        CardSubmenuPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var point = trigger.TranslatePoint(new Point(trigger.ActualWidth + 4, 0), CardMenuCanvas);
        var submenuWidth = GetMeasuredWidth(CardSubmenuPanel, 240);
        var submenuHeight = GetMeasuredHeight(CardSubmenuPanel, 120);
        var canvasWidth = CardMenuCanvas.ActualWidth;
        var canvasHeight = CardMenuCanvas.ActualHeight;
        var menuLeft = Canvas.GetLeft(CardMenuPanel);
        var menuWidth = GetMeasuredWidth(CardMenuPanel, 172);

        var rightLeft = menuLeft + menuWidth + 4;
        var leftLeft = menuLeft - submenuWidth - 4;

        double left;
        if (rightLeft + submenuWidth <= canvasWidth - 8)
            left = rightLeft;
        else if (leftLeft >= 8)
            left = leftLeft;
        else
            left = Math.Clamp(point.X, 8, Math.Max(8, canvasWidth - submenuWidth - 8));

        var top = point.Y;
        top = Math.Clamp(top, 8, Math.Max(8, canvasHeight - submenuHeight - 8));

        Canvas.SetLeft(CardSubmenuPanel, left);
        Canvas.SetTop(CardSubmenuPanel, top);
    }

    private async void CardMenuItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: CardMenuItem item } || item.IsSeparator || item.HasSubmenu) return;
        if (_activeMenuGame == null || _activeMenuViewModel == null) return;

        await HideCardMenuAsync();
        switch (item.Action)
        {
            case "batchmanage":
                _activeMenuViewModel.IsSelectionMode = true;
                _activeMenuGame.IsSelected = true;
                _activeMenuViewModel.NotifySelectionChanged();
                break;
            case "disable":
            case "enable":
                await _activeMenuViewModel.ToggleGameDisableCommand.ExecuteAsync(_activeMenuGame);
                break;
            case "edit":
                _activeMenuViewModel.EditGameCommand.Execute(_activeMenuGame);
                break;
            case "delete":
                await _activeMenuViewModel.DeleteGameCommand.ExecuteAsync(_activeMenuGame);
                break;
        }
    }

    private void CardMenuItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border border) return;
        border.Background = (Brush)Application.Current.FindResource("SystemControlHighlightListMediumBrush");
        if (border.Tag is not CardMenuItem item || !item.HasSubmenu || _activeMenuGame == null) return;

        _cardSubmenuTrigger = border;
        CardSubmenuList.ItemsSource = item.Action == "pin"
            ? BuildPinSubmenu(_activeMenuGame)
            : BuildInfoSubmenu();
        CardSubmenuPanel.Visibility = Visibility.Hidden;
        PositionCardSubmenu(border);
        if (CardSubmenuPanel.Visibility != Visibility.Visible)
            ShowPanel(CardSubmenuPanel);
    }

    private void CardMenuItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            border.Background = Brushes.Transparent;
        if (_cardSubmenuTrigger != null)
            _ = DelayedHideCardSubmenuAsync();
    }

    private void CardSubmenuPanel_MouseEnter(object sender, MouseEventArgs e) { }
    private void CardSubmenuPanel_MouseLeave(object sender, MouseEventArgs e) => _ = DelayedHideCardSubmenuAsync();
    private void CardMenuPanel_MouseLeave(object sender, MouseEventArgs e) => _ = DelayedHideCardMenuAsync();

    private void CardSubmenuItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            border.Background = (Brush)Application.Current.FindResource("SystemControlHighlightListMediumBrush");
    }

    private void CardSubmenuItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            border.Background = Brushes.Transparent;
    }

    private async void CardSubmenuItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: CardMenuItem item } || _activeMenuGame == null || _activeMenuViewModel == null) return;
        await HideCardMenuAsync();

        switch (item.Action)
        {
            case "unpin":
                await _activeMenuViewModel.UnpinGameCommand.ExecuteAsync(_activeMenuGame);
                break;
            case "pin-latest":
                await _activeMenuViewModel.PinToLatestCommand.ExecuteAsync(_activeMenuGame);
                break;
            case "pin-current":
                await _activeMenuViewModel.PinToCurrentCommand.ExecuteAsync(_activeMenuGame);
                break;
            case "steamdb":
                OpenUrl($"https://steamdb.info/app/{_activeMenuGame.AppId}/");
                break;
            case "store":
                OpenUrl($"https://store.steampowered.com/app/{_activeMenuGame.AppId}/");
                break;
            case "dlc-query":
                await _activeMenuViewModel.QueryDlcCommand.ExecuteAsync(_activeMenuGame);
                break;
            case "fetch-manifest":
                await _activeMenuViewModel.FetchManifestsCommand.ExecuteAsync(_activeMenuGame);
                break;
        }
    }

    private async Task DelayedHideCardSubmenuAsync()
    {
        await Task.Delay(200);
        if (CardSubmenuPanel.IsMouseOver || (_cardSubmenuTrigger?.IsMouseOver ?? false)) return;
        await HidePanelAsync(CardSubmenuPanel);
    }

    private async Task DelayedHideCardMenuAsync()
    {
        await Task.Delay(200);
        if (CardMenuPanel.IsMouseOver || CardSubmenuPanel.IsMouseOver) return;
        await HideCardMenuAsync();
    }

    private async Task HideCardMenuAsync()
    {
        await HidePanelAsync(CardSubmenuPanel);
        await HidePanelAsync(CardMenuPanel);
        _activeMenuButton = null;
        _cardSubmenuTrigger = null;
    }

    private static void ShowPanel(Border panel)
    {
        panel.Visibility = Visibility.Visible;
        panel.Opacity = 0;
        if (panel.RenderTransform is ScaleTransform scale)
            scale.ScaleY = 0.92;

        var opacity = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleAnim = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        panel.BeginAnimation(OpacityProperty, opacity);
        (panel.RenderTransform as ScaleTransform)?.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private static double GetMeasuredWidth(FrameworkElement element, double fallback)
    {
        if (element.DesiredSize.Width > 0) return element.DesiredSize.Width;
        if (element.ActualWidth > 0) return element.ActualWidth;
        return fallback;
    }

    private static double GetMeasuredHeight(FrameworkElement element, double fallback)
    {
        if (element.DesiredSize.Height > 0) return element.DesiredSize.Height;
        if (element.ActualHeight > 0) return element.ActualHeight;
        return fallback;
    }

    private static async Task HidePanelAsync(Border panel)
    {
        if (panel.Visibility != Visibility.Visible) return;

        var opacity = new DoubleAnimation(panel.Opacity, 0, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var scaleAnim = new DoubleAnimation(1, 0.92, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        panel.BeginAnimation(OpacityProperty, opacity);
        (panel.RenderTransform as ScaleTransform)?.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        await Task.Delay(100);
        panel.Visibility = Visibility.Collapsed;
        panel.BeginAnimation(OpacityProperty, null);
        if (panel.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleY = 1;
        }
        panel.Opacity = 1;
    }

    private CardMenuItem[] BuildPinSubmenu(GameInfo game) => game.IsManifestPinned
        ? [new CardMenuItem("unpin", L.Get("Home_CardUnpin"))]
        : [new CardMenuItem("pin-latest", L.Get("Home_CardPinLatest")), CardMenuItem.Separator(), new CardMenuItem("pin-current", L.Get("Home_CardPinCurrent"))];

    private static CardMenuItem[] BuildInfoSubmenu() =>
        [new CardMenuItem("steamdb", L.Get("Home_CardSteamDb")), CardMenuItem.Separator(), new CardMenuItem("store", L.Get("Home_CardStore")), CardMenuItem.Separator(), new CardMenuItem("dlc-query", L.Get("Home_CardDlcQuery")), CardMenuItem.Separator(), new CardMenuItem("fetch-manifest", L.Get("Home_CardFetchManifest"))];

    private void UpdateCardMenuBackground()
    {
        var brush = CreateFabPanelBrush();
        CardMenuPanel.Background = brush;
        CardSubmenuPanel.Background = brush;
    }

    private static SolidColorBrush CreateFabPanelBrush()
    {
        var isLight = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light;
        var color = isLight
            ? Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2)
            : Color.FromArgb(0xFF, 0x2D, 0x2D, 0x2D);
        return new SolidColorBrush(color) { Opacity = 0.85 };
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private sealed record CardMenuItem(string Action, string Header, bool HasSubmenu = false, bool IsSeparator = false)
    {
        public static CardMenuItem Separator() => new("separator", string.Empty, IsSeparator: true);
    }

    private void CheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.NotifySelectionChanged();
    }

    private void CheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.NotifySelectionChanged();
    }

    private async void AppIdText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameInfo game)
        {
            var ok = await CopyToClipboardAsync(game.AppId.ToString());
            if (DataContext is MainViewModel vm)
                vm.StatusMessage = ok ? L.Get("Home_CopiedAppId", game.AppId) : L.Get("Home_CopyFailed");
        }
        e.Handled = true;
    }

    private async void GameNameText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameInfo game && !string.IsNullOrEmpty(game.GameName))
        {
            var ok = await CopyToClipboardAsync(game.GameName);
            if (DataContext is MainViewModel vm)
                vm.StatusMessage = ok ? L.Get("Home_CopiedName", game.GameName) : L.Get("Home_CopyFailed");
        }
        e.Handled = true;
    }

    private void CopyableText_MouseEnter(object sender, MouseEventArgs e)
    {
        try
        {
            if (sender is TextBlock tb)
            {
                var brush = TryFindResource("SystemAccentColorBrush") as Brush ?? SystemColors.HighlightBrush;
                tb.Foreground = brush;
            }
        }
        catch { }
    }

    private void CopyableText_MouseLeave(object sender, MouseEventArgs e)
    {
        try
        {
            if (sender is TextBlock tb)
                tb.ClearValue(TextBlock.ForegroundProperty);
        }
        catch { }
    }

    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroinit = 0x0040;
    private const uint CfUnicodeText = 13;

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private static Task<bool> CopyToClipboardAsync(string text)
    {
        return Task.Run(() =>
        {
            var data = Encoding.Unicode.GetBytes(text + "\0");
            for (int i = 0; i < 5; i++)
            {
                if (!OpenClipboard(IntPtr.Zero))
                {
                    Thread.Sleep(40);
                    continue;
                }
                try
                {
                    if (!EmptyClipboard())
                        return false;
                    var hMem = GlobalAlloc(GmemMoveable | GmemZeroinit, (nuint)data.Length);
                    if (hMem == IntPtr.Zero)
                        return false;
                    var pMem = GlobalLock(hMem);
                    if (pMem == IntPtr.Zero)
                    {
                        GlobalFree(hMem);
                        return false;
                    }
                    Marshal.Copy(data, 0, pMem, data.Length);
                    GlobalUnlock(hMem);
                    if (SetClipboardData(CfUnicodeText, hMem) == IntPtr.Zero)
                    {
                        GlobalFree(hMem);
                        return false;
                    }
                    return true;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            return false;
        });
    }
}
