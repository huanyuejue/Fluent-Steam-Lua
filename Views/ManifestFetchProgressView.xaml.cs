using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;

namespace SteamLuaManager.Views;

// Manifest 手动获取的进度窗：进度条 + 当前状态 + 取消按钮，调用方用 Report 推进。
public class ManifestFetchProgressView : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _statusText;
    private readonly Button _cancelButton;

    public event EventHandler? CancelRequested;

    public ManifestFetchProgressView(string gameName, string backdropType = "Acrylic10")
    {
        Title = "正在获取 Manifest 清单";
        Width = 440;
        Height = 210;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        if (!Enum.TryParse<BackdropType>(backdropType, true, out var parsedBackdrop))
            parsedBackdrop = BackdropType.Acrylic10;

        WindowHelper.SetUseModernWindowStyle(this, true);
        WindowHelper.SetSystemBackdropType(this, parsedBackdrop);
        WindowHelper.SetCornerStyle(this, WindowCornerStyle.Round);

        var isLight = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light;
        if (parsedBackdrop == BackdropType.None)
        {
            Background = isLight
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5))
                : new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E));
        }
        else
        {
            if (isLight)
            {
                BackdropHelper.RemoveDarkMode(this);
                WindowHelper.SetAcrylic10Color(this, Color.FromArgb(0xF0, 0xF5, 0xF5, 0xF5));
            }
            else
            {
                WindowHelper.SetAcrylic10Color(this, Color.FromArgb(0xCC, 0x1E, 0x1E, 0x1E));
            }
            Background = null;
        }

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        var title = new TextBlock
        {
            Text = $"正在为 {gameName} 获取 Manifest 清单",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 12)
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        stack.Children.Add(title);

        _bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 6, Margin = new Thickness(0, 0, 0, 12) };
        stack.Children.Add(_bar);

        _statusText = new TextBlock
        {
            Text = "准备中...",
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 16)
        };
        _statusText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        stack.Children.Add(_statusText);

        _cancelButton = new Button
        {
            Content = "取消",
            Padding = new Thickness(24, 6, 24, 6),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _cancelButton.Click += (_, _) =>
        {
            _cancelButton.IsEnabled = false;
            _cancelButton.Content = "正在取消...";
            CancelRequested?.Invoke(this, EventArgs.Empty);
        };
        stack.Children.Add(_cancelButton);

        Content = stack;
    }

    public void Report(int done, int total, string text)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                _bar.Maximum = Math.Max(total, 1);
                _bar.Value = Math.Clamp(done, 0, Math.Max(total, 1));
                _statusText.Text = $"[{done}/{total}] {text}";
            });
        }
        catch { }
    }
}
