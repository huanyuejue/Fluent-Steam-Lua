using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Linq;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using SteamKit2.Authentication;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

// 独立登录窗口：本地凭证一键登录，或账号密码（2FA 在窗内完成）。
// 成功后 DialogResult = true，调用方直接读 ISteamAccountService 的会话状态。
public partial class LoginWindow : Window
{
    private readonly ISteamAccountService _accountService;
    private CancellationTokenSource? _loginCts;
    private TaskCompletionSource<string?>? _codeTcs;

    public LoginWindow(ISteamAccountService accountService, string backdropType = "Acrylic10")
    {
        _accountService = accountService;
        InitializeComponent();

        Title = "Steam 账号登录";
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

        Loaded += (_, _) =>
        {
            RefreshLocalAccounts();
            ShowStep(MethodPanel);
        };
        Closed += (_, _) =>
        {
            try { _loginCts?.Cancel(); } catch { }
            _codeTcs?.TrySetResult(null);
        };
    }

    private void ShowStep(Panel panel)
    {
        MethodPanel.Visibility = panel == MethodPanel ? Visibility.Visible : Visibility.Collapsed;
        LocalPanel.Visibility = panel == LocalPanel ? Visibility.Visible : Visibility.Collapsed;
        CredentialRootPanel.Visibility = panel == CredentialRootPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MethodLocalButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshLocalAccounts();
        SetStatus(string.Empty);
        ShowStep(LocalPanel);
    }

    private void MethodPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        SetStatus(string.Empty);
        ShowStep(CredentialRootPanel);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _codeTcs?.TrySetResult(null);
        TwoFactorPanel.Visibility = Visibility.Collapsed;
        CredentialPanel.Visibility = Visibility.Visible;
        SetStatus(string.Empty);
        ShowStep(MethodPanel);
    }

    private void RefreshLocalAccounts()
    {
        var users = _accountService.ListLocalUsers()
            .Select(u => new AccountItem(u, _accountService.GetAvatarPath(u.SteamId)))
            .ToList();
        LocalAccountList.ItemsSource = users;
        if (users.Count > 0) LocalAccountList.SelectedIndex = 0;
        LocalLoginButton.IsEnabled = users.Count > 0;
        MethodLocalButton.IsEnabled = users.Count > 0;
        if (users.Count == 0)
            SetStatus("本机无 Steam 登录记录，请输入账号密码登录");
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        PasswordHint.Visibility = string.IsNullOrEmpty(PasswordBox.Password)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record AccountItem(LocalSteamAccount User, string? AvatarPath)
    {
        public string PersonaName => User.PersonaName;
        public string AccountName => User.AccountName;
    }

    private async void LocalLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (LocalAccountList.SelectedItem is not AccountItem item) return;
        var acc = item.User;
        SetBusy(true);
        SetStatus($"正在用本机凭证登录 {acc.AccountName}...");
        try
        {
            var result = await _accountService.LoginWithLocalAsync(acc.AccountName);
            if (result.Success)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                SetStatus(result.Message);
            }
        }
        catch (Exception ex) { SetStatus($"登录异常：{ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void PasswordLoginButton_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            SetStatus("请输入账号和密码");
            return;
        }
        _loginCts?.Dispose();
        _loginCts = new CancellationTokenSource();
        SetBusy(true);
        CredentialPanel.Visibility = Visibility.Collapsed;
        SetStatus($"正在认证 {username}...");
        try
        {
            var result = await _accountService.LoginWithCredentialsAsync(
                username, password, new WindowAuthenticator(this), _loginCts.Token);
            if (result.Success)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                SetStatus(result.Message);
                CredentialPanel.Visibility = Visibility.Visible;
                TwoFactorPanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"登录异常：{ex.Message}");
            CredentialPanel.Visibility = Visibility.Visible;
            TwoFactorPanel.Visibility = Visibility.Collapsed;
        }
        finally { SetBusy(false); }
    }

    private void TwoFactorOkButton_Click(object sender, RoutedEventArgs e)
    {
        _codeTcs?.TrySetResult(TwoFactorCodeBox.Text.Trim());
    }

    private void TwoFactorCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _codeTcs?.TrySetResult(null);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        LocalLoginButton.IsEnabled = !busy;
        PasswordLoginButton.IsEnabled = !busy;
    }

    private void SetStatus(string message) => StatusText.Text = message;

    // 在窗内完成 2FA：服务回调切回 UI 线程弹码面板，等用户输入。
    // 手机确认直接拒绝，只走验证码（demo 里验证过这条路最稳）。
    private Task<string?> AskCodeAsync(string prompt)
    {
        _codeTcs = new TaskCompletionSource<string?>();
        TwoFactorPrompt.Text = prompt;
        TwoFactorCodeBox.Text = string.Empty;
        TwoFactorPanel.Visibility = Visibility.Visible;
        TwoFactorCodeBox.Focus();
        return _codeTcs.Task;
    }

    private sealed class WindowAuthenticator(LoginWindow owner) : IAuthenticator
    {
        public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);

        public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            var code = await owner.Dispatcher.InvokeAsync(() =>
            {
                if (previousCodeWasIncorrect)
                    owner.SetStatus("上次验证码不正确，请重试");
                return owner.AskCodeAsync("请输入手机令牌验证码");
            });
            var result = await code;
            return result ?? throw new OperationCanceledException();
        }

        public async Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            var code = await owner.Dispatcher.InvokeAsync(() =>
            {
                if (previousCodeWasIncorrect)
                    owner.SetStatus("上次验证码不正确，请重试");
                return owner.AskCodeAsync($"请输入发送到 {email} 的邮箱验证码");
            });
            var result = await code;
            return result ?? throw new OperationCanceledException();
        }
    }
}
