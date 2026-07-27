using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class ExtractionView : UserControl
{
    public ExtractionView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            try
            {
                var settings = App.ServiceProvider?.GetService(typeof(ISettingsService)) is ISettingsService s
                    ? s.Load() : null;
                var showInSetting = settings is { ShowCopyLogButton: true };
                if (showInSetting && DataContext is ExtractionViewModel vm)
                {
                    vm.LogLines.CollectionChanged += (_, _) =>
                        CopyLogButton.Visibility = vm.LogLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    CopyLogButton.Visibility = vm.LogLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                    CopyLogButton.Visibility = Visibility.Collapsed;
            }
            catch { CopyLogButton.Visibility = Visibility.Collapsed; }
        };
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExtractionViewModel vm && vm.LogLines.Count > 0)
        {
            try
            {
                var text = string.Join(Environment.NewLine, vm.LogLines);
                Clipboard.SetText(text);
            }
            catch { }
        }
    }

    private void LogScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var innerScroller = (ScrollViewer)sender;
        if ((e.Delta > 0 && innerScroller.VerticalOffset == 0) ||
            (e.Delta < 0 && innerScroller.VerticalOffset >= innerScroller.ScrollableHeight))
        {
            var parent = FindVisualParent<ScrollViewer>((DependencyObject)sender);
            if (parent != null)
            {
                e.Handled = true;
                var newArgs = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent
                };
                parent.RaiseEvent(newArgs);
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null && parent is not T)
            parent = VisualTreeHelper.GetParent(parent);
        return parent as T;
    }
}
