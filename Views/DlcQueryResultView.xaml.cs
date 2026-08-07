using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

public class DlcQueryResultView : Window
{
    private readonly ObservableCollection<DlcInfo> _allDlcs;
    private readonly StackPanel _itemsStack;
    private readonly string _luaPath;
    private readonly ISteamDepotService _depotService;
    private InfoBar? _statusBar;
    private TextBlock? _footer;
    private Button? _fetchAllButton;
    private readonly System.Windows.Threading.DispatcherTimer _statusBarTimer;
    private string _filter = "全部";

    public DlcQueryResultView(string gameName, ObservableCollection<DlcInfo> dlcList, string luaPath, ISteamDepotService depotService, string backdropType = "Acrylic10")
    {
        _allDlcs = dlcList;
        _luaPath = luaPath;
        _depotService = depotService;

        _statusBarTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _statusBarTimer.Tick += (_, _) =>
        {
            _statusBarTimer.Stop();
            if (_statusBar == null) return;
            _statusBar.IsOpen = false;
            if (_footer != null) _footer.Visibility = Visibility.Visible;
        };

        Title = "DLC 查询结果";
        Width = 560;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

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
                BackdropHelper.ApplyDarkMode(this);
            }
            Background = null;
        }

        var imported = dlcList.Count(d => d.IsImported);

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header row: title + filter
        var headerPanel = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var header = new TextBlock
        {
            Text = $"{gameName} 的 DLC 列表（共 {dlcList.Count} 个）",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Grid.SetColumn(header, 0);
        headerPanel.Children.Add(header);

        var filterPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var filterLabel = new TextBlock { Text = "筛选:", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        filterLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        filterPanel.Children.Add(filterLabel);
        var filterCombo = new ComboBox { SelectedIndex = 0, MinWidth = 60, FontSize = 12 };
        filterCombo.SetResourceReference(Control.BackgroundProperty, "ControlFillColorDefaultBrush");
        filterCombo.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        filterCombo.SetResourceReference(Control.BorderBrushProperty, "ControlElevationBorderBrush");
        filterCombo.Items.Add("全部");
        filterCombo.Items.Add("已入库");
        filterCombo.Items.Add("未入库");
        filterCombo.SelectionChanged += (_, _) =>
        {
            _filter = filterCombo.SelectedItem as string ?? "全部";
            RebuildList();
        };
        filterPanel.Children.Add(filterCombo);
        Grid.SetColumn(filterPanel, 1);
        headerPanel.Children.Add(filterPanel);

        var fetchAllButton = new Button
        {
            Content = "一键补全DLC",
            FontSize = 12,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _fetchAllButton = fetchAllButton;
        UpdateFetchAllButtonVisibility();
        fetchAllButton.SetResourceReference(Button.BackgroundProperty, "ControlFillColorDefaultBrush");
        fetchAllButton.SetResourceReference(Button.ForegroundProperty, "TextFillColorPrimaryBrush");
        fetchAllButton.SetResourceReference(Button.BorderBrushProperty, "ControlElevationBorderBrush");
        fetchAllButton.Click += async (_, _) => await OnFetchAllDlcClickedAsync(fetchAllButton);
        Grid.SetColumn(fetchAllButton, 2);
        headerPanel.Children.Add(fetchAllButton);

        Grid.SetRow(headerPanel, 0);
        grid.Children.Add(headerPanel);

        // DLC list
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _itemsStack = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        scroll.Content = _itemsStack;
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        // Footer / status bar (share the same row, toggled by visibility)
        _footer = new TextBlock
        {
            Text = $"已入库 {imported} 个，未入库 {dlcList.Count - imported} 个",
            FontSize = 12,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _footer.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetRow(_footer, 2);
        grid.Children.Add(_footer);

        _statusBar = new InfoBar
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 8, 0, 0),
            MaxHeight = 40,
            Padding = new Thickness(12, 4, 12, 4),
            IsOpen = false,
            Severity = InfoBarSeverity.Success,
            IsClosable = false,
            ClipToBounds = true
        };
        Panel.SetZIndex(_statusBar, 10);
        _statusBar.SetResourceReference(InfoBar.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        _statusBar.SetResourceReference(InfoBar.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        _statusBar.SetResourceReference(InfoBar.BorderThicknessProperty, "1");
        Grid.SetRow(_statusBar, 2);
        grid.Children.Add(_statusBar);

        Content = grid;

        RebuildList();
    }

    private void ShowStatusMessage(string message, bool isError)
    {
        if (_statusBar == null) return;
        if (_footer != null) _footer.Visibility = Visibility.Collapsed;
        _statusBar.Message = message;
        _statusBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        _statusBar.IsOpen = true;
        _statusBarTimer.Stop();
        _statusBarTimer.Start();
    }

    private void UpdateFooter()
    {
        if (_footer == null) return;
        var imported = _allDlcs.Count(d => d.IsImported);
        _footer.Text = $"已入库 {imported} 个，未入库 {_allDlcs.Count - imported} 个";
    }

    private void UpdateFetchAllButtonVisibility()
    {
        if (_fetchAllButton == null) return;
        _fetchAllButton.Visibility = _allDlcs.Any(d => !d.IsImported)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RebuildList()
    {
        _itemsStack.Children.Clear();

        var filtered = _allDlcs.Where(d => _filter switch
        {
            "已入库" => d.IsImported,
            "未入库" => !d.IsImported,
            _ => true
        }).OrderByDescending(d => d.IsImported).ToList();

        foreach (var dlc in filtered)
            _itemsStack.Children.Add(CreateDlcItem(dlc));

        UpdateFetchAllButtonVisibility();
    }

    private async Task OnFetchAllDlcClickedAsync(Button button)
    {
        button.IsEnabled = false;
        var pending = _allDlcs.Where(d => !d.IsImported).ToList();
        if (pending.Count == 0)
        {
            button.IsEnabled = true;
            ShowStatusMessage("没有需要补全的 DLC", false);
            return;
        }

        var originalText = button.Content.ToString();
        button.Content = "补全中...";
        var failedIds = new List<int>();

        try
        {
            foreach (var dlc in pending)
            {
                dlc.IsFetching = true;
                try
                {
                    var result = await _depotService.FetchDlcAsync(_luaPath, dlc.AppId, dlc.HasDepot);
                    if (result.Success)
                    {
                        dlc.IsImported = true;
                        dlc.FetchMessage = "已入库";
                    }
                    else
                    {
                        failedIds.Add(dlc.AppId);
                    }
                }
                catch
                {
                    failedIds.Add(dlc.AppId);
                }
                finally
                {
                    dlc.IsFetching = false;
                }
            }
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = originalText;
            UpdateFooter();
            RebuildList();
        }

        if (failedIds.Count == 0)
            ShowStatusMessage($"一键补全完成，已入库 {pending.Count} 个 DLC", false);
        else
            ShowStatusMessage($"部分 DLC 入库失败：{string.Join(", ", failedIds)}", true);
    }

    private async void OnFetchDlcClicked(DlcInfo dlc)
    {
        if (dlc.IsFetching) return;
        dlc.IsFetching = true;

        try
        {
            var result = await _depotService.FetchDlcAsync(_luaPath, dlc.AppId, dlc.HasDepot);
            if (result.Success)
            {
                dlc.IsImported = true;
                dlc.FetchMessage = "已入库";
                ShowStatusMessage("获取DLC成功，已写入清单并入库", false);
            }
            else
            {
                dlc.FetchMessage = result.Message;
                ShowStatusMessage(result.Message, true);
            }
        }
        catch (Exception ex)
        {
            dlc.FetchMessage = ex.Message;
            ShowStatusMessage($"查询 DLC 信息时出错：{ex.Message}", true);
        }
        finally
        {
            dlc.IsFetching = false;
            UpdateFooter();
            RebuildList();
        }
    }

    private Border CreateDlcItem(DlcInfo dlc)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 14, 8),
            Padding = new Thickness(12, 10, 12, 10)
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");

        var innerGrid = new Grid();
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var nameText = new TextBlock
        {
            Text = dlc.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 2)
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        infoStack.Children.Add(nameText);
        var appIdText = new TextBlock { Text = dlc.HasDepot ? $"AppID: {dlc.AppId} · 需Depots密钥" : $"AppID: {dlc.AppId}", FontSize = 11, Opacity = 0.6 };
        appIdText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        infoStack.Children.Add(appIdText);
        Grid.SetColumn(infoStack, 0);
        innerGrid.Children.Add(infoStack);

        Border statusBadge;
        if (dlc.IsImported)
        {
            statusBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0x4C, 0xAF, 0x50))
            };
            var badgeText = new TextBlock { Text = "已入库", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50)) };
            statusBadge.Child = badgeText;
        }
        else
        {
            statusBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x00, 0x00))
            };
            var badgeText = new TextBlock { Text = "未入库", FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.5 };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            statusBadge.Child = badgeText;
        }
        Grid.SetColumn(statusBadge, 2);
        innerGrid.Children.Add(statusBadge);

        if (!dlc.IsImported)
        {
            var fetchButton = new Button
            {
                Content = dlc.IsFetching ? "获取中..." : "获取DLC",
                FontSize = 12,
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = !dlc.IsFetching
            };
            fetchButton.SetResourceReference(Button.BackgroundProperty, "ControlFillColorDefaultBrush");
            fetchButton.SetResourceReference(Button.ForegroundProperty, "TextFillColorPrimaryBrush");
            fetchButton.SetResourceReference(Button.BorderBrushProperty, "ControlElevationBorderBrush");

            var clickedDlc = dlc;
            fetchButton.Click += (_, _) => OnFetchDlcClicked(clickedDlc);

            Grid.SetColumn(fetchButton, 1);
            innerGrid.Children.Add(fetchButton);
        }

        border.Child = innerGrid;
        return border;
    }
}
