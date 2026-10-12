using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace SteamLuaManager.Controls;

/// <summary>
/// 手柄键盘激活支持：让 Border / TextBlock / Grid 等非 ButtonBase 的鼠标点击区
/// 在获得键盘焦点时响应 Enter/Space，自动触发其鼠标点击事件，使手柄 A 键可操作。
/// 用法：在点击区元素上加 controls:GamepadClick.Enable="True"（元素本身需 Focusable="True"）。
/// 对 ClippingBorder 直接触发其双击事件（一次 A 即等效鼠标双击）。
/// </summary>
public static class GamepadClick
{
    public static readonly DependencyProperty EnableProperty =
        DependencyProperty.RegisterAttached(
            "Enable", typeof(bool), typeof(GamepadClick),
            new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);
    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)e.NewValue)
            element.KeyDown += OnKeyDown;
        else
            element.KeyDown -= OnKeyDown;
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if (sender is not UIElement element) return;

        // 自带键盘激活语义的控件不重复处理（ButtonBase 会自己触发 Click，输入控件保留 Enter 原有行为）
        if (element is ButtonBase or ComboBox or TextBoxBase or ListBoxItem or MenuItem or Expander)
            return;

        var timestamp = Environment.TickCount;

        if (element is ClippingBorder)
        {
            // 卡片双击打开：直接触发 ClippingBorder 的双击路由事件
            element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, timestamp, MouseButton.Left)
            {
                RoutedEvent = ClippingBorder.MouseDoubleClickEvent
            });
        }
        else
        {
            // 普通鼠标点击区：补发 按下+抬起 一对事件
            element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, timestamp, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent
            });
            element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, timestamp, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            });
        }

        e.Handled = true;
    }
}
