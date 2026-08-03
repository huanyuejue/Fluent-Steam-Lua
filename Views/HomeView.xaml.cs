using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Text;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern;
using SteamLuaManager.Models;
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

    private void ViewModeContainer_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            fe.IsVisibleChanged += (_, args) =>
            {
                if (args.NewValue is true)
                {
                    fe.Opacity = 0;
                    var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                    fe.BeginAnimation(OpacityProperty, animation);
                }
            };
        }
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
                new CardMenuItem(game.IsDisabled ? "enable" : "disable", game.IsDisabled ? "启用入库" : "禁用入库"),
                CardMenuItem.Separator(),
                new CardMenuItem("edit", "编辑 Lua"),
                CardMenuItem.Separator(),
                new CardMenuItem("delete", "删除 Lua"),
                CardMenuItem.Separator(),
                new CardMenuItem("batchmanage", "批量管理"),
                CardMenuItem.Separator(),
                new CardMenuItem("pin", "版本固定", HasSubmenu: true),
                CardMenuItem.Separator(),
                new CardMenuItem("info", "游戏信息", HasSubmenu: true)
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
            source = VisualTreeHelper.GetParent(source);
        }
        _ = HideCardMenuAsync();
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
        ? [new CardMenuItem("unpin", "取消版本固定")]
        : [new CardMenuItem("pin-latest", "固定到游戏最新版本"), CardMenuItem.Separator(), new CardMenuItem("pin-current", "固定到当前已安装版本")];

    private static CardMenuItem[] BuildInfoSubmenu() =>
        [new CardMenuItem("steamdb", "SteamDB页面"), CardMenuItem.Separator(), new CardMenuItem("store", "Steam商店页面"), CardMenuItem.Separator(), new CardMenuItem("dlc-query", "清单DLC入库查询")];

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
                vm.StatusMessage = ok ? $"已复制 AppID: {game.AppId}" : "复制失败，剪贴板被占用，请重试";
        }
        e.Handled = true;
    }

    private async void GameNameText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameInfo game && !string.IsNullOrEmpty(game.GameName))
        {
            var ok = await CopyToClipboardAsync(game.GameName);
            if (DataContext is MainViewModel vm)
                vm.StatusMessage = ok ? $"已复制游戏名: {game.GameName}" : "复制失败，剪贴板被占用，请重试";
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
