using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace SteamLuaManager.Views;

/// <summary>
/// 手柄屏幕键盘：不抢焦点（WS_EX_NOACTIVATE）的独立顶层窗口，按键全部不可聚焦，
/// 通过 keybd_event(KEYEVENTF_UNICODE) 把字符注入当前键盘焦点所在的 TextBox/PasswordBox。
/// 操作方式：右摇杆移动鼠标到虚拟键，A 键物理点击。
/// </summary>
public partial class GamepadKeyboardWindow : Window
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_BACK = 0x08;
    private const byte VK_RETURN = 0x0D;
    private const byte VK_LEFT = 0x25;
    private const byte VK_RIGHT = 0x27;

    private bool _shifted;

    public GamepadKeyboardWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // WS_EX_NOACTIVATE：点击虚拟键时窗口不激活，键盘焦点留在原输入框
        // WS_EX_TOOLWINDOW：不在 Alt+Tab/任务栏中出现
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var helper = new WindowInteropHelper(this);
        var style = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>放到主显示器工作区底部居中（避开任务栏），每次显示前重算以适应分辨率变化。</summary>
    public void PlaceAtBottom()
    {
        var work = SystemParameters.WorkArea;
        Width = Math.Min(940, work.Width - 24);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Bottom - Height - 10;
    }

    private void Key_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || tag.Length == 0) return;

        switch (tag)
        {
            case "CLOSE":
                Hide();
                return;
            case "BACK":
                SendVirtualKey(VK_BACK);
                return;
            case "ENTER":
                SendVirtualKey(VK_RETURN);
                return;
            case "LEFT":
                SendVirtualKey(VK_LEFT);
                return;
            case "RIGHT":
                SendVirtualKey(VK_RIGHT);
                return;
            case "SHIFT":
                _shifted = !_shifted;
                ShiftButton.Tag = _shifted ? "SHIFT-ON" : "SHIFT";
                UpdateLetterCase();
                return;
            case "SYM":
                LettersPanel.Visibility = Visibility.Collapsed;
                SymbolsPanel.Visibility = Visibility.Visible;
                return;
            case "ABC":
                SymbolsPanel.Visibility = Visibility.Collapsed;
                LettersPanel.Visibility = Visibility.Visible;
                return;
            default:
                SendChar(_shifted && tag.Length == 1 && char.IsLetter(tag[0])
                    ? char.ToUpper(tag[0]).ToString()
                    : tag);
                // 大写为单次 Shift：输入一个字母后自动回小写（与系统触摸键盘一致）
                if (_shifted && tag.Length == 1 && char.IsLetter(tag[0]))
                {
                    _shifted = false;
                    ShiftButton.Tag = "SHIFT";
                    UpdateLetterCase();
                }
                break;
        }
    }

    private void UpdateLetterCase()
    {
        foreach (var btn in EnumerateButtons(LettersPanel))
        {
            if (btn.Tag is string t && t.Length == 1 && char.IsLetter(t[0]))
                btn.Content = _shifted ? char.ToUpper(t[0]).ToString() : t;
        }
    }

    private static IEnumerable<Button> EnumerateButtons(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button b) yield return b;
            foreach (var nested in EnumerateButtons(child)) yield return nested;
        }
    }

    /// <summary>以 KEYEVENTF_UNICODE 发送任意字符（含空格/符号），直接进入当前焦点输入控件。</summary>
    private static void SendChar(string text)
    {
        if (Keyboard.FocusedElement is not (TextBox or PasswordBox)) return;
        foreach (var ch in text)
        {
            var code = (ushort)ch;
            keybd_event(0, (byte)(code & 0xFF), KEYEVENTF_UNICODE, IntPtr.Zero);
            keybd_event(0, (byte)(code & 0xFF), KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, IntPtr.Zero);
        }
    }

    private static void SendVirtualKey(byte vk)
    {
        keybd_event(vk, 0, 0, IntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
    }
}
