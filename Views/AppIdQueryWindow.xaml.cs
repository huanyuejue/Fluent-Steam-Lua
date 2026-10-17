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

// AppID 查询二级窗：初进按目录名自动搜一轮；选中即 DialogResult=true 关闭，调用方读 SelectedAppId
public partial class AppIdQueryWindow : Window
{
    private readonly AppIdQueryViewModel _viewModel;

    public int? SelectedAppId => _viewModel.SelectedAppId;

    public AppIdQueryWindow(string initialKeyword)
    {
        var httpClientProvider = App.ServiceProvider?.GetRequiredService<IHttpClientProvider>();
        _viewModel = new AppIdQueryViewModel(httpClientProvider!);
        DataContext = _viewModel;
        InitializeComponent();
        Title = L.Get("AppId_Title");
        WindowHelper.SetUseModernWindowStyle(this, true);
        ApplyBackdrop();
        _viewModel.RequestClose += () =>
        {
            try { DialogResult = true; } catch { }
            Close();
        };
        Closed += (_, _) => _viewModel.Dispose();
        if (!string.IsNullOrWhiteSpace(initialKeyword))
        {
            _viewModel.SearchText = initialKeyword;
            Loaded += (_, _) =>
            {
                if (_viewModel.SearchCommand.CanExecute(null))
                    _viewModel.SearchCommand.Execute(null);
            };
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_viewModel.SearchCommand.CanExecute(null))
            _viewModel.SearchCommand.Execute(null);
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
