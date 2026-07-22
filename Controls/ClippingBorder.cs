using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SteamLuaManager.Controls;

public class ClippingBorder : Border
{
    private Rect _lastRect;
    private double _lastRadius;

    public ClippingBorder()
    {
        SizeChanged += OnSizeChanged;
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
