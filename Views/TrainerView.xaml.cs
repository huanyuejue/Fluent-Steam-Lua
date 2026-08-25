using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class TrainerView : UserControl
{
    public TrainerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchContentPanel.IsVisibleChanged += OnContentPanelIsVisibleChanged;
        DownloadContentPanel.IsVisibleChanged += OnContentPanelIsVisibleChanged;
        BindingContentPanel.IsVisibleChanged += OnContentPanelIsVisibleChanged;
        HotTrainersScrollViewer.PreviewMouseWheel += OnNestedScrollViewerPreviewMouseWheel;
        NewReleasesScrollViewer.PreviewMouseWheel += OnNestedScrollViewerPreviewMouseWheel;
    }

    private static void OnContentPanelIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && sender is FrameworkElement element)
        {
            element.Opacity = 0;
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromSeconds(0.2),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(FrameworkElement.OpacityProperty, animation);
        }
    }

    private static void OnNestedScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer innerSv)
        {
            var parent = FindVisualParent<ScrollViewer>(innerSv);
            if (parent != null)
            {
                // 父级为平滑滚动容器时走缓动动画，保持与页面滚动一致的手感
                if (parent is Controls.SmoothScrollViewer smooth)
                    smooth.SmoothScrollBy(-e.Delta);
                else
                    parent.ScrollToVerticalOffset(parent.VerticalOffset - e.Delta);
                e.Handled = true;
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject element) where T : DependencyObject
    {
        while (element != null)
        {
            element = VisualTreeHelper.GetParent(element);
            if (element is T parent)
                return parent;
        }
        return null;
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (DataContext is TrainerViewModel vm && vm.SearchCommand.CanExecute(null))
            vm.SearchCommand.Execute(null);
    }

    private void BindingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is TrainerViewModel vm)
            vm.SaveBindingsCommand.Execute(null);
    }
}