using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using iNKORE.UI.WPF.Modern.Controls;
using Microsoft.Extensions.DependencyInjection;
using SteamLuaManager;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

public partial class AboutView : UserControl, INotifyPropertyChanged
{
    private const string ProjectUrl = "https://github.com/huanyuejue/Fluent-Steam-Lua";
    private const string AfdianUrl = "https://afdian.com/a/HuanJue";
    private const string QqGroupNumber = "1054228162";
    // 群官方 H5 分享链接：浏览器打开后调起 QQ 加群（mqqapi 私有协议新版 QQ 已不支持）
    private const string QqGroupShareUrl = "https://qun.qq.com/universal-share/share?ac=1&authKey=MQ1TVyxN4lUMerzDnHMsl6bp7noFeScwL%2F6C7AhW12PJx9fBlfJt7k%2BJ9uo%2B1SGY&busi_data=eyJncm91cENvZGUiOiIxMDU0MjI4MTYyIiwidG9rZW4iOiJjOGZHRHJKemF4Qytwa1lyMUgxbHdKYnRRcjFqS0plWFBEb3VJVFFqWXc4NzBYUFlMNDlRcUludHJRZ3ovRW5yIiwidWluIjoiNjMwOTExODEzIn0%3D&data=Z5XGMODvXtiqhqhOreDKo3DW9BAsa1W-WfOUjRaP6twkkcvauHBJIJxmahQU2kaAwpyJB5wcxgVqlkIRpQRG1w&svctype=4&tempid=h5_group_info";

    // 附件列表项：存完整路径用于读取，界面只显示文件名
    private sealed class AttachedFileItem
    {
        public AttachedFileItem(string fullPath) { FullPath = fullPath; }
        public string FullPath { get; }
        public override string ToString() => Path.GetFileName(FullPath);
    }

    private readonly string? _version;
    public string VersionText => L.Get("About_Version", _version ?? "1.0.0");

    public event PropertyChangedEventHandler? PropertyChanged;

    private FaqWindow? _faqWindow;

    public AboutView()
    {
        _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        InitializeComponent();
        DataContext = this;
        // View 常驻缓存：语言切换时重算版本行文案，其余动态弹窗在打开时按当前语言生成
        LocalizationService.Instance.PropertyChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VersionText)));
        // 注意：切页时预创建的 View 会触发 Unloaded，不能在这里关 FAQ 窗，
        // 否则开着对照操作一切页就被关掉；窗口 Owner 已设，主窗口退出时会自动联带关闭
    }

    // 常见问题独立窗口：非模态，可开着对照操作；已打开则提到前面，不叠窗口
    private void Faq_Click(object sender, RoutedEventArgs e)
    {
        if (_faqWindow != null)
        {
            try
            {
                if (_faqWindow.IsVisible)
                {
                    _faqWindow.Activate();
                    return;
                }
            }
            catch { }
            _faqWindow = null;
        }
        _faqWindow = new FaqWindow { Owner = Window.GetWindow(this) };
        _faqWindow.Closed += (_, _) => _faqWindow = null;
        _faqWindow.Show();
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
    }

    private void Afdian_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AfdianUrl) { UseShellExecute = true });
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var updateService = App.ServiceProvider?.GetRequiredService<IUpdateService>();
            if (updateService == null) return;
            var result = await updateService.CheckForUpdateAsync();

            if (result.HasUpdate)
                await App.ShowUpdateLogDialogAsync();
            else
                await ShowDialogAsync(L.Get("About_UpdLatestTitle"), L.Get("About_UpdLatestBody", result.CurrentVersion));
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("About_UpdFailTitle"), L.Get("About_UpdFailBody", ex.Message));
        }
    }

    private async void QqGroup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = L.Get("About_QqTitle"),
            Content = new TextBlock
            {
                Text = L.Get("About_QqAsk"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = L.Get("About_QqJump"),
            SecondaryButtonText = L.Get("About_QqCopy"),
            CloseButtonText = L.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            try
            {
                Process.Start(new ProcessStartInfo(QqGroupShareUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(L.Get("About_QqJumpFailTitle"), L.Get("About_QqJumpFailBody", ex.Message, QqGroupNumber));
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            var numberBox = new TextBox
            {
                Text = QqGroupNumber,
                IsReadOnly = true,
                FontSize = 16,
                Padding = new Thickness(8, 6, 8, 6),
                MinWidth = 280
            };
            numberBox.Loaded += (_, _) => { numberBox.Focus(); numberBox.SelectAll(); };
            await new ContentDialog
            {
                Title = L.Get("About_QqCopy"),
                Content = numberBox,
                CloseButtonText = L.Get("App_Close"),
                DefaultButton = ContentDialogButton.Close
            }.ShowAsync();
        }
    }

    private async void Feedback_Click(object sender, RoutedEventArgs e)
    {
        var stack = new StackPanel { Width = 440, MaxWidth = 440 };
        stack.Children.Add(BuildFeedbackLabel(L.Get("About_FbLabelTitle")));
        var titleBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(titleBox);
        stack.Children.Add(BuildFeedbackLabel(L.Get("About_FbLabelBody")));
        var bodyBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(bodyBox);
        stack.Children.Add(BuildFeedbackLabel(L.Get("About_FbLabelContact")));
        var contactBox = new TextBox();
        stack.Children.Add(contactBox);

        var attachedFiles = new ObservableCollection<AttachedFileItem>();
        var logCheck = new CheckBox
        {
            Content = L.Get("About_FbAttachLog"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Add(logCheck);
        var logHint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };
        logHint.SetResourceReference(TextBlock.ForegroundProperty, "AccentFillColorDefaultBrush");
        stack.Children.Add(logHint);

        var attachRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var addButton = new Button { Content = L.Get("About_FbAddAttach"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        var clearButton = new Button { Content = L.Get("About_FbClear"), Padding = new Thickness(16, 6, 16, 6) };
        attachRow.Children.Add(addButton);
        attachRow.Children.Add(clearButton);
        stack.Children.Add(attachRow);
        var sizeText = new TextBlock
        {
            Text = L.Get("About_FbNoAttach"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 4, 0, 0)
        };
        stack.Children.Add(sizeText);
        var fileListScroll = new ScrollViewer
        {
            MaxHeight = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var fileList = new ItemsControl { ItemsSource = attachedFiles };
        fileListScroll.Content = fileList;
        stack.Children.Add(fileListScroll);
        var limitHint = new TextBlock
        {
            Text = L.Get("About_FbLimitHint"),
            Opacity = 0.6,
            Margin = new Thickness(0, 4, 0, 0)
        };
        stack.Children.Add(limitHint);
        logCheck.Checked += (_, _) =>
        {
            logHint.Visibility = Visibility.Collapsed;
            if (!LogService.IsEnabled || !File.Exists(LogService.LogFilePath))
            {
                logCheck.IsChecked = false;
                logHint.Text = L.Get("About_FbNoLog");
                logHint.Visibility = Visibility.Visible;
            }
            RefreshSizeText();
        };
        logCheck.Unchecked += (_, _) =>
        {
            logHint.Visibility = Visibility.Collapsed;
            RefreshSizeText();
        };
        void RefreshSizeText()
        {
            try
            {
                long total = 0;
                foreach (var item in attachedFiles)
                {
                    try { total += new FileInfo(item.FullPath).Length; } catch { }
                }
                try
                {
                    if (logCheck.IsChecked == true && File.Exists(LogService.LogFilePath))
                        total += new FileInfo(LogService.LogFilePath).Length;
                }
                catch { }
                var count = attachedFiles.Count + (logCheck.IsChecked == true ? 1 : 0);
                if (count == 0)
                {
                    sizeText.Text = L.Get("About_FbNoAttach");
                    sizeText.FontWeight = FontWeights.Normal;
                    sizeText.ClearValue(TextBlock.ForegroundProperty);
                    return;
                }
                // 体积按 Base64 膨胀后估算，与发送时校验口径一致；超限仅提示不拦截，选择不受影响
                var overLimit = total * 4L / 3L > FeedbackService.MaxAttachmentsBytes;
                sizeText.Text = overLimit
                    ? L.Get("About_FbSizeOver", count, (total / 1048576.0).ToString("F1"))
                    : L.Get("About_FbSize", count, (total / 1048576.0).ToString("F1"));
                if (overLimit)
                {
                    sizeText.FontWeight = FontWeights.SemiBold;
                    sizeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentFillColorDefaultBrush");
                }
                else
                {
                    sizeText.FontWeight = FontWeights.Normal;
                    sizeText.ClearValue(TextBlock.ForegroundProperty);
                }
            }
            catch { sizeText.Text = L.Get("About_FbNoAttach"); }
        }
        addButton.Click += (_, _) =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = L.Get("About_FbPickTitle"),
                    Multiselect = true,
                    Filter = L.Get("About_FbPickFilter")
                };
                if (dlg.ShowDialog() != true) return;
                foreach (var f in dlg.FileNames)
                {
                    if (!attachedFiles.Any(x => x.FullPath.Equals(f, StringComparison.OrdinalIgnoreCase)))
                        attachedFiles.Add(new AttachedFileItem(f));
                }
                RefreshSizeText();
            }
            catch { }
        };
        clearButton.Click += (_, _) =>
        {
            attachedFiles.Clear();
            RefreshSizeText();
        };

        var dialog = new ContentDialog
        {
            Title = L.Get("About_Feedback"),
            Content = stack,
            PrimaryButtonText = L.Get("About_FbSend"),
            CloseButtonText = L.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var title = titleBox.Text.Trim();
        var body = bodyBox.Text.Trim();
        var contact = contactBox.Text.Trim();
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(body))
        {
            await ShowDialogAsync(L.Get("Main_Tip"), L.Get("About_FbEmptyWarn"));
            return;
        }

        try
        {
            var feedback = App.ServiceProvider?.GetRequiredService<IFeedbackService>();
            if (feedback == null) return;
            await feedback.SubmitAsync(title, body, contact,
                attachLog: logCheck.IsChecked == true,
                attachmentPaths: attachedFiles.Select(x => x.FullPath).ToList());
            await ShowDialogAsync(L.Get("About_FbSentTitle"), L.Get("About_FbSentBody"));
        }
        catch (OperationCanceledException)
        {
            await ShowDialogAsync(L.Get("About_FbFailTitle"), L.Get("About_FbFailTimeout"));
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("About_FbFailTitle"), L.Get("About_FbFailBody", ex.Message));
        }
    }

    private static TextBlock BuildFeedbackLabel(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 4)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return label;
    }

    private static async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            CloseButtonText = L.Get("Common_Confirm"),
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }
}
