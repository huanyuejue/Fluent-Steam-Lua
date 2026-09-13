using System.Diagnostics;
using System.Windows.Controls;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class ManifestView : UserControl
{
    // 存量 Key 的占位符：只为让框看起来不是空的，保存时靠 _pwdDirty 区分用户是否真改过
    private const string FakePassword = "****************";
    private bool _pwdDirty;

    public ManifestView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // 已存 Key 就用假内容填充，避免重启后空框让人以为没填过
            if (DataContext is ManifestViewModel vm && vm.HasSavedKey && string.IsNullOrEmpty(ApiKeyBox.Password))
            {
                ApiKeyBox.Password = FakePassword;
                _pwdDirty = false;
            }
        };
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

    private void GetKeyButton_Click(object sender, System.Windows.RoutedEventArgs e)
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
