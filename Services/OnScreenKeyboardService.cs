using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SteamLuaManager.Views;

namespace SteamLuaManager.Services;

/// <summary>
/// 屏幕键盘服务：手柄模式下，任意 TextBox / PasswordBox（含 AutoSuggestBox 内部搜索框）
/// 获得键盘焦点时弹出自研 <see cref="GamepadKeyboardWindow"/>；焦点离开所有输入控件后收起。
/// 用 EventManager 类处理器全局挂载一次，通过 enabled 标志随手柄开关启停。
/// （不使用系统 TabTip.exe：部分精简版 Windows 已移除 TabletInputService，系统键盘无法显示。）
/// </summary>
public static class OnScreenKeyboardService
{
    private static bool _enabled;
    private static bool _registered;
    private static GamepadKeyboardWindow? _keyboard;
    private static DateTime _lastShow = DateTime.MinValue;
    private const int ShowDebounceMs = 300; // 输入框之间快速切换焦点时避免重复唤起

    /// <summary>随“手柄操作”开关启停；关闭时一并收起已弹出的键盘。</summary>
    public static void SetEnabled(bool enabled)
    {
        if (!_registered)
        {
            EventManager.RegisterClassHandler(typeof(TextBox),
                Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotFocus));
            EventManager.RegisterClassHandler(typeof(PasswordBox),
                Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotFocus));
            EventManager.RegisterClassHandler(typeof(TextBox),
                Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostFocus));
            EventManager.RegisterClassHandler(typeof(PasswordBox),
                Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostFocus));
            _registered = true;
        }

        _enabled = enabled;
        if (!enabled) Hide();
    }

    private static void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_enabled || sender is not FrameworkElement { IsVisible: true }) return;
        // 防抖：用户名→密码等输入框间焦点流动时不重复处理
        if ((DateTime.UtcNow - _lastShow).TotalMilliseconds < ShowDebounceMs) return;
        _lastShow = DateTime.UtcNow;
        Show();
    }

    private static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_enabled) return;
        // TextBox ↔ PasswordBox 之间切换时保持键盘
        if (Keyboard.FocusedElement is TextBox or PasswordBox) return;
        // 稍延迟收起：焦点可能只是短暂经过非输入元素（如下拉弹层动画）
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_enabled) return;
            if (Keyboard.FocusedElement is TextBox or PasswordBox) return;
            Hide();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private static void Show()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess()) ShowOnUiThread();
        else dispatcher.BeginInvoke(new Action(ShowOnUiThread));
    }

    private static void ShowOnUiThread()
    {
        try
        {
            var app = Application.Current;
            if (app?.MainWindow == null) return;
            _keyboard ??= new GamepadKeyboardWindow();
            _keyboard.PlaceAtBottom();
            if (!_keyboard.IsVisible)
                _keyboard.Show();
        }
        catch (Exception ex) { LogService.Warn("系统", $"弹出屏幕键盘失败: {ex.Message}"); }
    }

    private static void Hide()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess()) HideOnUiThread();
        else dispatcher.BeginInvoke(new Action(HideOnUiThread));
    }

    private static void HideOnUiThread()
    {
        try { if (_keyboard is { IsVisible: true }) _keyboard.Hide(); }
        catch { }
    }
}
