using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Interop;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using P = System.Windows.Controls.Primitives;
using W = System.Windows;
using S = System.Windows.Shapes;
using T = System.Windows.Thickness;

namespace SteamLuaManager.Services;

/// <summary>系统托盘图标管理器：WPF Popup 全自绘圆角菜单（参考 FlClash 托盘自绘方案）。</summary>
public sealed class TrayIconManager : IDisposable
{
    // ---- 全局鼠标钩子（用于点击菜单外部时关闭） ----
    private const int WhMouseLl = 14;
    private const int WmLButtonDown = 0x201;
    private const int WmRButtonDown = 0x204;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rectangle rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpPositionFlags = SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpShowWindow;

    private static readonly M.Animation.QuadraticEase MenuEase = new()
    {
        EasingMode = M.Animation.EasingMode.EaseOut
    };

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLowLevelHookStruct
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private static TrayIconManager? _instance;
    private static IntPtr _mouseHook;
    private static readonly LowLevelMouseProc _mouseProc = MouseHookProc;
    private static IntPtr _menuHwnd;
    private static Rectangle _menuBounds;

    private readonly NotifyIcon _notifyIcon;
    private P.Popup? _menuPopup;
    private bool _closing;
    private bool _disposed;
    private bool _balloonShown;

    public event Action? RestoreRequested;
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    public TrayIconManager()
    {
        _instance = this;
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Fluent Steam Lua 管理工具",
            Visible = false
        };
        _notifyIcon.DoubleClick += (_, _) => RestoreRequested?.Invoke();
        _notifyIcon.MouseUp += OnMouseUp;
    }

    public void ShowBalloonOnce(string title, string text)
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(3000);
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
            ShowFluentMenu();
    }

    /// <summary>在鼠标点击位置弹出自绘圆角菜单。</summary>
    private void ShowFluentMenu()
    {
        var app = W.Application.Current;
        if (app == null) return;
        app.Dispatcher.Invoke(() =>
        {
            if (_menuPopup != null || _closing) { CloseMenu(); return; }

            var root = BuildMenuRoot();
            // 先测量获取菜单尺寸，用于定位修正
            root.Measure(new W.Size(double.PositiveInfinity, double.PositiveInfinity));
            var menuW = root.DesiredSize.Width;
            var menuH = root.DesiredSize.Height;

            // 使用与主窗口一致的 DPI 换算（比 GDI 更准，多屏也稳）
            double scaleX = 1.0, scaleY = 1.0;
            try
            {
                if (app.MainWindow is W.UIElement v)
                {
                    var dpi = M.VisualTreeHelper.GetDpi(v);
                    scaleX = dpi.DpiScaleX;
                    scaleY = dpi.DpiScaleY;
                }
            }
            catch { }

            var popup = new P.Popup
            {
                AllowsTransparency = true,
                StaysOpen = true,
                Placement = W.Controls.Primitives.PlacementMode.Absolute,
                Child = root
            };
            _menuPopup = popup;
            popup.Closed += (_, _) =>
            {
                if (ReferenceEquals(_menuPopup, popup)) _menuPopup = null;
            };
            popup.IsOpen = true;

            // 缓存菜单窗口句柄/区域，供外部点击判定
            _menuHwnd = IntPtr.Zero;
            if (W.PresentationSource.FromVisual(root) is HwndSource hs)
            {
                _menuHwnd = hs.Handle;

                // 用物理像素精确摆放：菜单底部紧贴鼠标点击位置（托盘场景鼠标在任务栏内，贴底对齐自然跟随）
                var cursor = new Point(Cursor.Position.X, Cursor.Position.Y);
                var screen = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
                int menuWpx = (int)Math.Round(menuW * scaleX);
                int menuHpx = (int)Math.Round(menuH * scaleY);

                int x;
                if (cursor.X + menuWpx + 8 <= screen.Right)
                    x = cursor.X;
                else
                    x = cursor.X - menuWpx;
                x = Math.Max(screen.Left + 4, x);

                int y = cursor.Y - menuHpx;                    // 底部贴鼠标，向上展开
                y = Math.Max(screen.Top + 4, y);                // 仅保护上边界，无底部钳制

                SetWindowPos(_menuHwnd, IntPtr.Zero, x, y, 0, 0, SwpPositionFlags);

                // 必须在 SetWindowPos 之后再取矩形，保证外部点击判断用真实位置
                if (GetWindowRect(_menuHwnd, out var r)) _menuBounds = r;
            }

            PlayOpenAnimation(root);
            InstallMouseHook();
        });
    }

    /// <summary>关闭菜单：先播放淡出动画再真正关闭。</summary>
    private void CloseMenu()
    {
        var popup = _menuPopup;
        if (popup == null || _closing) return;
        _closing = true;
        _menuPopup = null;
        UnhookMouseHook();

        void Finish()
        {
            try { popup.IsOpen = false; } catch { }
            _closing = false;
        }

        if (popup.Child is W.UIElement child)
        {
            var fade = new M.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
            fade.Completed += (_, _) => Finish();
            child.BeginAnimation(W.UIElement.OpacityProperty, fade);
        }
        else
        {
            Finish();
        }
    }

    /// <summary>呼出动画：淡入 + 轻微上滑。</summary>
    private static void PlayOpenAnimation(W.UIElement root)
    {
        root.Opacity = 0;
        root.RenderTransform = new M.TranslateTransform(0, -6);
        var fade = new M.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = MenuEase
        };
        var slide = new M.Animation.DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = MenuEase
        };
        root.BeginAnimation(W.UIElement.OpacityProperty, fade);
        ((M.TranslateTransform)root.RenderTransform).BeginAnimation(M.TranslateTransform.YProperty, slide);
    }

    /// <summary>安装全局鼠标钩子：点击菜单外部区域时关闭菜单。</summary>
    private static void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule!;
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(module.ModuleName), 0);
        }
        catch { }
    }

    private static void UnhookMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        try { UnhookWindowsHookEx(_mouseHook); } catch { }
        _mouseHook = IntPtr.Zero;
    }

    private static IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is WmLButtonDown or WmRButtonDown)
        {
            var info = Marshal.PtrToStructure<MouseLowLevelHookStruct>(lParam);
            var inside = _menuHwnd != IntPtr.Zero && _menuBounds.Contains(info.X, info.Y);
            if (!inside)
                W.Application.Current?.Dispatcher.BeginInvoke(() => _instance?.CloseMenu());
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private C.Border BuildMenuRoot()
    {
        var bg = FindBrush("SolidBackgroundFillColorBaseBrush");
        var stroke = FindBrush("SurfaceStrokeColorDefaultBrush");
        var textPrimary = FindBrush("TextFillColorPrimaryBrush");
        var hoverBrush = FindBrush("ControlFillColorSecondaryBrush");
        var dividerBrush = FindBrush("DividerStrokeColorDefaultBrush");

        var outer = new C.Border
        {
            Margin = new T(6),
            CornerRadius = new W.CornerRadius(8),
            Background = bg,
            BorderBrush = stroke,
            BorderThickness = new T(1),
            Padding = new T(4),
            Effect = new M.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.18,
                Direction = 270
            }
        };

        var panel = new C.StackPanel();

        // 打开主界面
        panel.Children.Add(BuildMenuItem("\uE80F", "打开主界面", textPrimary, hoverBrush, () => { CloseMenu(); RestoreRequested?.Invoke(); }));

        // 设置
        panel.Children.Add(BuildMenuItem("\uE713", "设置", textPrimary, hoverBrush, () => { CloseMenu(); OpenSettingsRequested?.Invoke(); }));

        // 分隔线
        panel.Children.Add(new S.Rectangle
        {
            Height = 1,
            Margin = new T(8, 3, 8, 3),
            Fill = dividerBrush
        });

        // 退出
        panel.Children.Add(BuildMenuItem("\uE711", "退出", FindBrush("TextFillColorDangerBrush") ?? textPrimary, hoverBrush, () => { CloseMenu(); ExitRequested?.Invoke(); }));

        outer.Child = panel;
        return outer;
    }

    private static C.Border BuildMenuItem(string glyph, string text, M.Brush? foreground, M.Brush? hover, Action onClick)
    {
        var item = new C.Border
        {
            CornerRadius = new W.CornerRadius(4),
            Margin = new T(2, 0, 2, 0),
            Padding = new T(8, 0, 8, 0),
            Height = 30,
            Cursor = W.Input.Cursors.Hand,
            Background = M.Brushes.Transparent
        };

        var row = new C.StackPanel
        {
            Orientation = C.Orientation.Horizontal,
            VerticalAlignment = W.VerticalAlignment.Center
        };
        if (glyph.Length > 0)
        {
            row.Children.Add(new C.TextBlock
            {
                Text = glyph,
                FontFamily = new M.FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Margin = new T(0, 0, 6, 0),
                VerticalAlignment = W.VerticalAlignment.Center,
                Foreground = foreground
            });
        }
        row.Children.Add(new C.TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = W.VerticalAlignment.Center,
            Foreground = foreground
        });
        item.Child = row;

        item.MouseEnter += (_, _) => item.Background = hover;
        item.MouseLeave += (_, _) => item.Background = M.Brushes.Transparent;
        item.MouseLeftButtonUp += (_, _) => onClick();

        return item;
    }

    private static M.Brush? FindBrush(string key)
    {
        try
        {
            return W.Application.Current?.TryFindResource(key) as M.Brush;
        }
        catch { return null; }
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                return Icon.ExtractAssociatedIcon(exePath) ?? SystemIcons.Application;
        }
        catch { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnhookMouseHook();
        _instance = null;
        CloseMenu();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
