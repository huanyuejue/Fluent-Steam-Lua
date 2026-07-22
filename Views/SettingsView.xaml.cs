using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SteamLuaManager.Views;

public partial class SettingsView : UserControl
{
    private bool _firstLoad = true;

    public SettingsView()
    {
        InitializeComponent();
    }

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_firstLoad)
        {
            _firstLoad = false;
            return;
        }

        if (sender is TabControl tc &&
            tc.Template.FindName("ContentArea", tc) is UIElement content)
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            content.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        }
    }
}
