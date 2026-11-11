using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using Microsoft.Extensions.DependencyInjection;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

/// <summary>
/// 扩展安装引导。Steam 内置浏览器不允许用 URL 协议直达扩展页，
/// 只能让用户手动操作一次，因此这里把状态与路径都摆出来，减少试错。
/// </summary>
public partial class BridgeGuideWindow : Window
{
    private const string ExtensionsPageUrl = "chrome://extensions/";

    private readonly IBridgeServiceManager _bridgeManager;
    private readonly IExtensionStateService _extensionState;
    private readonly string _extensionDir;

    public BridgeGuideWindow(IBridgeServiceManager bridgeManager, IExtensionStateService extensionState)
    {
        _bridgeManager = bridgeManager;
        _extensionState = extensionState;
        InitializeComponent();

        // 套上 Modern 窗样式与系统材质：不设置的话深色主题下会落在默认白底窗上
        WindowHelper.SetUseModernWindowStyle(this, true);
        WindowHelper.SetCornerStyle(this, WindowCornerStyle.Round);
        ApplyBackdrop();

        _extensionDir = bridgeManager.GetExtensionDirectory() ?? string.Empty;

        BuildSteps();
        NoteText.Text = L.Get("Bridge_NoteBody");

        // 打开窗口时自动检测一次；之后由用户手动刷新
        RefreshStatus();
    }

    /// <summary>
    /// 材质跟随设置里的背景选择，与其它窗口保持一致；
    /// 纯色模式下给实底，否则让系统材质透过并补上对应的暗色模式。
    /// </summary>
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
            // 材质设置失败时退回实底，保证内容仍然可读
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        }
    }

    /// <summary>把安装流程铺成一条时间轴，每步带上可直接取用的内容。</summary>
    private void BuildSteps()
    {
        StepsPanel.Children.Clear();

        AddStep(1, L.Get("Bridge_Step1Title"), L.Get("Bridge_Step1Desc"), null, null);
        AddStep(2, L.Get("Bridge_Step2Title"), L.Get("Bridge_Step2Desc"), ExtensionsPageUrl, null);
        AddStep(3, L.Get("Bridge_Step3Title"), L.Get("Bridge_Step3Desc"), null, null);
        // 展示的是 bridge 父目录：用户要拖动里面的 extension 文件夹，
        // 直接给到散文件所在的 extension 目录会让人误以为该拖那一堆散文件
        var bridgeRoot = System.IO.Path.GetDirectoryName(_extensionDir) ?? _extensionDir;
        AddStep(4, L.Get("Bridge_Step4Title"), L.Get("Bridge_Step4Desc"), bridgeRoot, L.Get("Bridge_OpenFolder"));
        AddStep(5, L.Get("Bridge_Step5Title"), L.Get("Bridge_Step5Desc"), null, null);
    }

    private void AddStep(int index, string title, string desc, string? content, string? buttonLabel)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 0, 0, index == 5 ? 0 : 18)
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左侧时间轴：圆点 + 连接下一条的竖线
        var axis = new Grid();
        var dot = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = (Brush)FindResource("ControlFillColorDefaultBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        dot.Child = new TextBlock
        {
            Text = index.ToString(),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush")
        };
        axis.Children.Add(dot);

        if (index != 5)
        {
            // 竖线从圆点下沿延伸到行底部，形成连续的时间轴
            axis.Children.Add(new Rectangle
            {
                Width = 1,
                Margin = new Thickness(0, 30, 0, -18),
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill = (Brush)FindResource("DividerStrokeColorDefaultBrush")
            });
        }
        Grid.SetColumn(axis, 0);
        row.Children.Add(axis);

        // 右侧内容
        var text = new StackPanel { Margin = new Thickness(4, 1, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextFillColorPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrEmpty(desc))
        {
            text.Children.Add(new TextBlock
            {
                Text = desc,
                FontSize = 13,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush")
            });
        }

        // 输入框铺满整行，右侧按钮贴在框内末尾
        if (!string.IsNullOrEmpty(content))
        {
            // Auto 让短内容贴合文字，MaxWidth 给长内容一个上限；
            // 列宽仍受父容器约束，窗口再窄也不会把按钮挤出去
            var boxGrid = new Grid
            {
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            boxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            boxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var box = new TextBox
            {
                Text = content,
                IsReadOnly = true,
                FontSize = 14,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                Padding = new Thickness(14, 10, 14, 10),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 460,
                ToolTip = L.Get("Bridge_CopyTip")
            };
            box.Background = new SolidColorBrush(
                ((SolidColorBrush)FindResource("ControlFillColorDefaultBrush")).Color)
            { Opacity = 0.45 };
            Grid.SetColumn(box, 0);
            boxGrid.Children.Add(box);

            if (!string.IsNullOrEmpty(buttonLabel))
            {
                var btn = new Button
                {
                    Padding = new Thickness(14, 8, 14, 8),
                    FontSize = 13,
                    Margin = new Thickness(8, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            new iNKORE.UI.WPF.Modern.Controls.FontIcon
                            {
                                Glyph = "\uE8B7",
                                FontSize = 14,
                                Margin = new Thickness(0, 0, 8, 0),
                                VerticalAlignment = VerticalAlignment.Center
                            },
                            new TextBlock { Text = buttonLabel, VerticalAlignment = VerticalAlignment.Center }
                        }
                    }
                };

                if (index == 4)
                {
                    btn.ToolTip = L.Get("Bridge_OpenFolderTip");
                    btn.Click += (_, _) => OpenBridgeFolder();
                }
                Grid.SetColumn(btn, 1);
                boxGrid.Children.Add(btn);
            }

            text.Children.Add(boxGrid);
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        StepsPanel.Children.Add(row);
    }

    /// <summary>
    /// 打开 bridge 父目录（不是 extension 本身）。
    /// 用户在文件管理器里看到 extension 文件夹后，把它整个拖到 Steam 扩展页即可安装。
    /// </summary>
    private void OpenBridgeFolder()
    {
        try
        {
            if (string.IsNullOrEmpty(_extensionDir))
            {
                _ = ShowTipAsync(L.Get("Bridge_WindowTitle"), L.Get("Bridge_NotDeployed"));
                return;
            }

            // 打开 bridge 的父目录，用户在其中能看到 extension 文件夹并拖动它
            var bridgeRoot = System.IO.Path.GetDirectoryName(_extensionDir);
            if (string.IsNullOrEmpty(bridgeRoot) || !System.IO.Directory.Exists(bridgeRoot))
            {
                _ = ShowTipAsync(L.Get("Bridge_WindowTitle"), L.Get("Bridge_NotDeployed"));
                return;
            }

            // 单文件发布时当前进程带着解包目录相关的环境变量，
            // 直接 shell 启动 explorer 会让它继承这些变量并初始化失败（0xc0000142）。
            // 这里用系统目录下的完整路径，并显式构造一份干净的基础环境
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var explorer = System.IO.Path.Combine(windowsDir, "explorer.exe");
            if (!System.IO.File.Exists(explorer)) explorer = "explorer.exe";

            var psi = new ProcessStartInfo(explorer, $"\"{bridgeRoot}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = windowsDir
            };

            // 只保留 explorer 正常运行需要的系统变量，避免继承单文件的解包路径
            psi.EnvironmentVariables.Clear();
            foreach (var name in new[] { "SystemRoot", "windir", "SystemDrive", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "ComSpec", "PATH", "PATHEXT", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "COMPUTERNAME", "USERNAME", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value)) psi.EnvironmentVariables[name] = value;
            }

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _ = ShowTipAsync(L.Get("Bridge_WindowTitle"), ex.Message);
        }
    }

    /// <summary>重新检测桥接进程与扩展安装状态。</summary>
    private void RefreshStatus_Click(object sender, RoutedEventArgs e) => RefreshStatus();

    private void RefreshStatus()
    {
        // 桥接进程
        var bridgeRunning = _bridgeManager.IsRunning;
        var bridgeDeployed = _bridgeManager.IsDeployed;
        if (bridgeRunning)
        {
            SetPill(BridgeStatePill, BridgeStateText, L.Get("Bridge_BridgeReady"), true);
            BridgeProcessText.Text = "FluentLuaBridge.exe";
        }
        else if (bridgeDeployed)
        {
            SetPill(BridgeStatePill, BridgeStateText, L.Get("Bridge_BridgeStopped"), false);
            BridgeProcessText.Text = "FluentLuaBridge.exe";
        }
        else
        {
            SetPill(BridgeStatePill, BridgeStateText, L.Get("Bridge_BridgeMissing"), false);
            BridgeProcessText.Text = L.Get("Bridge_NotDeployed");
        }

        // Steam 扩展插件状态，底部固定显示插件名称
        // 按部署目录判断，不用 ID：未打包扩展的 ID 由 Chrome 依路径推导，换目录就变
        var state = _extensionState.Query(_extensionDir);
        var extOk = state.Installed && state.Enabled;
        var extName = ReadExtensionName() ?? L.Get("Bridge_ExtName");
        if (!state.Installed)
        {
            SetPill(ExtStatePill, ExtStateText, L.Get("Bridge_ExtNotInstalled"), false);
            ExtDetailText.Text = extName;
        }
        else if (!state.Enabled)
        {
            SetPill(ExtStatePill, ExtStateText, L.Get("Bridge_ExtDisabled"), false);
            ExtDetailText.Text = state.Name ?? extName;
        }
        else
        {
            SetPill(ExtStatePill, ExtStateText, L.Get("Bridge_ExtReady"), true);
            ExtDetailText.Text = state.Name ?? extName;
        }

        // 组件版本：桥接与扩展分开显示，更新后能直观看出是否已换版
        var bridgeVersion = _bridgeManager.GetDeployedVersion() ?? "-";
        var extVersion = _bridgeManager.GetDeployedExtensionVersion() ?? "-";
        VersionSummaryText.Text = $"桥接 {bridgeVersion} / 扩展 {extVersion}";

        // 总体结论
        var allOk = bridgeRunning && extOk;
        if (allOk)
        {
            SetPill(OverallStatePill, OverallStateText, L.Get("Bridge_OverallRunning"), true);
            OverallDot.Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));
        }
        else
        {
            SetPill(OverallStatePill, OverallStateText, L.Get("Bridge_OverallNotReady"), false);
            OverallDot.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        }
    }

    /// <summary>从部署目录读取插件名称；清单缺失时由调用方回退到固定文案。</summary>
    private string? ReadExtensionName()
    {
        try
        {
            if (string.IsNullOrEmpty(_extensionDir)) return null;
            var manifest = System.IO.Path.Combine(_extensionDir, "manifest.json");
            if (!System.IO.File.Exists(manifest)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>状态胶囊用实心亮底配白字，与整体卡片形成明确的状态对比。</summary>
    private static void SetPill(Border pill, TextBlock text, string label, bool good)
    {
        var color = good ? Color.FromRgb(0x2E, 0xA0, 0x60) : Color.FromRgb(0xD9, 0x8B, 0x1F);
        pill.Background = new SolidColorBrush(color);
        text.Text = label;
        text.Foreground = Brushes.White;
    }

    /// <summary>轻量提示框，与主程序其它弹窗保持一致的外观。</summary>
    private Task<ContentDialogResult> ShowTipAsync(string title, string message)
    {
        return new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420
            },
            CloseButtonText = L.Get("App_Close"),
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
    }
}
