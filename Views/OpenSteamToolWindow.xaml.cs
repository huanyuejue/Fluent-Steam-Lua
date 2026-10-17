using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Threading;
using iNKORE.UI.WPF.Modern;
using iNKORE.UI.WPF.Modern.Controls;
using iNKORE.UI.WPF.Modern.Controls.Helpers;
using iNKORE.UI.WPF.Modern.Helpers.Styles;
using Microsoft.Extensions.DependencyInjection;
using SteamLuaManager.Services;

namespace SteamLuaManager.Views;

// OpenSteamTool 独立管理窗：内核安装/更新/启禁用/卸载/净化/取toml，外加入口注入方式切换。
// 逻辑从主窗口浮动子菜单迁移而来，仅承载内核管理，故用模态打开、一次只对一个窗口操作。
public partial class OpenSteamToolWindow : Window
{
    private readonly IOpenSteamToolService _service;
    private readonly ISteamPathService _steamPathService;
    private CancellationTokenSource? _kernelCts;
    private CancellationTokenSource? _tomlCts;
    private bool _tomlFetching;

    public OpenSteamToolWindow(IOpenSteamToolService service, ISteamPathService steamPathService)
    {
        _service = service;
        _steamPathService = steamPathService;
        InitializeComponent();
        // 套上 Modern 窗样式与系统材质，否则深色主题下白字落在默认白底窗上会整窗隐形
        WindowHelper.SetUseModernWindowStyle(this, true);
        WindowHelper.SetCornerStyle(this, WindowCornerStyle.Round);
        ApplyBackdrop();
        // {l:Loc} 绑定随语言自动刷新，代码里拼的状态文本得手动重建
        LocalizationService.Instance.PropertyChanged += OnLocalizationChanged;
        Closed += (_, _) => LocalizationService.Instance.PropertyChanged -= OnLocalizationChanged;
        Loaded += (_, _) => RefreshState();
    }

    // 材质跟随设置里的背景选择；浅色/深色分别走不同的 Acrylic 底色，纯色模式直接给实底
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

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => RefreshState();

    private void RefreshState()
    {
        var steamPath = _service.GetSteamPath();
        SteamPathText.Text = steamPath ?? L.Get("Home_SteamNotFound");

        var installed = _service.IsInstalled;
        var disabled = _service.HasDisabledKernel;
        // 只有内核启用态才谈得上"注入入口配置"：禁用态入口已被改名，不能当成用户的选择
        var kernelReady = installed && !disabled;
        var entryState = kernelReady ? _service.GetEntryState() : InjectionEntryState.None;

        // 内核状态药丸：未安装灰 / 已禁用、缺入口、入口冲突黄 / 正常绿。
        // 缺入口与冲突内核本体都在、却不是可用的注入状态，必须区别于"已安装"，否则误导。
        // 先判禁用：禁用时本体已是 .off 形态，探测不到激活的 OpenSteamTool.dll，
        // 若按未安装分支会误报"未安装"，把已禁用的内核说成没装
        if (disabled)
        {
            KernelStateText.Text = L.Get("Ost_StatusDisabled");
            ApplyStatePill(PillTone.Yellow);
        }
        else if (!installed)
        {
            KernelStateText.Text = L.Get("Ost_StatusNotInstalled");
            ApplyStatePill(PillTone.Gray);
        }
        else if (entryState == InjectionEntryState.None)
        {
            KernelStateText.Text = L.Get("Ost_StatusNoEntry");
            ApplyStatePill(PillTone.Yellow);
        }
        else if (entryState == InjectionEntryState.BothActive)
        {
            KernelStateText.Text = L.Get("Ost_StatusConflict");
            ApplyStatePill(PillTone.Yellow);
        }
        else
        {
            KernelStateText.Text = L.Get("Ost_StatusRunning");
            ApplyStatePill(PillTone.Green);
        }

        // 有效注入方式：双入口冲突时不做"当前"归属，显示未选中（由横幅引导二选一）；
        // 正常单入口才显示其名字。内核未就绪时一律不显示方式，避免"已禁用"旁仍挂个入口名
        var mode = kernelReady ? _service.GetInjectionMode() : InjectionMode.None;
        InjectModeText.Text = entryState == InjectionEntryState.BothActive
            ? L.Get("Ost_StatusInjectUnselected")
            : mode switch
            {
                InjectionMode.Dwmapi => L.Get("Ost_InjectDwmapiName"),
                InjectionMode.Version => L.Get("Ost_InjectVersionName"),
                _ => L.Get("Ost_StatusInjectNone")
            };

        // 异常入口配置的横幅：缺入口=当前根本不会注入；冲突=两个都开着、实际以 version 为准。
        // 禁用/未安装时入口本就该是停用态，不算异常配置，不能对着已禁用的内核喊"缺入口"
        if (kernelReady && entryState == InjectionEntryState.None)
        {
            InjectWarningText.Text = L.Get("Ost_InjectWarnNone");
            InjectWarningBar.Visibility = Visibility.Visible;
        }
        else if (kernelReady && entryState == InjectionEntryState.BothActive)
        {
            InjectWarningText.Text = L.Get("Ost_InjectWarnBoth");
            InjectWarningBar.Visibility = Visibility.Visible;
        }
        else
        {
            InjectWarningBar.Visibility = Visibility.Collapsed;
        }

        // 双入口冲突时两侧都不勾选/不高亮，与"未选中"一致
        var cardMode = entryState == InjectionEntryState.BothActive ? InjectionMode.None : mode;
        SetCardActive(DwmapiBadge, DwmapiCheck, cardMode == InjectionMode.Dwmapi);
        SetCardActive(VersionBadge, VersionCheck, cardMode == InjectionMode.Version);
        ApplyCardActive(DwmapiCard, cardMode == InjectionMode.Dwmapi);
        ApplyCardActive(VersionCard, cardMode == InjectionMode.Version);

        // 入口切换要能改名，只在启用态开放；禁用/未安装时置灰
        DwmapiCard.Opacity = kernelReady ? 1.0 : 0.5;
        VersionCard.Opacity = kernelReady ? 1.0 : 0.5;

        InstallButton.IsEnabled = !installed && !disabled;
        UpdateButton.IsEnabled = kernelReady;
        UninstallButton.IsEnabled = kernelReady;
        ToggleButton.IsEnabled = installed || disabled;
        ToggleTitleText.Text = disabled ? L.Get("Main_KmEnable") : L.Get("Main_KmDisable");

        _ = UpdateVersionTextAsync();
    }

    private enum PillTone { Green, Yellow, Gray }

    // 状态药丸按语义取色：绿=正常可注入，黄=需处理（禁用/缺入口/冲突），灰=未安装
    private void ApplyStatePill(PillTone tone)
    {
        var (bg, fg) = tone switch
        {
            PillTone.Green => (Color.FromArgb(0x33, 0x6C, 0xCB, 0x5F), Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F)),
            PillTone.Yellow => (Color.FromArgb(0x33, 0xE8, 0xC3, 0x00), Color.FromArgb(0xFF, 0xE8, 0xC3, 0x00)),
            _ => (Color.FromArgb(0x1F, 0x80, 0x80, 0x80), Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E))
        };
        KernelStatePill.Background = new SolidColorBrush(bg);
        KernelStateText.Foreground = new SolidColorBrush(fg);
    }

    // 选中卡用强调色描边 + 淡强调底，其余回落到默认卡片色
    private void ApplyCardActive(Border card, bool active)
    {
        if (active)
        {
            card.BorderBrush = TryFindResource("SystemAccentColorBrush") as Brush ?? Brushes.Transparent;
            card.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x0F, 0x6C, 0xBD));
        }
        else
        {
            card.BorderBrush = TryFindResource("CardStrokeColorDefaultBrush") as Brush ?? Brushes.Transparent;
            card.Background = TryFindResource("CardBackgroundFillColorDefaultBrush") as Brush ?? Brushes.Transparent;
        }
    }

    private static void SetCardActive(Border badge, UIElement check, bool active)
    {
        var vis = active ? Visibility.Visible : Visibility.Collapsed;
        badge.Visibility = vis;
        check.Visibility = vis;
    }

    private async Task UpdateVersionTextAsync()
    {
        try
        {
            var v = await _service.GetLocalVersionAsync();
            KernelVersionText.Text = string.IsNullOrEmpty(v) ? L.Get("Main_Unknown") : v;
        }
        catch
        {
            KernelVersionText.Text = L.Get("Main_Unknown");
        }
    }

    // ========== 弹窗 ==========

    private async Task ShowDialogAsync(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            CloseButtonText = L.Get("Common_Confirm"),
            DefaultButton = ContentDialogButton.Close
        };
        await dlg.ShowAsync();
    }

    private async Task<bool> ShowConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = primaryText ?? L.Get("Common_Confirm"),
            CloseButtonText = closeText ?? L.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    // ========== 遮罩层 ==========

    private void ShowKernelOverlay(string status)
    {
        KernelOverlayGrid.Visibility = Visibility.Visible;
        KernelOverlayStatus.Text = status;
        KernelOverlayHint.Visibility = Visibility.Collapsed;
        KernelOverlayProgressBar.Visibility = Visibility.Collapsed;
        KernelOverlayPercent.Visibility = Visibility.Collapsed;
        KernelOverlayRing.IsActive = true;
    }

    private void UpdateKernelOverlayProgress(int percent)
    {
        KernelOverlayRing.IsActive = false;
        KernelOverlayRing.Visibility = Visibility.Collapsed;
        KernelOverlayProgressBar.Visibility = Visibility.Visible;
        KernelOverlayPercent.Visibility = Visibility.Visible;
        KernelOverlayProgressBar.Value = percent;
        KernelOverlayPercent.Text = $"{percent}%";
    }

    private void ShowKernelDownloadHint() => KernelOverlayHint.Visibility = Visibility.Visible;

    private void HideKernelOverlay()
    {
        KernelOverlayRing.IsActive = false;
        KernelOverlayGrid.Visibility = Visibility.Collapsed;
    }

    private void KernelCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _kernelCts?.Cancel();
        _tomlCts?.Cancel();
    }

    // ========== 操作 ==========

    private async void InstallButton_Click(object sender, RoutedEventArgs e) => await InstallKernelAsync();

    private async void UpdateButton_Click(object sender, RoutedEventArgs e) => await UpdateKernelAsync();

    private async void ToggleButton_Click(object sender, RoutedEventArgs e) => await ToggleKernelAsync();

    private async void UninstallButton_Click(object sender, RoutedEventArgs e) => await UninstallKernelAsync();

    private async void PurifyButton_Click(object sender, RoutedEventArgs e) => await PurifyKernelAsync();

    private async void FetchTomlButton_Click(object sender, RoutedEventArgs e) => await FetchTomlFilesAsync();

    private async void DwmapiCard_Click(object sender, MouseButtonEventArgs e) =>
        await SwitchInjectionAsync(InjectionMode.Dwmapi);

    private async void VersionCard_Click(object sender, MouseButtonEventArgs e) =>
        await SwitchInjectionAsync(InjectionMode.Version);

    // ========== 活动条 ==========

    private async Task SwitchInjectionAsync(InjectionMode target)
    {
        if (_service.HasDisabledKernel)
        {
            await ShowDialogAsync(L.Get("Main_Tip"), L.Get("Ost_InjectNeedEnabled"));
            return;
        }
        if (!_service.IsInstalled)
        {
            await ShowDialogAsync(L.Get("Main_Tip"), L.Get("Main_InstalledNotFound"));
            return;
        }
        // 只有当前就是该入口的"单一激活"态才算无需切换；双入口冲突等异常态下
        // 点任一入口都应继续走下去，借这次切换把配置归一
        var entryState = _service.GetEntryState();
        var alreadySingle = (target == InjectionMode.Dwmapi && entryState == InjectionEntryState.Dwmapi)
                            || (target == InjectionMode.Version && entryState == InjectionEntryState.Version);
        if (alreadySingle) return;
        // 切换要重命名入口 DLL，Steam 占用时改不动，先关门禁
        if (SteamProcess.IsSteamRunning())
        {
            await ShowDialogAsync(L.Get("Main_DialogSteamBusy"), L.Get("Ost_InjectNeedSteamClosed"));
            return;
        }
        if (!await ShowConfirmAsync(L.Get("Ost_InjectSwitchTitle"), L.Get("Ost_InjectSwitchBody"))) return;

        try
        {
            await _service.SwitchInjectionModeAsync(target);
            RefreshState();
            var name = target == InjectionMode.Dwmapi
                ? L.Get("Ost_InjectDwmapiName")
                : L.Get("Ost_InjectVersionName");
            var restart = await ShowConfirmAsync(
                L.Get("Ost_InjectSwitched"),
                L.Get("Ost_InjectSwitchedBody", name),
                L.Get("Main_FabRestart"),
                L.Get("Common_Confirm"));
            if (restart)
            {
                var result = SteamProcess.RestartSteam(_steamPathService);
                if (!result.Ok) await ShowDialogAsync(result.Title, result.Message);
            }
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Ost_InjectSwitchFailed", ex.Message));
        }
    }

    private async Task InstallKernelAsync()
    {
        if (_service.IsInstalled)
        {
            var confirmed = await ShowConfirmAsync(
                L.Get("Main_InstConfirmTitle"),
                L.Get("Main_InstConfirmBody"),
                L.Get("Main_Reinstall"));
            if (!confirmed) return;
        }

        try
        {
            var (version, downloadUrl, _) = await _service.GetRemoteInfoAsync();
            if (string.IsNullOrEmpty(downloadUrl))
            {
                await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_NoDownloadUrl"));
                return;
            }

            if (!await RunKernelDownloadAsync(downloadUrl))
                return;

            await ShowDialogAsync(L.Get("Main_InstDone"), L.Get("Main_InstDoneBody", version));
            RefreshState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_InstFailed", ex.Message));
        }
    }

    // 统一的内核下载流程，Install/Update 共用；返回 false 表示已取消
    private async Task<bool> RunKernelDownloadAsync(string downloadUrl)
    {
        ShowKernelOverlay(L.Get("Main_OvDownloading"));
        _kernelCts = new CancellationTokenSource();
        try
        {
            var status = new Progress<string>(msg => KernelOverlayStatus.Text = msg);
            var progress = new Progress<int>(pct => UpdateKernelOverlayProgress(pct));
            ShowKernelDownloadHint();
            await _service.InstallAsync(downloadUrl, status, progress, _kernelCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _kernelCts?.Dispose();
            _kernelCts = null;
            HideKernelOverlay();
        }
    }

    private async Task UpdateKernelAsync()
    {
        if (!_service.IsInstalled)
        {
            await ShowDialogAsync(L.Get("Main_Tip"), L.Get("Main_NotInstalledBody"));
            return;
        }

        var localVersion = await _service.GetLocalVersionAsync() ?? L.Get("Main_Unknown");
        var localDisplay = localVersion;

        try
        {
            var (remoteVersion, downloadUrl, releaseUrl) = await _service.GetRemoteInfoAsync();
            if (string.IsNullOrEmpty(downloadUrl))
            {
                await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_NoRemoteInfo"));
                return;
            }

            if (localVersion != L.Get("Main_Unknown"))
            {
                // fork 仓库 tag 不带 v 前缀（如 1.4.9），老版本本地号带 v（如 v1.4.7），统一去掉前缀再比对
                var localVer = Version.TryParse(localVersion.Trim().TrimStart('v', 'V'), out var lv) ? lv : null;
                var remoteVer = Version.TryParse(remoteVersion.Trim().TrimStart('v', 'V'), out var rv) ? rv : null;
                if (localVer != null && remoteVer != null && localVer >= remoteVer)
                {
                    await ShowDialogAsync(L.Get("Main_NoUpdate"), L.Get("Main_NoUpdateBody", localVersion, remoteVersion));
                    return;
                }
            }

            var updateDialog = new ContentDialog
            {
                Title = L.Get("Main_UpdateAvail"),
                Content = new TextBlock
                {
                    Text = L.Get("Main_UpdateAvailBody", localDisplay, remoteVersion),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460
                },
                PrimaryButtonText = L.Get("Main_Update"),
                SecondaryButtonText = L.Get("Main_GoRelease"),
                CloseButtonText = L.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            var dialogResult = await updateDialog.ShowAsync();
            if (dialogResult == ContentDialogResult.Secondary)
            {
                Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
                return;
            }
            if (dialogResult != ContentDialogResult.Primary) return;

            if (!await RunKernelDownloadAsync(downloadUrl))
                return;

            await ShowDialogAsync(L.Get("Main_UpdDone"), L.Get("Main_UpdDoneBody", remoteVersion));
            RefreshState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_UpdCheckFailed", ex.Message));
        }
    }

    private async Task ToggleKernelAsync()
    {
        if (_service.HasDisabledKernel)
        {
            try
            {
                var done = await _service.EnableKernelAsync();
                await ShowDialogAsync(L.Get("Main_EnableDone"), L.Get("Main_EnableDoneBody", done));
                RefreshState();
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_EnableFailed", ex.Message));
            }
            return;
        }

        if (!_service.IsInstalled)
        {
            await ShowDialogAsync(L.Get("Main_Tip"), L.Get("Main_InstalledNotFound"));
            return;
        }
        // 禁用：重命名被加载的 DLL 不可靠，Steam 关闭门禁
        if (SteamProcess.IsSteamRunning())
        {
            await ShowDialogAsync(L.Get("Main_DialogSteamBusy"), L.Get("Main_SteamRunningDisable"));
            return;
        }
        try
        {
            var done = await _service.DisableKernelAsync();
            await ShowDialogAsync(L.Get("Main_DisableDone"), L.Get("Main_DisableDoneBody", done));
            RefreshState();
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_DisableFailed", ex.Message));
        }
    }

    private async Task UninstallKernelAsync()
    {
        if (!_service.IsInstalled)
        {
            await ShowDialogAsync(L.Get("Main_Tip"), L.Get("Main_InstalledNotFound"));
            return;
        }

        // 文件被 Steam 占用时删不动，先关门禁；
        // 待删清单从磁盘实测而来，与确认文案同源，不会再出现写死三件套
        if (SteamProcess.IsSteamRunning())
        {
            await ShowDialogAsync(L.Get("Main_DialogSteamBusy"), L.Get("Ost_UninstNeedSteamClosed"));
            return;
        }

        var files = _service.GetInstalledKernelFiles();
        var fileList = string.Join("\n", files.Select(f => "• " + f));
        var confirmed = await ShowConfirmAsync(
            L.Get("Main_UninstConfirmTitle"),
            L.Get("Main_UninstConfirmBody", fileList),
            L.Get("Main_Uninstall"));
        if (!confirmed) return;

        try
        {
            await _service.UninstallAsync();
            await ShowDialogAsync(L.Get("Main_UninstDone"), L.Get("Main_UninstDoneBody"));
            RefreshState();
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_UninstFailed", ex.Message));
        }
    }

    private async Task PurifyKernelAsync()
    {
        // 删文件前 Steam 必须完全退出，否则占用的 DLL 删不动（更新器同款教训）
        if (SteamProcess.IsSteamRunning())
        {
            await ShowDialogAsync(L.Get("Main_DialogSteamBusy"), L.Get("Main_SteamRunningPurify"));
            return;
        }

        List<ForeignKernelFile> found;
        try
        {
            found = (await _service.ScanForeignKernelsAsync()).ToList();
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_DialogScanFailed", ex.Message));
            return;
        }

        if (found.Count == 0)
        {
            await ShowDialogAsync(L.Get("Main_DialogNoForeign"), L.Get("Main_DialogNoForeignBody"));
            return;
        }

        var confirmed = await ShowConfirmAsync(
            L.Get("Main_DialogConfirmPurify"),
            L.Get("Main_DialogConfirmPurifyBody", string.Join("\n", found.Select(f => $"• {f.RelativePath}（{f.Owner}）"))),
            L.Get("Main_Delete"));
        if (!confirmed) return;

        try
        {
            var (removed, failed) = await _service.PurifyForeignKernelsAsync(found.Select(f => f.RelativePath));
            RefreshState();
            if (failed.Count == 0)
            {
                await ShowDialogAsync(L.Get("Main_DialogPurifyDone"), L.Get("Main_DialogPurifyDoneBody", removed));
            }
            else
            {
                await ShowDialogAsync(L.Get("Main_DialogPurifyPartial"),
                    L.Get("Main_DialogPurifyPartialBody", removed, failed.Count,
                    string.Join("\n", failed.Select(f => $"• {f}"))));
            }
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_DialogPurifyFailed", ex.Message));
        }
    }

    private async Task FetchTomlFilesAsync()
    {
        // 防重入：上一次全流程（确认框/下载/结果框）没走完又点一次，
        // 两个 ContentDialog 互斥直接闪退（已实测）。静默吞掉，只记日志。
        if (_tomlFetching)
        {
            LogService.Info(L.Get("LogCat_Op"), L.Get("Main_LogTomlDupClick"));
            return;
        }
        _tomlFetching = true;
        try
        {
            await FetchTomlFilesCoreAsync();
        }
        finally
        {
            _tomlFetching = false;
        }
    }

    private async Task FetchTomlFilesCoreAsync()
    {
        // 先识别再问：把构建ID和两个SHA摆出来让用户确认，避免下错版本
        SteamDllTomlInfo? info;
        try
        {
            info = await _service.DetectSteamDllInfoAsync();
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_TomlDetectFailed", ex.Message));
            return;
        }

        if (info == null)
        {
            await ShowDialogAsync(L.Get("Main_SteamNotFoundTitle"), L.Get("Main_SteamNotFoundBody"));
            return;
        }

        var secondary = Application.Current.TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
        var fetchPanel = new StackPanel { MaxWidth = 560 };
        fetchPanel.Children.Add(new TextBlock
        {
            Text = L.Get("Main_TomlResult"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        fetchPanel.Children.Add(new TextBlock
        {
            Text = L.Get("Main_TomlClientSha"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
        fetchPanel.Children.Add(new TextBlock
        {
            Text = info.ClientSha,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = secondary
        });
        fetchPanel.Children.Add(new TextBlock
        {
            Text = L.Get("Main_TomlUiSha"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
        fetchPanel.Children.Add(new TextBlock
        {
            Text = info.UiSha,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = secondary
        });
        fetchPanel.Children.Add(new TextBlock
        {
            Text = L.Get("Main_TomlWillFetch"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 2)
        });
        foreach (var (channel, component) in new[] { ("pattern", "steamclient"), ("pattern", "steamui"), ("ipc", "steamclient") })
        {
            var sha = component == "steamui" ? info.UiSha : info.ClientSha;
            fetchPanel.Children.Add(new TextBlock
            {
                Text = $"- {channel}/{component}/{sha}.toml",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = secondary,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }
        fetchPanel.Children.Add(new TextBlock
        {
            Text = L.Get("Main_TomlNotes"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 2)
        });
        fetchPanel.Children.Add(new TextBlock
        {
            Text = L.Get("Main_TomlOverwrite"),
            TextWrapping = TextWrapping.Wrap
        });
        fetchPanel.Children.Add(new TextBlock
        {
            Text = L.Get("Main_TomlAsk"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });
        var fetchDialog = new ContentDialog
        {
            Title = L.Get("Main_TomlTitle"),
            Content = new ScrollViewer
            {
                Content = fetchPanel,
                MaxHeight = 440,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = L.Get("Main_Fetch"),
            CloseButtonText = L.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await fetchDialog.ShowAsync() != ContentDialogResult.Primary) return;

        ShowKernelOverlay(L.Get("Main_OvFetchingToml"));
        _tomlCts = new CancellationTokenSource();
        IReadOnlyList<TomlFetchResult> results;
        try
        {
            var status = new Progress<string>(msg => KernelOverlayStatus.Text = msg);
            var progress = new Progress<int>(pct => UpdateKernelOverlayProgress(pct));
            ShowKernelDownloadHint();
            results = await _service.FetchTomlFilesAsync(info, status, progress, _tomlCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(L.Get("Main_DialogError"), L.Get("Main_TomlFailed", ex.Message));
            return;
        }
        finally
        {
            _tomlCts?.Dispose();
            _tomlCts = null;
            HideKernelOverlay();
        }

        var ok = results.Where(r => r.Success).ToList();
        var failed = results.Where(r => !r.Success).ToList();
        var notFound = failed.Where(r => r.NotFound).ToList();
        var netFailed = failed.Where(r => !r.NotFound).ToList();
        LogService.Info(L.Get("LogCat_Op"), L.Get("Main_LogTomlDone", ok.Count, failed.Count, notFound.Count));

        var resultPanel = new StackPanel { MaxWidth = 560 };
        if (ok.Count > 0)
        {
            resultPanel.Children.Add(new TextBlock
            {
                Text = L.Get("Main_TomlReady"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2)
            });
            foreach (var r in ok)
            {
                resultPanel.Children.Add(new TextBlock
                {
                    Text = $"• {r.FileName}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = secondary,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
        }
        if (notFound.Count > 0)
        {
            resultPanel.Children.Add(new TextBlock
            {
                Text = L.Get("Main_TomlNoUpstream"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 2)
            });
            foreach (var r in notFound)
            {
                resultPanel.Children.Add(new TextBlock
                {
                    Text = $"• {r.FileName}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = secondary,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
            resultPanel.Children.Add(new TextBlock
            {
                Text = L.Get("Main_TomlBetaHint"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        if (netFailed.Count > 0)
        {
            resultPanel.Children.Add(new TextBlock
            {
                Text = L.Get("Main_TomlDlFailed"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 2)
            });
            foreach (var r in netFailed)
            {
                resultPanel.Children.Add(new TextBlock
                {
                    Text = $"• {r.FileName}：{r.Error}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = secondary,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
            resultPanel.Children.Add(new TextBlock
            {
                Text = L.Get("Main_TomlCheckNet"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        // 全失败就别留重启按钮和重启文案：啥都没拿到，重启也没用
        var anyOk = ok.Count > 0;
        if (anyOk)
        {
            resultPanel.Children.Add(new TextBlock
            {
                Text = L.Get("Main_TomlRestartHint"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        var resultDialog = new ContentDialog
        {
            Title = failed.Count == 0 ? L.Get("Main_TomlDone") : L.Get("Main_TomlPartial"),
            Content = new ScrollViewer
            {
                Content = resultPanel,
                MaxHeight = 440,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = anyOk ? L.Get("Main_FabRestart") : L.Get("Common_Confirm"),
            SecondaryButtonText = anyOk ? L.Get("Common_Confirm") : null,
            DefaultButton = ContentDialogButton.Primary
        };
        var dialogResult = await resultDialog.ShowAsync();
        if (!anyOk || dialogResult != ContentDialogResult.Primary) return;

        var restartResult = SteamProcess.RestartSteam(_steamPathService);
        if (!restartResult.Ok)
            await ShowDialogAsync(restartResult.Title, restartResult.Message);
    }
}
