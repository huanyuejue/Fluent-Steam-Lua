using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class ScriptDownloadView : UserControl
{
    public ScriptDownloadView()
    {
        InitializeComponent();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e)
    {
        if (DataContext is ScriptDownloadViewModel vm && !string.IsNullOrEmpty(e.QueryText))
        {
            vm.GameId = e.QueryText;
            vm.SearchCommand.Execute(null);
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (DataContext is ScriptDownloadViewModel vm && e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            vm.GameId = sender.Text ?? string.Empty;
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

    private void GameInfoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScriptDownloadViewModel.FoundGame game })
        {
            Process.Start(new ProcessStartInfo($"https://store.steampowered.com/app/{game.AppId}/")
            {
                UseShellExecute = true
            });
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
