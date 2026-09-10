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
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace SteamLuaManager.ViewModels;

public partial class ExtractionViewModel : ObservableObject, IDisposable
{
    private readonly ISteamDepotService _depotService;
    private readonly ISteamPathService _steamPathService;
    private readonly IDialogService _dialogService;
    private readonly ISteamAccountService _accountService;
    private CancellationTokenSource? _cts;
    private bool _disposed;

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

    [ObservableProperty]
    private bool _downloadManifests = true;

    public ObservableCollection<string> LogLines { get; } = [];

    // 提取页常驻显示登录态：活会话或缓存令牌任一成立即视为已登录；
    // 令牌只在点提取时兑现，平时不联网
    public bool IsLoggedOn => _accountService.IsLoggedOn || _accountService.HasCachedToken();
    public string LoginStatusText => _accountService.IsLoggedOn
        ? $"已登录：{_accountService.CurrentAccountName}"
        : (_accountService.SavedAccountName is string saved
            ? $"已登录：{saved}"
            : "未登录");

    // 待决议的 depot 行：拼装前只收集再统一写出。
    // IsDlc 按 ID 判定（含与主 depot 同 ID 的 DLC，这类行 Scope 是"仓库"不能按 Scope 判）。
    // HasDepots 仅 DLC 行有意义：无独立仓库的 DLC 裸 addappid 本身就是完整解锁，必须写出。
    private sealed record PendingDepot(uint Id, uint ParentAppId, string? ManifestId, string Scope, int? DlcAppId, bool IsSub, bool IsDlc, bool HasDepots);

    public ExtractionViewModel(ISteamDepotService depotService, ISteamPathService steamPathService, IDialogService dialogService, ISteamAccountService accountService)
    {
        _depotService = depotService;
        _steamPathService = steamPathService;
        _dialogService = dialogService;
        _accountService = accountService;
        _accountService.SessionChanged += OnSessionChanged;
    }

    private void OnSessionChanged()
    {
        OnPropertyChanged(nameof(IsLoggedOn));
        OnPropertyChanged(nameof(LoginStatusText));
    }

    [RelayCommand]
    private async Task OpenLoginAsync() => await ShowLoginWindowAsync();

    [RelayCommand]
    private void Logout()
    {
        _accountService.LogOff();
        PostLog("已退出登录");
    }

    private async Task<bool> ShowLoginWindowAsync()
    {
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var win = new Views.LoginWindow(_accountService);
            win.Owner = Application.Current.MainWindow;
            return win.ShowDialog() == true;
        });
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

            // 0. 登录门禁：先兑现缓存令牌静默登录，不行再弹登录窗
            if (!_accountService.IsLoggedOn)
            {
                if (_accountService.HasCachedToken())
                {
                    PostLog("正在使用缓存令牌登录...");
                    var restored = await _accountService.TryRestoreSessionAsync(ct);
                    if (!restored.Success)
                        PostLog($"缓存登录失败：{restored.Message}");
                }
                if (!_accountService.IsLoggedOn)
                {
                    PostLog("提取需要先登录 Steam 账号");
                    var loginOk = await ShowLoginWindowAsync();
                    if (!loginOk || !_accountService.IsLoggedOn)
                    {
                        StatusMessage = "未登录，已取消提取";
                        PostLog("未登录，已取消提取");
                        return;
                    }
                }
            }
            PostLog($"当前账号：{_accountService.CurrentAccountName}");

            // 1. Query depot info from api.steamcmd.net
            var queryResult = await _depotService.QueryAppAsync(appId, ct);
            if (queryResult == null || queryResult.GameDepots.Count == 0)
            {
                StatusMessage = "查询失败，未找到该游戏的仓库信息";
                PostLog("查询失败，未找到该游戏的仓库信息");
                return;
            }

            PostLog($"已获取仓库信息，游戏名称：{queryResult.AppName}");
            PostLog($"找到 {queryResult.GameDepots.Count} 个仓库（已排除共享仓库），{queryResult.DlcAppIds.Count} 个 DLC");
            StatusMessage = "正在整理仓库信息...";

            // 2. Steam 路径只用于成就提取；密钥走纯云端，不再读 config.vdf
            var steamPath = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(steamPath))
                PostLog("未检测到 Steam 安装路径，成就提取将不可用");

            // 3. 待决议行：主 App 行 + 主 depots（顺序与旧逻辑一致，先收集不写出）
            var dlcIdSet = new HashSet<int>(queryResult.DlcAppIds);
            var lines = new List<PendingDepot> { new((uint)appId, (uint)appId, null, "主仓库", null, false, false, false) };
            foreach (var depot in queryResult.GameDepots)
                lines.Add(new PendingDepot((uint)depot.DepotId, (uint)appId, depot.ManifestId, "仓库", null, false, dlcIdSet.Contains(depot.DepotId), true));

            // 4. 各 DLC 的仓库信息并发查询并缓存（DLC 大户串行太慢，限流 6）
            var mainDepotIds = new HashSet<int>(queryResult.GameDepots.Select(d => d.DepotId));
            var dlcIds = queryResult.DlcAppIds.ToArray();
            var dlcResultArr = new DepotQueryResult?[dlcIds.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, dlcIds.Length),
                new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
                async (i, innerCt) => { dlcResultArr[i] = await _depotService.QueryAppAsync(dlcIds[i], innerCt); });
            var dlcResults = new Dictionary<int, DepotQueryResult?>();
            for (int i = 0; i < dlcIds.Length; i++)
            {
                var dlcAppId = dlcIds[i];
                var dlcResult = dlcResultArr[i];
                dlcResults[dlcAppId] = dlcResult;
                if (!mainDepotIds.Contains(dlcAppId))
                    lines.Add(new PendingDepot((uint)dlcAppId, (uint)dlcAppId, null, $"DLC {dlcAppId}", dlcAppId, false, true,
                        (dlcResultArr[i]?.GameDepots.Count ?? 0) > 0));
                if (dlcResult != null)
                    foreach (var depot in dlcResult.GameDepots)
                        lines.Add(new PendingDepot((uint)depot.DepotId, (uint)dlcAppId, depot.ManifestId, $"DLC {dlcAppId}", dlcAppId, true, false, false));
            }

            // 5. 纯云端取 key（登录门禁已保证会话，会话内并行下发）
            StatusMessage = "正在获取仓库密钥...";
            var mergedKeys = new Dictionary<uint, string>();
            var fetched = await _accountService.GetDepotKeysAsync(
                lines.Select(m => (m.Id, m.ParentAppId)), ct);
            foreach (var (depId, k) in fetched)
                mergedKeys[depId] = Convert.ToHexString(k).ToLowerInvariant();
            PostLog($"密钥获取 {mergedKeys.Count}/{lines.Count} 个");

            // 6. 单遍拼装：有独立仓库但无 key 的 DLC 行跳过（无权限写了也无效，含与主 depot 同 ID 的 DLC）；
            // 无独立仓库的 DLC 裸行本身就是完整解锁，必须写出；子仓库无 key 不写行；主行/纯主 depot 无 key 保留裸行
            var sb = new StringBuilder();
            sb.AppendLine("-- lua by Fluent-Steam-Lua (https://github.com/huanyuejue/Fluent-Steam-Lua)");
            sb.AppendLine();

            var matchedCount = 0;
            var dlcSubMatched = new Dictionary<int, int>();
            foreach (var line in lines)
            {
                if (mergedKeys.TryGetValue(line.Id, out var key))
                {
                    sb.AppendLine($"addappid({line.Id}, 1, \"{key}\")");
                    if (PinManifest && !string.IsNullOrEmpty(line.ManifestId))
                        sb.AppendLine($"setManifestid({line.Id},\"{line.ManifestId}\",0)");
                    matchedCount++;
                    if (line.IsSub && line.DlcAppId is int subDlcId)
                        dlcSubMatched[subDlcId] = dlcSubMatched.TryGetValue(subDlcId, out var c) ? c + 1 : 1;
                    PostLog($"{line.Scope} {line.Id} 密钥匹配成功");
                }
                else if (line.Scope == "主仓库")
                {
                    sb.AppendLine($"addappid({line.Id})");
                    PostLog($"主仓库 {line.Id} 未找到密钥，跳过加密");
                }
                else if (line.IsSub)
                {
                    // 子仓库无 key 不写行
                }
                else if (line.IsDlc && line.HasDepots)
                {
                    PostLog($"DLC {line.Id} 无密钥（无权限），跳过");
                }
                else if (line.IsDlc)
                {
                    sb.AppendLine($"addappid({line.Id})");
                    PostLog($"DLC {line.Id} 无独立仓库，写入解锁行");
                }
                else
                {
                    sb.AppendLine($"addappid({line.Id})");
                    PostLog($"仓库 {line.Id} 未找到密钥，跳过");
                }
            }

            // DLC 子仓库小结（文案与旧逻辑一致）
            foreach (var dlcAppId in queryResult.DlcAppIds)
            {
                var isMainDepot = mainDepotIds.Contains(dlcAppId);
                var dlcResult = dlcResults[dlcAppId];
                dlcSubMatched.TryGetValue(dlcAppId, out var subMatched);
                if (dlcResult != null)
                {
                    if (dlcResult.GameDepots.Count > 0)
                        PostLog(isMainDepot
                            ? $"DLC {dlcAppId}（跳过，已是主仓库）{subMatched}/{dlcResult.GameDepots.Count} 子仓库匹配密钥"
                            : $"DLC {dlcAppId} {subMatched}/{dlcResult.GameDepots.Count} 子仓库匹配密钥");
                    else
                        PostLog(isMainDepot
                            ? $"DLC {dlcAppId}（跳过，已是主仓库），无额外子仓库"
                            : $"DLC {dlcAppId} 无子仓库");
                }
                else
                {
                    PostLog(isMainDepot
                        ? $"DLC {dlcAppId}（跳过，已是主仓库），无额外子仓库信息"
                        : $"DLC {dlcAppId} 无子仓库信息");
                }
            }

            if (matchedCount == 0)
            {
                StatusMessage = "未找到任何可用密钥";
                PostLog("账号未找到该游戏及其仓库的任何可用密钥");
                PostLog("提示：请确认登录账号拥有该游戏正版");
                return;
            }

            // 4. Save to Cache\dump\{appid}\
            StatusMessage = "正在保存 Lua 清单...";
            var dumpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "dump", id);
            if (!Directory.Exists(dumpDir))
                Directory.CreateDirectory(dumpDir);

            var luaPath = Path.Combine(dumpDir, $"{id}.lua");
            await File.WriteAllTextAsync(luaPath, sb.ToString(), ct);

            if (DownloadManifests)
            {
                // 登录门禁已保证会话，直接下载；CDN 下载限流 3 并行，结果按原顺序打日志
                StatusMessage = "正在下载 Manifest 清单文件...";
                var seen = new HashSet<(uint, ulong)>();
                var targets = new List<(PendingDepot Line, ulong Gid)>();
                foreach (var line in lines)
                {
                    if (string.IsNullOrEmpty(line.ManifestId)) continue;
                    if (!ulong.TryParse(line.ManifestId, out var gid) || gid == 0) continue;
                    if (!seen.Add((line.Id, gid))) continue;
                    targets.Add((line, gid));
                }
                var dlResults = new (PendingDepot Line, ManifestDownloadResult R)?[targets.Count];
                await Parallel.ForEachAsync(Enumerable.Range(0, targets.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
                    async (i, innerCt) =>
                    {
                        var (line, gid) = targets[i];
                        mergedKeys.TryGetValue(line.Id, out var keyHex);
                        byte[]? keyBytes = null;
                        try { keyBytes = string.IsNullOrEmpty(keyHex) ? null : Convert.FromHexString(keyHex); } catch { }
                        var r = await _accountService.DownloadManifestAsync(line.ParentAppId, line.Id, gid, keyBytes, dumpDir, innerCt);
                        dlResults[i] = (line, r);
                    });
                foreach (var item in dlResults)
                {
                    if (item is not { } done) continue;
                    PostLog(done.R.Success && done.R.FilePath != null
                        ? $"已下载 Manifest：{Path.GetFileName(done.R.FilePath)}"
                        : $"Manifest 跳过 ({done.Line.Id})：{done.R.Message}");
                }
            }

            if (ExtractAchievements)
            {
                PostLog("提示：请确认登录账号拥有该游戏并用 Steam 客户端启动过，否则无成就缓存");
                if (string.IsNullOrEmpty(steamPath))
                {
                    PostLog("未检测到 Steam 安装路径，跳过成就提取");
                }
                else
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
                        PostLog($"已提取成就文件：{Path.GetFileName(largest)}");
                    }
                    else
                    {
                        PostLog("未找到该游戏的成就缓存文件");
                    }
                }
                else
                {
                    PostLog($"成就缓存目录不存在：{statsDir}");
                }
                }
            }

            StatusMessage = $"提取完成，匹配到 {matchedCount} 个密钥";
            PostLog($"提取成功！文件已保存到：{luaPath}");

            ShowOpenDirectoryPrompt(dumpDir);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消";
            PostLog("操作已取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"异常: {ex.Message}";
            PostLog($"异常: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void ShowOpenDirectoryPrompt(string directory)
    {
        try
        {
            var confirmed = await _dialogService.ShowConfirmAsync(
                "提取完成",
                $"提取完成！文件已保存到:\n{directory}\n\n是否打开该目录？",
                "打开目录", "关闭");
            if (confirmed)
                Process.Start("explorer.exe", directory);
        }
        catch (Exception ex)
        {
            LogService.Warn("提取", $"显示完成对话框失败: {ex.Message}");
        }
    }

    private void PostLog(string message)
    {
        LogService.Info("提取", message);
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _ = Application.Current.Dispatcher.InvokeAsync(() => LogLines.Add(line));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _accountService.SessionChanged -= OnSessionChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
