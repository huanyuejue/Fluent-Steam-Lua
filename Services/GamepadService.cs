using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;

namespace SteamLuaManager.Services;

/// <summary>
/// 手柄服务实现：通过 xinput1_4.dll P/Invoke 在后台线程 60Hz 轮询，
/// 完成按钮边沿检测、左摇杆/D-pad 方向 MoveFocus（带节流）、右摇杆控制鼠标光标
/// （停止后聚焦光标下控件）、LB/RB 切换左侧主导航页面（Messenger 通知）、LT/RT 滚动，
/// A/B/X 通过 keybd_event 模拟键盘，Y/Start/Back 通过 Messenger 通知 MainWindow。
/// </summary>
public sealed class GamepadService : IGamepadService, IDisposable
{
    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const int WHEEL_DELTA = 120;

    // XINPUT 按钮位掩码
    private const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001;
    private const ushort XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
    private const ushort XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
    private const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
    private const ushort XINPUT_GAMEPAD_START = 0x0010;
    private const ushort XINPUT_GAMEPAD_BACK = 0x0020;
    private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;  // LB
    private const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200; // RB
    private const ushort XINPUT_GAMEPAD_A = 0x1000;
    private const ushort XINPUT_GAMEPAD_B = 0x2000;
    private const ushort XINPUT_GAMEPAD_X = 0x4000;
    private const ushort XINPUT_GAMEPAD_Y = 0x8000;

    // 虚拟键码
    private const byte VK_RETURN = 0x0D;
    private const byte VK_ESCAPE = 0x1B;
    private const byte VK_F5 = 0x74;

    /// <summary>
    /// 最近一次手柄 A 键物理点击的时间（环境 Tick64，毫秒）。
    /// MainWindow 导航切换时据此区分“手柄 A 点了导航项”与鼠标点击/编程切换，
    /// 仅前者在切页后把焦点与光标带入页面内容区。
    /// </summary>
    public static long LastActivateClickTick { get; private set; }

    /// <summary>
    /// 手柄 A 键按下时在 UI 线程触发（与物理左键点击同时刻）。
    /// MainWindow 据此判断光标是否命中导航项：命中则在点击切页后把焦点/光标带入页面内容区。
    /// </summary>
    public static event Action? ActivatePressed;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    private Thread? _pollThread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private double _sensitivity = 1.0;

    // 上一帧状态（边沿检测）
    private ushort _lastButtons;
    private bool _lastLTOn, _lastRTOn;
    private FocusNavigationDirection? _lastDirection;

    // 右摇杆鼠标状态
    private bool _mouseActive;          // 本轮右摇杆是否已开始推动（未推动过则停止时不做命中聚焦）
    private int _mouseIdleFrames;       // 摇杆回中后累计的空闲帧

    // 节流时间戳
    private DateTime _lastNavTime = DateTime.MinValue;
    private DateTime _lastScrollTime = DateTime.MinValue;
    private const int NavRepeatMs = 280;
    private const int ScrollRepeatMs = 120;
    private const double DeadZone = 0.15; // 左摇杆/D-pad 死区
    private const byte TriggerThreshold = 30; // LT/RT 数字触发阈值
    private const double MouseDeadZone = 0.18; // 右摇杆各轴独立死区
    private const double MouseMaxPixelsPerFrame = 16.0; // 推到底时每帧(~16.7ms)移动像素
    private const double MouseAccelPower = 1.5; // 加速曲线指数：轻推精细、推到底较快
    private const int MouseFocusIdleFrames = 10; // 回中约 160ms 后聚焦光标下控件

    public bool IsRunning => _running;

    public void SetSensitivity(double sensitivity) =>
        _sensitivity = Math.Clamp(sensitivity, 0.5, 2.0);

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _pollThread = new Thread(() => PollLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "GamepadPoll"
        };
        _pollThread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _cts?.Cancel(); } catch { }
        try { _pollThread?.Join(200); } catch { }
        _cts?.Dispose();
        _cts = null;
        _pollThread = null;
    }

    public void Dispose() => Stop();

    private void PollLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _running)
        {
            try
            {
                // ERROR_SUCCESS = 0，无手柄或 DLL 缺失时返回非零，静默跳过
                if (XInputGetState(0, out var state) == 0)
                    ProcessState(state);
            }
            catch
            {
                // xinput1_4.dll 在系统缺失时抛 EntryPointNotFoundException/DllNotFoundException，静默
            }
            try { Thread.Sleep(16); } // ~60Hz
            catch { break; }
        }
    }

    private void ProcessState(XINPUT_STATE state)
    {
        var buttons = state.Gamepad.wButtons;
        var now = DateTime.UtcNow;

        // 按下边沿：本帧为 1 且上一帧为 0
        var pressed = (ushort)(buttons & ~_lastButtons);

        // A → 在鼠标光标当前位置物理左键单击（点哪是哪，由操作系统完成命中，
        // 与真实鼠标点击完全等价：按钮、卡片内按钮、菜单、导航项均可触发）；
        // 同时在 UI 线程广播，MainWindow 用于“点导航项后进入页面内容区”
        if ((pressed & XINPUT_GAMEPAD_A) != 0)
        {
            Dispatch(() => ActivatePressed?.Invoke());
            SendLeftClick();
        }
        // B → Escape（返回/关闭）
        if ((pressed & XINPUT_GAMEPAD_B) != 0)
            SendKey(VK_ESCAPE);
        // X → F5（刷新当前页）
        if ((pressed & XINPUT_GAMEPAD_X) != 0)
            SendKey(VK_F5);
        // Y → 切换导航栏展开/折叠
        if ((pressed & XINPUT_GAMEPAD_Y) != 0)
            Dispatch(() => WeakReferenceMessenger.Default.Send(new GamepadTogglePaneMessage()));

        // Start → 设置页
        if ((pressed & XINPUT_GAMEPAD_START) != 0)
            Dispatch(() => WeakReferenceMessenger.Default.Send(new GamepadNavigateMessage("Settings")));
        // Back → 关于页
        if ((pressed & XINPUT_GAMEPAD_BACK) != 0)
            Dispatch(() => WeakReferenceMessenger.Default.Send(new GamepadNavigateMessage("About")));

        // LB → 左侧主导航上一页，RB → 下一页（按钮边沿触发，按一次切一页）
        if ((pressed & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0)
            Dispatch(() => WeakReferenceMessenger.Default.Send(new GamepadPrevPageMessage()));
        if ((pressed & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0)
            Dispatch(() => WeakReferenceMessenger.Default.Send(new GamepadNextPageMessage()));

        // D-pad / 左摇杆 → 方向导航（边沿立即触发，持续按下按节流重复）
        var dir = GetDirection(state);
        if (dir != null)
        {
            var interval = NavRepeatMs / _sensitivity;
            if (dir != _lastDirection || (now - _lastNavTime).TotalMilliseconds >= interval)
            {
                _lastNavTime = now;
                MoveFocus(dir.Value);
            }
        }
        else
        {
            _lastNavTime = DateTime.MinValue; // 松开后重置，下次按下立即触发
        }
        _lastDirection = dir;

        // LT/RT → 在鼠标光标位置滚动滚轮（MOUSEEVENTF_WHEEL）。
        // 不用 PageUp/PageDown：那要求键盘焦点在内容区 ScrollViewer 内，而 A 键纯鼠标点击后
        // 焦点可能停在导航项；滚轮作用于光标下的可滚动区域，与焦点位置无关。
        var ltOn = state.Gamepad.bLeftTrigger > TriggerThreshold;
        var rtOn = state.Gamepad.bRightTrigger > TriggerThreshold;
        var scrollInterval = ScrollRepeatMs / _sensitivity;
        if (ltOn && (!_lastLTOn || (now - _lastScrollTime).TotalMilliseconds >= scrollInterval))
        {
            _lastScrollTime = now;
            SendWheel(+WHEEL_DELTA); // 向上滚
        }
        if (rtOn && (!_lastRTOn || (now - _lastScrollTime).TotalMilliseconds >= scrollInterval))
        {
            _lastScrollTime = now;
            SendWheel(-WHEEL_DELTA); // 向下滚
        }
        _lastLTOn = ltOn;
        _lastRTOn = rtOn;

        // 右摇杆 → 鼠标光标移动；停止约 160ms 后聚焦光标下控件，使 A 键可激活任意控件
        ProcessRightStickMouse(state);

        _lastButtons = buttons;
    }

    /// <summary>D-pad 优先，其次左摇杆（取最大轴方向，死区 0.15）。</summary>
    private static FocusNavigationDirection? GetDirection(XINPUT_STATE state)
    {
        var b = state.Gamepad.wButtons;
        if ((b & XINPUT_GAMEPAD_DPAD_UP) != 0) return FocusNavigationDirection.Up;
        if ((b & XINPUT_GAMEPAD_DPAD_DOWN) != 0) return FocusNavigationDirection.Down;
        if ((b & XINPUT_GAMEPAD_DPAD_LEFT) != 0) return FocusNavigationDirection.Left;
        if ((b & XINPUT_GAMEPAD_DPAD_RIGHT) != 0) return FocusNavigationDirection.Right;

        var lx = state.Gamepad.sThumbLX;
        var ly = state.Gamepad.sThumbLY;
        var dz = (short)(32767 * DeadZone);
        if (Math.Abs(lx) <= dz && Math.Abs(ly) <= dz) return null;
        // 取主轴方向，避免斜向漂移
        if (Math.Abs(lx) > Math.Abs(ly))
            return lx > 0 ? FocusNavigationDirection.Right : FocusNavigationDirection.Left;
        return ly > 0 ? FocusNavigationDirection.Up : FocusNavigationDirection.Down;
    }

    /// <summary>
    /// 右摇杆控制鼠标：各轴独立死区，重映射到 0~1 后用 1.5 次曲线加速（轻推精确、推到底较快）。
    /// Y 轴取反（摇杆上推对应屏幕向上）。回中静止约 160ms 后命中光标下控件并聚焦。
    /// </summary>
    private void ProcessRightStickMouse(XINPUT_STATE state)
    {
        var nx = state.Gamepad.sThumbRX / 32767.0;
        var nyRaw = state.Gamepad.sThumbRY / 32767.0;

        if (Math.Abs(nx) < MouseDeadZone) nx = 0;
        else nx = (nx - Math.Sign(nx) * MouseDeadZone) / (1 - MouseDeadZone);
        if (Math.Abs(nyRaw) < MouseDeadZone) nyRaw = 0;
        else nyRaw = (nyRaw - Math.Sign(nyRaw) * MouseDeadZone) / (1 - MouseDeadZone);

        if (nx != 0 || nyRaw != 0)
        {
            _mouseActive = true;
            _mouseIdleFrames = 0;

            var dx = (int)Math.Round(Math.Sign(nx) * Math.Pow(Math.Abs(nx), MouseAccelPower) * MouseMaxPixelsPerFrame * _sensitivity);
            // 屏幕坐标 Y 向下为正，摇杆上推 nyRaw 为正需要取反
            var dy = -(int)Math.Round(Math.Sign(nyRaw) * Math.Pow(Math.Abs(nyRaw), MouseAccelPower) * MouseMaxPixelsPerFrame * _sensitivity);
            if (dx != 0 || dy != 0)
            {
                try { mouse_event(MOUSEEVENTF_MOVE, dx, dy, 0, IntPtr.Zero); } catch { }
            }
            return;
        }

        // 摇杆回中：仅在本轮确实推动过右摇杆后，静止若干帧才聚焦（不干扰物理鼠标与左摇杆导航）
        if (!_mouseActive) return;
        _mouseIdleFrames++;
        if (_mouseIdleFrames < MouseFocusIdleFrames) return;

        _mouseActive = false;
        _mouseIdleFrames = 0;
        Dispatch(() =>
        {
            if (Application.Current.MainWindow is Views.MainWindow mw)
                mw.FocusElementAtCursor();
        });
    }

    private static void MoveFocus(FocusNavigationDirection direction)
    {
        Dispatch(() =>
        {
            try
            {
                var focused = Keyboard.FocusedElement as FrameworkElement;
                var request = new TraversalRequest(direction);
                if (focused != null && focused.MoveFocus(request))
                {
                    // 焦点移动后把光标带到新焦点元素中心，保持键盘焦点与鼠标位置一致（A 键目标统一）
                    TryMoveCursorToFocused();
                    return;
                }
                // 无聚焦元素或导航出界：落到当前活动窗口的焦点根，重新开始
                var window = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
                window?.Focus();
            }
            catch { /* 焦点导航失败静默 */ }
        });
    }

    private static void TryMoveCursorToFocused()
    {
        try
        {
            if (Keyboard.FocusedElement is not FrameworkElement fe || !fe.IsVisible) return;
            var center = fe is Window w
                ? w.PointToScreen(new Point(w.ActualWidth / 2, w.ActualHeight / 2))
                : fe.PointToScreen(new Point(fe.ActualWidth / 2, fe.ActualHeight / 2));
            SetCursorPos((int)center.X, (int)center.Y);
        }
        catch { /* 元素尚未布局完成时 PointToScreen 可能抛异常，忽略本次跟随 */ }
    }

    private static void SendKey(byte vk)
    {
        try
        {
            keybd_event(vk, 0, 0, IntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>在光标当前位置发送物理鼠标左键单击（dx/dy=0 表示不移动，原地按下抬起）。</summary>
    private static void SendLeftClick()
    {
        LastActivateClickTick = Environment.TickCount64;
        try
        {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>在光标当前位置发送一格鼠标滚轮（正值向上、负值向下，一格 = WHEEL_DELTA 120）。</summary>
    private static void SendWheel(int delta)
    {
        try { mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)delta, IntPtr.Zero); }
        catch { }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(DispatcherPriority.Input, action);
    }
}
