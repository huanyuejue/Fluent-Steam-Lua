using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Controls;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class AchievementView : UserControl
{
    public AchievementView()
    {
        InitializeComponent();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (DataContext is AchievementViewModel vm)
        {
            vm.SearchCommand.Execute(null);
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        sender.ItemsSource = null;
        if (string.IsNullOrEmpty(sender.Text) && DataContext is AchievementViewModel vm)
        {
            vm.SearchCommand.Execute(null);
        }
    }

    private void CardsScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        CheckVisibleCovers();
    }

    /// <summary>滚动到哪加载到哪：同步检查，避免延迟调度在滚动停止后仍批量加载。</summary>
    private void CardsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        CheckVisibleCovers();
    }

    /// <summary>首次变为可见（数据加载完成）时的兜底触发。</summary>
    private void CardsScrollViewer_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            CheckVisibleCovers();
        }
    }

    /// <summary>懒加载：按滚动位置估算当前视口内的卡片索引范围（卡片固定宽 200 + 右边距 14，行高取首个容器），
    /// 仅触发视口内卡片的封面下载，滚动到哪才加载到哪。</summary>
    private void CheckVisibleCovers()
    {
        var scrollViewer = CardsScrollViewer;
        var itemsControl = CardsItemsControl;
        if (scrollViewer == null || itemsControl == null || scrollViewer.ViewportHeight <= 0)
        {
            return;
        }

        var count = itemsControl.Items.Count;
        if (count == 0) return;

        const double cardWidth = 214;
        var cols = Math.Max(1, (int)(scrollViewer.ViewportWidth / cardWidth));
        if (cols > count) cols = count;

        if (itemsControl.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement first ||
            first.ActualHeight <= 0)
        {
            return;
        }
        var rowHeight = first.ActualHeight + 14;

        var firstVisibleRow = Math.Max(0, (int)(scrollViewer.VerticalOffset / rowHeight));
        var visibleRows = (int)Math.Ceiling(scrollViewer.ViewportHeight / rowHeight);

        var start = firstVisibleRow * cols;
        var end = Math.Min(count, (firstVisibleRow + visibleRows) * cols);

        for (var i = start; i < end; i++)
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
            {
                continue;
            }
            if (FindAsyncImage(container) is { } image)
            {
                image.BeginLoad();
            }
        }
    }

    /// <summary>每张卡片仅一个 AsyncImage，找到即返回（短路，避免无谓遍历整个子树）。</summary>
    private static AsyncImage? FindAsyncImage(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is AsyncImage image)
            {
                return image;
            }
            if (FindAsyncImage(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    private void ViewModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CardsScrollViewer == null || ListScrollViewer == null || ViewModeComboBox.SelectedIndex < 0) return;

        var isCardMode = ViewModeComboBox.SelectedIndex == 0;
        CardsScrollViewer.Visibility = isCardMode ? Visibility.Visible : Visibility.Collapsed;
        ListScrollViewer.Visibility = isCardMode ? Visibility.Collapsed : Visibility.Visible;

        // 仅在卡片模式下触发封面加载（列表模式不加载封面）
        if (isCardMode)
        {
            CheckVisibleCovers();
            // 刚变为 Visible 时 ViewportHeight 可能尚未布局完成，兜底再查一次
            Dispatcher.BeginInvoke(DispatcherPriority.Background, CheckVisibleCovers);
        }
    }

    /// <summary>排序重排后容器可能复用（AsyncImage 已重置），等列表重建完成后兜底触发视口封面加载。</summary>
    private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, CheckVisibleCovers);
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, CheckVisibleCovers);
    }

    /// <summary>重新触发视口内封面加载（首次可见时调用），内存缓存命中时立即恢复显示。</summary>
    private void ListItemEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AchievementGameInfo game })
        {
            OpenEditWindow(game);
        }
    }

    private void Card_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AchievementGameInfo game })
        {
            LogService.Info("操作", $"打开成就编辑: {game.Name} (AppID {game.AppId})");
            OpenEditWindow(game);
        }
    }

    private static void OpenEditWindow(AchievementGameInfo game)
    {
        var window = new AchievementEditWindow(game.AppId, game.Name)
        {
            Owner = Application.Current.MainWindow
        };
        window.Show();
    }
}
