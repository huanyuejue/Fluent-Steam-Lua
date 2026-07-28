using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using Microsoft.Win32;
using SteamLuaManager.Models;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

public class CheatOptionItem
{
    public CheatOption Option { get; set; } = null!;
    public bool IsSelected { get; set; }
}

public class TrainerBindingDialog : Window
{
    private ComboBox _trainerCombo = null!;
    private TextBox _gameExePathBox = null!;
    private TextBox _gameNameBox = null!;
    private ListBox _cheatOptionsList = null!;
    private StackPanel _cheatOptionsPanel = null!;
    private readonly ObservableCollection<string> _autoKeys = new();
    private readonly ObservableCollection<CheatOptionItem> _cheatOptions = new();
    private readonly ObservableCollection<DownloadedTrainerItem> _trainers;

    public TrainerBinding? Result { get; private set; }
    public TrainerBinding? EditingBinding { get; }

    public TrainerBindingDialog(ObservableCollection<DownloadedTrainerItem> trainers, TrainerBinding? existing = null)
    {
        _trainers = trainers;
        EditingBinding = existing;

        Title = existing != null ? "编辑游戏绑定" : "添加游戏绑定";
        Width = 860;
        Height = 740;
        MinWidth = 640;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        SetupWindowStyle();

        var grid = BuildLayout();
        Content = grid;

        if (existing != null)
            LoadExistingData(existing);
    }

    private void SetupWindowStyle()
    {
        if (!Enum.TryParse<BackdropType>("Acrylic10", true, out var parsedBackdrop))
            parsedBackdrop = BackdropType.Acrylic10;

        WindowHelper.SetUseModernWindowStyle(this, true);
        WindowHelper.SetSystemBackdropType(this, parsedBackdrop);
        WindowHelper.SetCornerStyle(this, WindowCornerStyle.Round);

        var isLight = ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Light;

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

    private DataTemplate CreateCheatOptionTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(CheckBox));
        factory.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3));
        factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        factory.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding("IsSelected"));
        factory.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler(CheatOptionCheckBox_Click));
        // Bind Content to self so ContentTemplate renders the inner layout
        factory.SetBinding(CheckBox.ContentProperty, new System.Windows.Data.Binding());

        // Inner layout template
        var contentTemplate = new DataTemplate();
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(StackPanel.MarginProperty, new Thickness(2, 0, 0, 0));

        var desc = new FrameworkElementFactory(typeof(TextBlock));
        desc.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        desc.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Option.Description"));

        var key = new FrameworkElementFactory(typeof(TextBlock));
        key.SetValue(TextBlock.FontSizeProperty, 11.0);
        key.SetValue(TextBlock.MarginProperty, new Thickness(0, 1, 0, 0));
        key.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        key.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Option.FullKey"));

        stack.AppendChild(desc);
        stack.AppendChild(key);
        contentTemplate.VisualTree = stack;
        factory.SetValue(CheckBox.ContentTemplateProperty, contentTemplate);

        return new DataTemplate { VisualTree = factory };
    }

    private void LoadCheatOptionsForSelectedTrainer()
    {
        _cheatOptions.Clear();
        _cheatOptionsPanel.Visibility = Visibility.Collapsed;

        if (_trainerCombo.SelectedItem is not DownloadedTrainerItem item) return;
        if (string.IsNullOrWhiteSpace(item.FilePath)) return;

        var options = TrainerViewModel.LoadCachedOptions(item.FilePath);
        if (options == null || options.Count == 0) return;

        foreach (var opt in options)
        {
            _cheatOptions.Add(new CheatOptionItem
            {
                Option = opt,
                IsSelected = _autoKeys.Contains(opt.FullKey) || _autoKeys.Contains(opt.DisplayText)
            });
        }
        _cheatOptionsPanel.Visibility = Visibility.Visible;
    }

    private void CheatOptionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is CheatOptionItem item)
        {
            item.IsSelected = cb.IsChecked == true;
            if (item.IsSelected && !_autoKeys.Contains(item.Option.DisplayText))
            {
                _autoKeys.Add(item.Option.DisplayText);
            }
            else if (!item.IsSelected)
            {
                _autoKeys.Remove(item.Option.DisplayText);
            }
        }
    }

    private Grid BuildLayout()
    {
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Title
        var titleBlock = new TextBlock
        {
            Text = EditingBinding != null ? "编辑游戏绑定" : "添加游戏绑定",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 20)
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        Grid.SetRow(titleBlock, 0);
        grid.Children.Add(titleBlock);

        // Form
        var formScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var formPanel = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };

        // --- Trainer selector ---
        formPanel.Children.Add(MakeLabel("修改器"));
        _trainerCombo = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 14),
            Height = 34,
            DisplayMemberPath = "DisplayName",
            FontSize = 13
        };
        _trainerCombo.SetResourceReference(Control.BackgroundProperty, "ControlFillColorDefaultBrush");
        _trainerCombo.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        _trainerCombo.SetResourceReference(Control.BorderBrushProperty, "ControlElevationBorderBrush");
        foreach (var t in _trainers)
            _trainerCombo.Items.Add(t);
        formPanel.Children.Add(_trainerCombo);

        // --- Game exe path ---
        formPanel.Children.Add(MakeLabel("游戏可执行文件路径"));

        var exeRow = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        exeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        exeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _gameExePathBox = new TextBox
        {
            Height = 34,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0)
        };
        _gameExePathBox.SetResourceReference(TextBox.BackgroundProperty, "ControlFillColorDefaultBrush");
        _gameExePathBox.SetResourceReference(TextBox.ForegroundProperty, "TextFillColorPrimaryBrush");
        _gameExePathBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlElevationBorderBrush");
        Grid.SetColumn(_gameExePathBox, 0);
        exeRow.Children.Add(_gameExePathBox);

        var browseBtn = new Button
        {
            Content = "浏览...",
            Height = 34,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0)
        };
        browseBtn.SetResourceReference(Control.BackgroundProperty, "ControlFillColorDefaultBrush");
        browseBtn.Click += BrowseGameExe;
        Grid.SetColumn(browseBtn, 1);
        exeRow.Children.Add(browseBtn);
        formPanel.Children.Add(exeRow);

        // --- Game name ---
        formPanel.Children.Add(MakeLabel("游戏名称"));
        _gameNameBox = new TextBox
        {
            Height = 34,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0)
        };
        _gameNameBox.SetResourceReference(TextBox.BackgroundProperty, "ControlFillColorDefaultBrush");
        _gameNameBox.SetResourceReference(TextBox.ForegroundProperty, "TextFillColorPrimaryBrush");
        _gameNameBox.SetResourceReference(TextBox.BorderBrushProperty, "ControlElevationBorderBrush");
        formPanel.Children.Add(_gameNameBox);

        _trainerCombo.SelectionChanged += (_, _) =>
        {
            if (_trainerCombo.SelectedItem is DownloadedTrainerItem item && string.IsNullOrWhiteSpace(_gameNameBox.Text))
                _gameNameBox.Text = item.DisplayName;
            LoadCheatOptionsForSelectedTrainer();
        };

        // --- Cheat options panel ---
        _cheatOptionsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        _cheatOptionsPanel.Children.Add(MakeLabel("修改器功能选项 (勾选后启动修改器时自动激活对应功能)"));
        var hintText = new TextBlock
        {
            Text = "该功能需要安装后台服务才能实现",
            FontSize = 11,
            Margin = new Thickness(4, 0, 0, 6)
        };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _cheatOptionsPanel.Children.Add(hintText);

        _cheatOptionsList = new ListBox
        {
            ItemsSource = _cheatOptions,
            Height = 300,
            Margin = new Thickness(0, 0, 0, 14),
            FontSize = 13,
            BorderThickness = new Thickness(1)
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_cheatOptionsList, ScrollBarVisibility.Disabled);
        _cheatOptionsList.SetResourceReference(Control.BackgroundProperty, "ControlFillColorDefaultBrush");
        _cheatOptionsList.SetResourceReference(Control.BorderBrushProperty, "ControlElevationBorderBrush");

        // ListBoxItem 容器 Stretch 填充 WrapPanel.ItemWidth 格子，实现等宽排列
        var itemContainerStyle = new Style(typeof(ListBoxItem));
        itemContainerStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _cheatOptionsList.ItemContainerStyle = itemContainerStyle;

        // ItemsPanel: WrapPanel for multi-column layout
        var itemsPanelTemplate = new ItemsPanelTemplate();
        var wrapPanelFactory = new FrameworkElementFactory(typeof(WrapPanel));
        wrapPanelFactory.SetValue(WrapPanel.OrientationProperty, Orientation.Horizontal);
        wrapPanelFactory.SetValue(WrapPanel.ItemWidthProperty, 260.0);
        itemsPanelTemplate.VisualTree = wrapPanelFactory;
        _cheatOptionsList.ItemsPanel = itemsPanelTemplate;

        // Item template for cheat options (CheckBox + Description + Key)
        _cheatOptionsList.ItemTemplate = CreateCheatOptionTemplate();
        _cheatOptionsPanel.Children.Add(_cheatOptionsList);
        formPanel.Children.Add(_cheatOptionsPanel);

        // AutoKeys section removed - no longer needed

        formScroll.Content = formPanel;
        Grid.SetRow(formScroll, 1);
        grid.Children.Add(formScroll);

        // Buttons
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var cancelBtn = new Button
        {
            Content = "取消",
            Height = 34,
            Padding = new Thickness(16, 0, 16, 0),
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };
        cancelBtn.SetResourceReference(Control.BackgroundProperty, "ControlFillColorDefaultBrush");
        cancelBtn.Click += (_, _) => { Result = null; Close(); };
        btnRow.Children.Add(cancelBtn);

        var okBtn = new Button
        {
            Content = "确定",
            Height = 34,
            Padding = new Thickness(16, 0, 16, 0),
            FontSize = 13,
            IsDefault = true
        };
        okBtn.SetResourceReference(Control.BackgroundProperty, "ControlFillColorDefaultBrush");
        okBtn.Click += Ok_Click;
        btnRow.Children.Add(okBtn);

        Grid.SetRow(btnRow, 2);
        grid.Children.Add(btnRow);

        return grid;
    }

    private static TextBlock MakeLabel(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
            FontWeight = FontWeights.SemiBold
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return tb;
    }

    private void BrowseGameExe(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = "选择游戏可执行文件"
        };
        if (dialog.ShowDialog() == true)
        {
            _gameExePathBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(_gameNameBox.Text))
                _gameNameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private void LoadExistingData(TrainerBinding binding)
    {
        // 先填充 AutoKeys，这样后续 LoadCheatOptionsForSelectedTrainer 才能检查已激活的键
        foreach (var k in binding.AutoKeys)
            _autoKeys.Add(k);

        for (int i = 0; i < _trainerCombo.Items.Count; i++)
        {
            var item = _trainerCombo.Items[i] as DownloadedTrainerItem;
            if (item?.FilePath?.Equals(binding.TrainerFilePath, StringComparison.OrdinalIgnoreCase) == true)
            {
                _trainerCombo.SelectedIndex = i;
                break;
            }
        }
        _gameExePathBox.Text = binding.GameExePath;
        _gameNameBox.Text = binding.GameName;
        // Cheat options will be loaded by the SelectionChanged handler above (now with correct AutoKeys state)
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_trainerCombo.SelectedItem is not DownloadedTrainerItem trainerItem)
        {
            ShowError("请选择一个修改器");
            return;
        }
        if (string.IsNullOrWhiteSpace(_gameExePathBox.Text))
        {
            ShowError("请选择游戏可执行文件路径");
            return;
        }
        if (!File.Exists(_gameExePathBox.Text))
        {
            ShowError("游戏可执行文件不存在");
            return;
        }

        Result = new TrainerBinding
        {
            TrainerFilePath = trainerItem.FilePath,
            TrainerDisplayName = trainerItem.DisplayName,
            GameExePath = _gameExePathBox.Text.Trim(),
            GameName = _gameNameBox.Text.Trim(),
            IsEnabled = true,
            AutoKeys = _autoKeys.ToList()
        };

        if (string.IsNullOrWhiteSpace(Result.GameName))
            Result.GameName = Path.GetFileNameWithoutExtension(Result.GameExePath);

        DialogResult = true;
        Close();
    }

    private async void ShowError(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "输入有误",
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360
            },
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }
}
