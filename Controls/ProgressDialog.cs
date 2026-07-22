using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern.Controls;

namespace SteamLuaManager.Controls;

public class ProgressDialog : Window
{
    private readonly TextBlock _statusText;
    private readonly TextBlock _hintText;
    private readonly TextBlock _percentText;
    private readonly System.Windows.Controls.ProgressBar _progressBar;
    private readonly ProgressRing _progressRing;

    public ProgressDialog(string title, Window owner)
    {
        Title = title;
        Owner = owner;
        Width = 420;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));
        Foreground = Brushes.White;

        var stack = new StackPanel { Margin = new Thickness(20) };

        _statusText = new TextBlock
        {
            Text = "准备中...",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White
        };

        _hintText = new TextBlock
        {
            Text = "若下载过慢可尝试开启代理或 VPN",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Collapsed
        };

        var progressGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _progressBar = new System.Windows.Controls.ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            IsIndeterminate = false,
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(_progressBar, 0);

        _percentText = new TextBlock
        {
            Text = "0%",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Width = 40,
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(_percentText, 1);

        progressGrid.Children.Add(_progressBar);
        progressGrid.Children.Add(_percentText);

        _progressRing = new ProgressRing
        {
            IsActive = true,
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        stack.Children.Add(_statusText);
        stack.Children.Add(_hintText);
        stack.Children.Add(progressGrid);
        stack.Children.Add(_progressRing);
        Content = stack;

        Loaded += (_, _) =>
        {
            if (Owner != null)
            {
                Left = Owner.Left + (Owner.Width - Width) / 2;
                Top = Owner.Top + (Owner.Height - Height) / 2;
            }
        };
    }

    public void SetStatus(string message)
    {
        Dispatcher.Invoke(() => _statusText.Text = message);
    }

    public void SetProgress(int percent)
    {
        Dispatcher.Invoke(() =>
        {
            _progressRing.Visibility = Visibility.Collapsed;
            _progressBar.Visibility = Visibility.Visible;
            _percentText.Visibility = Visibility.Visible;
            _progressBar.Value = percent;
            _percentText.Text = $"{percent}%";
        });
    }

    public void ShowDownloadHint()
    {
        Dispatcher.Invoke(() => _hintText.Visibility = Visibility.Visible);
    }
}
