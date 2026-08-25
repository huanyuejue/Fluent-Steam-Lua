using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace SteamLuaManager.Controls;

/// <summary>
/// 平滑滚动 ScrollViewer：滚轮滚动以缓动动画过渡到目标位置，替代默认的逐档跳变。
/// 动画期间再次滚轮会从当前目标继续累加，保证连续滚动手感连贯；
/// 外部代码直接设置偏移（如 ScrollToTop）时会自动取消进行中的动画并同步目标。
/// </summary>
public class SmoothScrollViewer : ScrollViewer
{
    // 动画驱动的中间属性：每帧变化时同步到真实滚动偏移（VerticalOffset 只读，无法直接动画）
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.Register(
        "AnimatedOffset", typeof(double), typeof(SmoothScrollViewer),
        new PropertyMetadata(0.0, (d, e) =>
        {
            var sv = (SmoothScrollViewer)d;
            sv._lastAnimatedValue = (double)e.NewValue;
            sv.ScrollToVerticalOffset((double)e.NewValue);
        }));

    private double _targetOffset;
    private bool _animating;
    private double _lastAnimatedValue;
    private const int AnimationDurationMs = 300;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || ScrollableHeight <= 0) return;

        // 内层 ScrollViewer（日志框等）滚到底后会通过 PreviewMouseWheel 转发，
        // 走 SmoothScrollBy 入口；此处仅处理落在自身内容上的滚轮
        e.Handled = true;
        SmoothScrollBy(-e.Delta);
    }

    /// <summary>按像素增量平滑滚动（正数向下），供嵌套滚动转发等场景调用。</summary>
    public void SmoothScrollBy(double deltaPixels)
    {
        if (ScrollableHeight <= 0) return;

        // 动画进行中从目标位置继续累加，未动画则从当前位置起步
        var current = _animating ? _targetOffset : VerticalOffset;
        _targetOffset = Math.Clamp(current + deltaPixels, 0, ScrollableHeight);
        StartAnimation();
    }

    private void StartAnimation()
    {
        var anim = new DoubleAnimation(_targetOffset, TimeSpan.FromMilliseconds(AnimationDurationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) =>
        {
            _animating = false;
            _targetOffset = VerticalOffset;
        };
        _animating = true;
        BeginAnimation(AnimatedOffsetProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnScrollChanged(ScrollChangedEventArgs e)
    {
        base.OnScrollChanged(e);

        // 动画帧引发的偏移与 _lastAnimatedValue 一致；偏差较大说明是外部滚动
        // （拖动滚动条、ScrollToTop 等），打断动画并同步目标，避免下次滚轮跳变
        if (_animating && Math.Abs(VerticalOffset - _lastAnimatedValue) > 1.5)
        {
            BeginAnimation(AnimatedOffsetProperty, null);
            _animating = false;
            _targetOffset = VerticalOffset;
        }
    }
}
