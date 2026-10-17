using System.Windows;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using Microsoft.Extensions.DependencyInjection;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

// 常见问题独立窗口：非模态，可开着对照主界面操作；样式跟登录窗一致
public partial class FaqWindow : Window
{
    public FaqWindow()
    {
        DataContext = new AboutViewModel();
        InitializeComponent();
        Title = L.Get("Faq_Title");
        WindowHelper.SetUseModernWindowStyle(this, true);
        ApplyBackdrop();
        // 打开不抢焦点：默认会落到搜索框，清掉它
        Loaded += (_, _) => System.Windows.Input.Keyboard.ClearFocus();
    }

    private void ApplyBackdrop()
    {
        try
        {
            var backdropType = App.ServiceProvider?.GetRequiredService<ISettingsService>().Load().SelectedBackdrop
                               ?? "Acrylic10";
            if (!Enum.TryParse<BackdropType>(backdropType, true, out var parsedBackdrop))
                parsedBackdrop = BackdropType.Acrylic10;
            WindowHelper.SetSystemBackdropType(this, parsedBackdrop);
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
                    BackdropHelper.ApplyDarkMode(this);
                }
                Background = null;
            }
        }
        catch { }
    }
}
