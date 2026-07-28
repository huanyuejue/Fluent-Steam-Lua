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

namespace SteamLuaManager.Views;

public class TrainerBindingDialog : Window
{
    private ComboBox _trainerCombo = null!;
    private TextBox _gameExePathBox = null!;
    private TextBox _gameNameBox = null!;
    private readonly ObservableCollection<DownloadedTrainerItem> _trainers;

    public TrainerBinding? Result { get; private set; }
    public TrainerBinding? EditingBinding { get; }

    public TrainerBindingDialog(ObservableCollection<DownloadedTrainerItem> trainers, TrainerBinding? existing = null)
    {
        _trainers = trainers;
        EditingBinding = existing;

        Title = existing != null ? "编辑游戏绑定" : "添加游戏绑定";
        Width = 520;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

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
        };

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
            IsEnabled = true
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
