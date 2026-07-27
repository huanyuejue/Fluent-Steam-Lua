using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern.Controls;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class ExtractionViewModel : ObservableObject
{
    private readonly ISteamDepotService _depotService;
    private readonly ISteamPathService _steamPathService;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _appId = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _pinManifest;

    [ObservableProperty]
    private bool _extractAchievements;

    public ObservableCollection<string> LogLines { get; } = [];

    public ExtractionViewModel(ISteamDepotService depotService, ISteamPathService steamPathService)
    {
        _depotService = depotService;
        _steamPathService = steamPathService;
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartExtractionAsync()
    {
        var id = AppId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            StatusMessage = "请输入 AppID";
            return;
        }

        if (!int.TryParse(id, out var appId))
        {
            StatusMessage = "AppID 必须为数字";
            return;
        }

        if (IsRunning)
        {
            _cts?.Cancel();
            return;
        }

        IsRunning = true;
        StatusMessage = "正在查询游戏仓库信息...";
        LogLines.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            PostLog($"开始查询 AppID:{id}");

            // 1. Query depot info from api.steamcmd.net
            var queryResult = await Task.Run(() => _depotService.QueryAppAsync(appId, ct), ct);
            if (queryResult == null || queryResult.GameDepots.Count == 0)
            {
                StatusMessage = "查询失败，未找到该游戏的仓库信息";
                PostLog("❌ 查询失败，未找到该游戏的仓库信息");
                return;
            }

            PostLog($"✔ 已获取仓库信息，游戏名称：{queryResult.AppName}");
            PostLog($"找到 {queryResult.GameDepots.Count} 个仓库（已排除共享仓库），{queryResult.DlcAppIds.Count} 个 DLC");
            StatusMessage = "正在读取 Steam 配置...";

            // 2. Read config.vdf and parse depot keys
            var steamPath = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(steamPath))
            {
                StatusMessage = "未检测到 Steam 安装路径";
                PostLog("❌ 未检测到 Steam 安装路径");
                return;
            }

            var vdfPath = Path.Combine(steamPath, "config", "config.vdf");
            if (!File.Exists(vdfPath))
            {
                StatusMessage = "未找到 config.vdf";
                PostLog($"❌ 未找到 config.vdf：{vdfPath}");
                return;
            }

            var vdfContent = await File.ReadAllTextAsync(vdfPath, ct);
            var depotKeys = VdfHelper.ParseDepotKeys(vdfContent);
            PostLog($"✔ 已读取 Steam 配置，找到 {depotKeys.Count} 个仓库密钥");

            // 3. Generate lua content
            var sb = new StringBuilder();
            sb.AppendLine("-- lua by Fluent-Steam-Lua (https://github.com/huanyuejue/Fluent-Steam-Lua)");
            sb.AppendLine();

            var matchedCount = 0;

            if (depotKeys.TryGetValue(id, out var mainKey))
            {
                sb.AppendLine($"addappid({id}, 1, \"{mainKey}\")");
                matchedCount++;
                PostLog($"✔ 主仓库 {id} 密钥匹配成功");
            }
            else
            {
                sb.AppendLine($"addappid({id})");
                PostLog($"⚠ 主仓库 {id} 未找到密钥，跳过加密");
            }

            foreach (var depot in queryResult.GameDepots)
            {
                var depotIdStr = depot.DepotId.ToString();
                if (depotKeys.TryGetValue(depotIdStr, out var key))
                {
                    sb.AppendLine($"addappid({depot.DepotId}, 1, \"{key}\")");
                    if (PinManifest && !string.IsNullOrEmpty(depot.ManifestId))
                        sb.AppendLine($"setManifestid({depot.DepotId},\"{depot.ManifestId}\",0)");
                    depot.Key = key;
                    depot.IsMatched = true;
                    matchedCount++;
                    PostLog($"✔ 仓库 {depot.DepotId} 密钥匹配成功");
                }
                else
                {
                    PostLog($"⚠ 仓库 {depot.DepotId} 未找到密钥，跳过");
                }
            }

            // 5. Process DLCs — query each DLC's depots for key matching
            var mainDepotIds = new HashSet<int>(queryResult.GameDepots.Select(d => d.DepotId));
            foreach (var dlcAppId in queryResult.DlcAppIds)
            {
                var dlcIdStr = dlcAppId.ToString();
                var isMainDepot = mainDepotIds.Contains(dlcAppId);

                // If DLC app ID is also a main game depot, skip addappid (already handled above)
                if (!isMainDepot)
                {
                    if (depotKeys.TryGetValue(dlcIdStr, out var dlcKey))
                    {
                        sb.AppendLine($"addappid({dlcAppId}, 1, \"{dlcKey}\")");
                        matchedCount++;
                        PostLog($"✔ DLC {dlcAppId} 密钥匹配成功");
                    }
                    else
                    {
                        sb.AppendLine($"addappid({dlcAppId})");
                    }
                }

                // Query DLC's own sub-depots for additional key matching
                var dlcResult = await Task.Run(() => _depotService.QueryAppAsync(dlcAppId, ct), ct);
                if (dlcResult != null)
                {
                    int subMatched = 0;
                    foreach (var depot in dlcResult.GameDepots)
                    {
                        if (depotKeys.TryGetValue(depot.DepotId.ToString(), out var depotKey))
                        {
                            sb.AppendLine($"addappid({depot.DepotId}, 1, \"{depotKey}\")");
                            if (PinManifest && !string.IsNullOrEmpty(depot.ManifestId))
                                sb.AppendLine($"setManifestid({depot.DepotId},\"{depot.ManifestId}\",0)");
                            matchedCount++;
                            subMatched++;
                        }
                    }
                    if (dlcResult.GameDepots.Count > 0)
                        PostLog(isMainDepot
                            ? $"✔ DLC {dlcAppId}（跳过，已是主仓库）{subMatched}/{dlcResult.GameDepots.Count} 子仓库匹配密钥"
                            : $"✔ DLC {dlcAppId} {subMatched}/{dlcResult.GameDepots.Count} 子仓库匹配密钥");
                    else
                        PostLog(isMainDepot
                            ? $"ℹ DLC {dlcAppId}（跳过，已是主仓库），无额外子仓库"
                            : $"ℹ DLC {dlcAppId} 无子仓库");
                }
                else
                {
                    PostLog(isMainDepot
                        ? $"ℹ DLC {dlcAppId}（跳过，已是主仓库），无额外子仓库信息"
                        : $"ℹ DLC {dlcAppId} 无子仓库信息");
                }
            }

            if (matchedCount == 0)
            {
                StatusMessage = "未找到任何可用密钥";
                PostLog("❌ 本地 config.vdf 中未找到该游戏及其仓库的任何密钥");
                PostLog("提示：请确保 Steam 已登录正版账号并启动过该游戏");
                return;
            }

            // 4. Save to Cache\dump\{appid}\
            StatusMessage = "正在保存 Lua 清单...";
            var dumpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "dump", id);
            if (!Directory.Exists(dumpDir))
                Directory.CreateDirectory(dumpDir);

            var luaPath = Path.Combine(dumpDir, $"{id}.lua");
            await File.WriteAllTextAsync(luaPath, sb.ToString(), ct);

            StatusMessage = $"提取完成，匹配到 {matchedCount} 个密钥";
            PostLog($"✔ 提取成功！文件已保存到：{luaPath}");

            if (ExtractAchievements)
            {
                var statsDir = Path.Combine(steamPath, "appcache", "stats");
                if (Directory.Exists(statsDir))
                {
                    var achFiles = Directory.GetFiles(statsDir, $"*{id}*")
                        .Where(f => f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (achFiles.Count > 0)
                    {
                        var largest = achFiles.OrderByDescending(f => new FileInfo(f).Length).First();
                        var dest = Path.Combine(dumpDir, Path.GetFileName(largest));
                        File.Copy(largest, dest, true);
                        PostLog($"✔ 已提取成就文件：{Path.GetFileName(largest)}");
                    }
                    else
                    {
                        PostLog("ℹ 未找到该游戏的成就缓存文件");
                    }
                }
                else
                {
                    PostLog($"ℹ 成就缓存目录不存在：{statsDir}");
                }
            }

            ShowOpenDirectoryPrompt(dumpDir);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消";
            PostLog("⏹ 操作已取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"异常: {ex.Message}";
            PostLog($"❌ 异常: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ShowOpenDirectoryPrompt(string directory)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var dialog = new ContentDialog
            {
                Title = "提取完成",
                Content = new TextBlock
                {
                    Text = $"提取完成！文件已保存到:\n{directory}\n\n是否打开该目录？",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                PrimaryButtonText = "打开目录",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                Process.Start("explorer.exe", directory);
        }));
    }

    private void PostLog(string message)
    {
        _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => LogLines.Add(message)));
    }
}
