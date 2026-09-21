using System.Collections.ObjectModel;
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

public partial class AboutView : UserControl
{
    private const string ProjectUrl = "https://github.com/huanyuejue/Fluent-Steam-Lua";
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

    public string VersionText { get; }

    public AboutView()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText = version is not null
            ? $"版本 {version.Major}.{version.Minor}.{version.Build}"
            : "版本 1.0.0";
        InitializeComponent();
        DataContext = this;
    }

    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
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
                await ShowDialogAsync("已是最新版本", $"当前已是最新版本：{result.CurrentVersion}");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("检查更新失败", $"无法获取最新版本信息：{ex.Message}");
        }
    }

    private async void QqGroup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "聊天反馈群",
            Content = new TextBlock
            {
                Text = "要加入QQ的聊天反馈群吗？",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            PrimaryButtonText = "跳转群聊",
            SecondaryButtonText = "复制群号",
            CloseButtonText = "取消",
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
                await ShowDialogAsync("跳转失败", $"未能打开加群链接：{ex.Message}\n可复制群号 {QqGroupNumber} 手动加群。");
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
                Title = "复制群号",
                Content = numberBox,
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close
            }.ShowAsync();
        }
    }

    private async void Feedback_Click(object sender, RoutedEventArgs e)
    {
        var stack = new StackPanel { Width = 440, MaxWidth = 440 };
        stack.Children.Add(BuildFeedbackLabel("问题标题"));
        var titleBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(titleBox);
        stack.Children.Add(BuildFeedbackLabel("问题描述"));
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
        stack.Children.Add(BuildFeedbackLabel("联系方式（可选，方便回复）"));
        var contactBox = new TextBox();
        stack.Children.Add(contactBox);

        var attachedFiles = new ObservableCollection<AttachedFileItem>();
        var logCheck = new CheckBox
        {
            Content = "附带运行日志（app.log 完整文件）",
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
        var addButton = new Button { Content = "添加附件", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        var clearButton = new Button { Content = "清空", Padding = new Thickness(16, 6, 16, 6) };
        attachRow.Children.Add(addButton);
        attachRow.Children.Add(clearButton);
        stack.Children.Add(attachRow);
        var sizeText = new TextBlock
        {
            Text = "未选择附件",
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
            Text = "附件总大小上限 40MB（Base64 编码后）",
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
                logHint.Text = "未找到运行日志，请先在设置中打开“日志记录”开关，复现问题后再勾选提交。";
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
                    sizeText.Text = "未选择附件";
                    sizeText.FontWeight = FontWeights.Normal;
                    sizeText.ClearValue(TextBlock.ForegroundProperty);
                    return;
                }
                // 体积按 Base64 膨胀后估算，与发送时校验口径一致；超限仅提示不拦截，选择不受影响
                var overLimit = total * 4L / 3L > FeedbackService.MaxAttachmentsBytes;
                sizeText.Text = overLimit
                    ? $"已选 {count} 个，共 {total / 1048576.0:F1} MB（已超出 40MB 上限，发送前请移除部分文件）"
                    : $"已选 {count} 个，共 {total / 1048576.0:F1} MB";
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
            catch { sizeText.Text = "未选择附件"; }
        }
        addButton.Click += (_, _) =>
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择附件",
                    Multiselect = true,
                    Filter = "所有文件|*.*|图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|日志文本|*.log;*.txt"
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
            Title = "问题反馈",
            Content = stack,
            PrimaryButtonText = "发送",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var title = titleBox.Text.Trim();
        var body = bodyBox.Text.Trim();
        var contact = contactBox.Text.Trim();
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(body))
        {
            await ShowDialogAsync("提示", "请填写标题和问题描述");
            return;
        }

        try
        {
            var feedback = App.ServiceProvider?.GetRequiredService<IFeedbackService>();
            if (feedback == null) return;
            await feedback.SubmitAsync(title, body, contact,
                attachLog: logCheck.IsChecked == true,
                attachmentPaths: attachedFiles.Select(x => x.FullPath).ToList());
            await ShowDialogAsync("发送成功", "反馈已提交，感谢！");
        }
        catch (OperationCanceledException)
        {
            await ShowDialogAsync("发送失败", "请求超时，请检查网络后重试。");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("发送失败", $"{ex.Message}\n可前往 GitHub 提交 Issue。");
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
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }
}
