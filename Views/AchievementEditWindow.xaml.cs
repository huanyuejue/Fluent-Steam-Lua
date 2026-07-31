using System.Windows;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using Microsoft.Extensions.DependencyInjection;
using SteamLuaManager.Services;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public partial class AchievementEditWindow : Window
{
    private readonly AchievementEditViewModel _viewModel;

    public AchievementEditWindow(uint appId, string gameName)
    {
        InitializeComponent();
        _viewModel = new AchievementEditViewModel(appId, gameName);
        DataContext = _viewModel;
        Title = $"成就编辑 - {gameName}";
        ApplyBackdrop();
        Closed += AchievementEditWindow_Closed;
        _ = _viewModel.LoadAsync();
    }

    /// <summary>背景与 DLC 查询窗口保持一致：跟随设置中的背景类型与主题颜色。</summary>
    private void ApplyBackdrop()
    {
        try
        {
            var backdropType = App.ServiceProvider?.GetRequiredService<ISettingsService>().Load().SelectedBackdrop
                               ?? "Acrylic10";
            if (!Enum.TryParse<BackdropType>(backdropType, true, out var parsedBackdrop))
            {
                parsedBackdrop = BackdropType.Acrylic10;
            }

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
        catch
        {
        }
    }

    private void AchievementEditWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
    }
}
