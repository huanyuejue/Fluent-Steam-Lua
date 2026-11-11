using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FluentLuaBridge;

/// <summary>
/// 入库行为链窗口。整窗是一张圆角卡片：
/// 没有独立标题栏，也没有底部按钮条，关闭入口只保留右上角图标。
/// 结束后停在结果态等用户手动关闭，不自动消失。
/// </summary>
public partial class ImportProgressWindow : Window
{
    private readonly ImportJob _job;
    private NotifyCollectionChangedEventHandler? _stepsChanged;
    private bool _finished;

    public ImportProgressWindow(ImportJob job)
    {
        _job = job;
        InitializeComponent();

        StepsList.ItemsSource = job.Steps;
        GameNameText.Text = string.IsNullOrWhiteSpace(job.DisplayName) ? "读取中…" : job.DisplayName;
        AppIdText.Text = $"AppID {job.AppId}";

        // 保存委托实例，关闭时精确退订；匿名 lambda 会让窗口被任务长期引用
        _stepsChanged = (_, _) => SyncHeader();
        job.Steps.CollectionChanged += _stepsChanged;

        // 无边框窗口自行处理拖动，判定放在按下时，避免吞掉按钮点击
        MouseLeftButtonDown += OnWindowMouseDown;
        Loaded += OnLoaded;
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { /* 从控件上起拖时 WPF 会抛，忽略即可 */ }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Topmost = true;
        Activate();
        FlashWindow();

        // 轻微淡入，避免从 Steam 界面里突然弹出一整块
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    private void FlashWindow()
    {
        try
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = 3,
                dwTimeout = 0
            };
            FlashWindowEx(ref info);
        }
        catch
        {
            // 闪烁只是提示，失败不影响功能
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    private void SyncHeader()
    {
        if (!string.IsNullOrWhiteSpace(_job.DisplayName))
        {
            GameNameText.Text = _job.DisplayName;
        }
    }

    /// <summary>由外层在任务结束后调用，切换到结果态并放开关闭入口。</summary>
    public void Finish()
    {
        _finished = true;
        SyncHeader();

        if (_job.IsSuccess)
        {
            TitleText.Text = "入库完成";
            TitleText.Foreground = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));
        }
        else
        {
            TitleText.Text = string.IsNullOrWhiteSpace(_job.ErrorMessage)
                ? "入库失败"
                : "入库失败 · " + _job.ErrorMessage;
            TitleText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
        }

        CloseButton.IsEnabled = true;
        CloseButton.Focus();
        FlashWindow();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // 未结束前不允许关闭，避免任务在后台孤零零地跑
        if (!_finished) return;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            return;
        }

        // 退订并断开数据源，让任务对象随窗口一起被回收
        if (_stepsChanged != null)
        {
            _job.Steps.CollectionChanged -= _stepsChanged;
            _stepsChanged = null;
        }
        StepsList.ItemsSource = null;

        base.OnClosing(e);
    }
}
