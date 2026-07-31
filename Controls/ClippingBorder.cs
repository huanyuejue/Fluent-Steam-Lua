using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SteamLuaManager.Controls;

public class ClippingBorder : Border
{
    private Rect _lastRect;
    private double _lastRadius;
    private DateTime _lastClickTime;
    private Point _lastClickPosition;

    public static readonly RoutedEvent MouseDoubleClickEvent = EventManager.RegisterRoutedEvent(
        "MouseDoubleClick", RoutingStrategy.Bubble, typeof(MouseButtonEventHandler), typeof(ClippingBorder));

    public event MouseButtonEventHandler MouseDoubleClick
    {
        add => AddHandler(MouseDoubleClickEvent, value);
        remove => RemoveHandler(MouseDoubleClickEvent, value);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    public ClippingBorder()
    {
        SizeChanged += OnSizeChanged;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        var now = DateTime.UtcNow;
        var position = e.GetPosition(this);
        var clickSpeed = SystemParameters.MinimumHorizontalDragDistance * 2;

        if ((now - _lastClickTime).TotalMilliseconds <= GetDoubleClickTime() &&
            (position - _lastClickPosition).Length <= clickSpeed)
        {
            _lastClickTime = default;
            RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
            {
                RoutedEvent = MouseDoubleClickEvent,
                Source = this
            });
        }
        else
        {
            _lastClickTime = now;
            _lastClickPosition = position;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CornerRadius != default && ActualWidth > 0 && ActualHeight > 0)
        {
            var newRect = new Rect(0, 0, ActualWidth, ActualHeight);
            var radius = CornerRadius.TopLeft;

            if (newRect != _lastRect || radius != _lastRadius)
            {
                Clip = new RectangleGeometry(newRect, radius, radius);
                _lastRect = newRect;
                _lastRadius = radius;
            }
        }
    }
}
