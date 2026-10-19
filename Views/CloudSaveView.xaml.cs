using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class CloudSaveView : UserControl
{
    private CloudSaveViewModel? _wiredVm;

    public CloudSaveView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireSecretSync();
        WireSecretSync();
    }

    // Secret 回显：PasswordBox 不支持绑定，VM 预填只到属性，这里把值同步回框体；
    // 不等则不写，避免与用户输入互顶；同一 VM 只订阅一次
    private void WireSecretSync()
    {
        if (DataContext is not CloudSaveViewModel vm) return;
        if (!ReferenceEquals(_wiredVm, vm))
        {
            if (_wiredVm != null) _wiredVm.PropertyChanged -= OnSecretPropertyChanged;
            _wiredVm = vm;
            vm.PropertyChanged += OnSecretPropertyChanged;
        }
        SyncSecretBoxes(vm);
    }

    private void OnSecretPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is CloudSaveViewModel vm) SyncSecretBoxes(vm, e.PropertyName);
    }

    private void SyncSecretBoxes(CloudSaveViewModel vm, string? onlyProperty = null)
    {
        try
        {
            if ((onlyProperty == null || onlyProperty == nameof(vm.R2SecretKey)) &&
                R2SecretBox.Password != (vm.R2SecretKey ?? ""))
                R2SecretBox.Password = vm.R2SecretKey ?? "";
            if ((onlyProperty == null || onlyProperty == nameof(vm.S3SecretKey)) &&
                S3SecretBox.Password != (vm.S3SecretKey ?? ""))
                S3SecretBox.Password = vm.S3SecretKey ?? "";
            if ((onlyProperty == null || onlyProperty == nameof(vm.WebDavPassword)) &&
                WebDavPasswordBox.Password != (vm.WebDavPassword ?? ""))
                WebDavPasswordBox.Password = vm.WebDavPassword ?? "";
        }
        catch { }
    }

    // 名单内滚动到顶/底后把滚轮让给外层页面，避免鼠标进来就滚不出去
    private void GamesScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;
        if ((e.Delta > 0 && scroller.VerticalOffset == 0) ||
            (e.Delta < 0 && scroller.VerticalOffset >= scroller.ScrollableHeight))
        {
            e.Handled = true;
            var newArgs = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent
            };
            FindVisualParent<ScrollViewer>(scroller)?.RaiseEvent(newArgs);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null && parent is not T)
            parent = VisualTreeHelper.GetParent(parent);
        return parent as T;
    }

    private void R2SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CloudSaveViewModel vm && sender is PasswordBox box)
            vm.R2SecretKey = box.Password;
    }

    private void S3SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CloudSaveViewModel vm && sender is PasswordBox box)
            vm.S3SecretKey = box.Password;
    }

    private void WebDavPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CloudSaveViewModel vm && sender is PasswordBox box)
            vm.WebDavPassword = box.Password;
    }
}
