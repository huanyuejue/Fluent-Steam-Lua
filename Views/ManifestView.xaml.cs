using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class ManifestView : UserControl
{
    // 存量 Key 的占位符：只为让框看起来不是空的，保存时靠 _pwdDirty 区分用户是否真改过
    private const string FakePassword = "****************";
    private bool _pwdDirty;
    private ManifestViewModel? _subscribedVm;

    public ManifestView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Loaded += (_, _) =>
        {
            // 已存 Key 就用假内容填充，避免重启后空框让人以为没填过
            if (DataContext is ManifestViewModel vm && vm.HasSavedKey && string.IsNullOrEmpty(ApiKeyBox.Password))
            {
                ApiKeyBox.Password = FakePassword;
                _pwdDirty = false;
            }
            RefreshCopyLogButtonVisibility();
            // 切页每次都会进 Loaded；VM 是单例，先摘旧订阅否则 handler 越积越多
            var logVm = DataContext as ManifestViewModel;
            if (!ReferenceEquals(_subscribedVm, logVm))
            {
                if (_subscribedVm != null)
                {
                    _subscribedVm.HubLogLines.CollectionChanged -= OnLogLinesChanged;
                    _subscribedVm.CodeLogLines.CollectionChanged -= OnLogLinesChanged;
                }
                _subscribedVm = logVm;
                if (_subscribedVm != null)
                {
                    _subscribedVm.HubLogLines.CollectionChanged += OnLogLinesChanged;
                    _subscribedVm.CodeLogLines.CollectionChanged += OnLogLinesChanged;
                }
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 与修改器/联机页一致：内容块变为可见时播一次短淡入；切页每次都会进 Loaded，先摘后挂
        CodeContentPanel.IsVisibleChanged -= OnContentPanelIsVisibleChanged;
        HubContentPanel.IsVisibleChanged -= OnContentPanelIsVisibleChanged;
        CodeContentPanel.IsVisibleChanged += OnContentPanelIsVisibleChanged;
        HubContentPanel.IsVisibleChanged += OnContentPanelIsVisibleChanged;
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

    private void OnLogLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RefreshCopyLogButtonVisibility();

    // 复制日志按钮跟随设置开关与日志条数，和入库/提取界面保持一致
    private void RefreshCopyLogButtonVisibility()
    {
        try
        {
            var settings = App.ServiceProvider?.GetService(typeof(ISettingsService)) is ISettingsService s
                ? s.Load() : null;
            var showInSetting = settings is { ShowCopyLogButton: true };
            if (DataContext is not ManifestViewModel vm)
            {
                CopyCodeLogButton.Visibility = Visibility.Collapsed;
                CopyHubLogButton.Visibility = Visibility.Collapsed;
                return;
            }
            CopyCodeLogButton.Visibility = showInSetting && vm.CodeLogLines.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
            CopyHubLogButton.Visibility = showInSetting && vm.HubLogLines.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            CopyCodeLogButton.Visibility = Visibility.Collapsed;
            CopyHubLogButton.Visibility = Visibility.Collapsed;
        }
    }

    private void CopyCodeLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ManifestViewModel vm && vm.CodeLogLines.Count > 0)
        {
            try
            {
                var text = string.Join(Environment.NewLine, vm.CodeLogLines);
                Clipboard.SetText(text);
            }
            catch { }
        }
    }

    private void CopyHubLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ManifestViewModel vm && vm.HubLogLines.Count > 0)
        {
            try
            {
                var text = string.Join(Environment.NewLine, vm.HubLogLines);
                Clipboard.SetText(text);
            }
            catch { }
        }
    }

    private void ApiKeyBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        _pwdDirty = true;
    }

    private void SaveKeyButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ManifestViewModel vm && vm.SaveApiKeyCommand.CanExecute(_pwdDirty ? ApiKeyBox.Password : null))
        {
            vm.SaveApiKeyCommand.Execute(_pwdDirty ? ApiKeyBox.Password : null);
            _pwdDirty = false;
        }
    }

    private void GetKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://manifesthub2.filegear-sg.me",
                UseShellExecute = true
            });
        }
        catch { }
    }
}
