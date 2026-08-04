using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        ScrollToTop();
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
            ScrollToTop();
            vm.SearchCommand.Execute(null);
        }
    }

    private void CardsScrollViewer_Loaded(object sender, RoutedEventArgs e)
    {
        // 订阅容器生成完成事件：刷新/排序/搜索会重建 ItemsSource → 容器重新生成 → 此时必然触发封面检测，
        // 不依赖"调度时机恰好布局就绪"的猜测。
        var generator = CardsItemsControl.ItemContainerGenerator;
        generator.StatusChanged -= CardsGenerator_StatusChanged;
        generator.StatusChanged += CardsGenerator_StatusChanged;
        CheckVisibleCovers();
    }

    private bool _coverCheckQueued;

    /// <summary>容器重建完成（列表刷新/排序/搜索后）时兜底触发当前视口封面加载。</summary>
    private void CardsGenerator_StatusChanged(object? sender, EventArgs e)
    {
        if (CardsItemsControl.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
        {
            return;
        }
        CheckVisibleCovers();
        // 容器刚生成时布局（ActualHeight/ViewportHeight）可能尚未就绪，延迟一轮布局完成后再查一次
        QueueCoverCheck();
    }

    /// <summary>布局完成后再补查一次视口封面；布局未就绪（容器刚重建时 ActualHeight=0）则自动重试，
    /// 最多 5 次，覆盖排序/刷新等容器重建的时序窗口。</summary>
    private void QueueCoverCheck(int attempt = 0)
    {
        if (_coverCheckQueued) return;
        _coverCheckQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _coverCheckQueued = false;
            var ok = CheckVisibleCovers();
            if (!ok && attempt < 5)
            {
                QueueCoverCheck(attempt + 1);
            }
        });
    }

    /// <summary>滚动到哪加载到哪：同步检查，避免延迟调度在滚动停止后仍批量加载；接近底部时增量渲染下一页。</summary>
    private void GamesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(sender, CardsScrollViewer))
        {
            CheckVisibleCovers();
        }
        TryLoadMore((ScrollViewer)sender);
    }

    /// <summary>接近底部（还剩 1.5 屏内）时触发增量渲染；LoadMoreAsync 内部有防重入与版本校验。</summary>
    private void TryLoadMore(ScrollViewer scrollViewer)
    {
        if (scrollViewer.ScrollableHeight <= 0) return;
        if (scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset >= scrollViewer.ViewportHeight * 1.5) return;
        if (DataContext is AchievementViewModel vm)
        {
            _ = vm.LoadMoreAsync();
        }
    }

    /// <summary>搜索/排序/切视图后列表重建，回到顶部重新浏览，避免残留偏移直接触发增量加载。</summary>
    private void ScrollToTop()
    {
        CardsScrollViewer?.ScrollToTop();
        ListScrollViewer?.ScrollToTop();
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
    /// 仅触发视口内卡片的封面下载，滚动到哪才加载到哪。返回是否成功执行（false 表示布局未就绪被跳过）。</summary>
    private bool CheckVisibleCovers()
    {
        var scrollViewer = CardsScrollViewer;
        var itemsControl = CardsItemsControl;
        if (scrollViewer == null || itemsControl == null || scrollViewer.ViewportHeight <= 0)
        {
            return false;
        }

        var count = itemsControl.Items.Count;
        if (count == 0)
        {
            return false;
        }

        const double cardWidth = 214;
        var cols = Math.Max(1, (int)(scrollViewer.ViewportWidth / cardWidth));
        if (cols > count) cols = count;

        if (itemsControl.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement first ||
            first.ActualHeight <= 0)
        {
            return false;
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
        return true;
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

        ScrollToTop();

        // 仅在卡片模式下触发封面加载（列表模式不加载封面）
        if (isCardMode)
        {
            CheckVisibleCovers();
            // 刚变为 Visible 时 ViewportHeight 可能尚未布局完成，兜底再查一次
            Dispatcher.BeginInvoke(DispatcherPriority.Background, CheckVisibleCovers);
        }
    }

    /// <summary>排序重排后容器可能复用（AsyncImage 已重置），回顶并等列表重建完成后兜底触发视口封面加载。</summary>
    private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ScrollToTop();
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
