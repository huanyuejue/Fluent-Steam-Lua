using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SteamLuaManager.Models;
using SteamLuaManager.Services;

namespace FluentLuaBridge;

/// <summary>
/// 把入库流程拆成用户看得懂的行为链并按顺序执行。
/// 不依赖主程序的 UI，也不改动入库服务本身：每一步都只是调用它的公开方法，
/// 在步与步之间推进链状态。
/// </summary>
internal sealed class ImportRunner
{
    private readonly ISteamDepotService _depot;
    private readonly ISteamPathService _steamPath;
    private readonly ISettingsService _settings;

    public ImportRunner(ISteamDepotService depot, ISteamPathService steamPath, ISettingsService settings)
    {
        _depot = depot;
        _steamPath = steamPath;
        _settings = settings;
    }

    /// <summary>
    /// 执行一次入库。行为链先铺满所有阶段再逐步点亮，
    /// 用户能直接看出还剩几步。
    /// </summary>
    public async Task RunAsync(ImportJob job, CancellationToken ct)
    {
        var stepPrepare = new ImportStep("检查 Steam 目录");
        var stepQuery = new ImportStep("查询游戏信息");
        var stepBuild = new ImportStep("匹配仓库密钥");
        var stepDlc = new ImportStep("补全 DLC");
        var stepWrite = new ImportStep("写入入库文件");

        foreach (var s in new[] { stepPrepare, stepQuery, stepBuild, stepDlc, stepWrite })
        {
            job.Steps.Add(s);
        }

        try
        {
            // ① Steam 目录：拿不到目录后面全白费，先挡在这里
            stepPrepare.State = StepState.Running;
            var luaFolder = _steamPath.GetLuaFolder();
            if (string.IsNullOrEmpty(luaFolder))
            {
                stepPrepare.State = StepState.Failed;
                stepPrepare.Detail = "未找到 Steam Lua 目录";
                Fail(job, "未检测到 Steam 安装目录，请先在主程序中确认路径");
                return;
            }
            stepPrepare.State = StepState.Done;
            stepPrepare.Detail = luaFolder;

            // ② 查询游戏信息：密钥文件在查询内部按需准备，不单独占一行，
            //    失败原因归到这一步上说清楚；顺便先拿到游戏名，让窗口标题立刻有意义
            stepQuery.State = StepState.Running;
            ct.ThrowIfCancellationRequested();
            if (!await _depot.EnsureKeyFilesAsync(ct))
            {
                stepQuery.State = StepState.Failed;
                stepQuery.Detail = "密钥文件获取失败";
                Fail(job, "密钥文件获取失败，请检查网络后重试");
                return;
            }
            var info = await _depot.QueryAppAsync(job.AppId, ct);
            if (info == null)
            {
                stepQuery.State = StepState.Failed;
                stepQuery.Detail = "查询接口无响应";
                Fail(job, "查询游戏信息失败。请检查网络后重试；若持续失败，可在主程序中查询该游戏。");
                return;
            }
            // 商店页传来名字就直接用，避免为了标题再走一次接口，也避免中英文名混用。
            // 只有调用方没带名字时，才用查询结果兜底
            if (string.IsNullOrWhiteSpace(job.DisplayName) && !string.IsNullOrWhiteSpace(info.AppName))
            {
                job.DisplayName = info.AppName;
            }
            stepQuery.State = StepState.Done;
            stepQuery.Detail = $"{(string.IsNullOrWhiteSpace(job.DisplayName) ? $"AppID {job.AppId}" : job.DisplayName)} · {info.GameDepots.Count} 个仓库";

            if (info.GameDepots.Count == 0)
            {
                stepBuild.State = StepState.Failed;

                // 仓储查询为空有三种完全不同的原因，分开说清楚才不会让用户以为游戏没数据：
                // 有 token 说明这张表认可它，但要走 Steam 协议的完整查询，桥接进程不承担；
                // 没 token 说明这张表本身就不认识它，主程序也查不出更多东西。
                var hasToken = await _depot.HasAppTokenAsync(job.AppId, ct);
                if (hasToken)
                {
                    stepBuild.Detail = "需要访问令牌";
                    Fail(job, "该游戏受访问令牌保护，桥接进程无法直接查询。请打开主程序，在游戏列表中对该游戏重试入库。");
                }
                else
                {
                    stepBuild.Detail = "仓库信息为空";
                    Fail(job, "该游戏没有公开的仓库信息，密钥库未收录。常见于尚未发售、已下架或需要账号权限的游戏。");
                }
                return;
            }
            stepBuild.Detail = string.Empty;
            stepBuild.State = StepState.Done;

            // ④ 补全 DLC：DLC 数量不定，用计数显示而不是进度条
            var dlcCount = info.DlcAppIds.Count;
            if (dlcCount > 0)
            {
                stepDlc.State = StepState.Running;
                var progress = new Progress<(int Done, int Total)>(p =>
                {
                    stepDlc.Detail = $"{p.Done}/{p.Total}";
                });

                var luaPath = await _depot.GenerateLuaWithDlcAsync(
                    job.AppId, ct, pinManifest: false, dlcProgress: progress, fetchNames: true);

                if (string.IsNullOrEmpty(luaPath))
                {
                    stepDlc.State = StepState.Failed;
                    Fail(job, "生成入库文件失败，请稍后重试");
                    return;
                }
                stepDlc.State = StepState.Done;
                stepDlc.Detail = $"{dlcCount} 个 DLC";

                stepWrite.State = StepState.Done;
                stepWrite.Detail = Path.GetFileName(luaPath);
                Succeed(job, stepWrite.Detail);
            }
            else
            {
                stepDlc.State = StepState.Skipped;
                stepDlc.Detail = "无 DLC";

                var luaPath = await _depot.GenerateLuaAsync(
                    job.AppId, ct, pinManifest: false, fetchNames: true);
                if (string.IsNullOrEmpty(luaPath))
                {
                    stepWrite.State = StepState.Failed;
                    Fail(job, "生成入库文件失败，请稍后重试");
                    return;
                }
                stepWrite.State = StepState.Done;
                stepWrite.Detail = Path.GetFileName(luaPath);
                Succeed(job, stepWrite.Detail);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var s in job.Steps)
            {
                if (s.State == StepState.Running) s.State = StepState.Failed;
            }
            job.IsFinished = true;
            job.IsSuccess = false;
            job.ErrorMessage = "已取消";
        }
        catch (Exception ex)
        {
            foreach (var s in job.Steps)
            {
                if (s.State is StepState.Running or StepState.Pending) s.State = StepState.Failed;
            }
            job.IsFinished = true;
            job.IsSuccess = false;
            job.ErrorMessage = ex.Message;
            BridgeLog.Write($"AppID {job.AppId} 入库异常: {ex}");
        }
    }

    private static void Fail(ImportJob job, string message)
    {
        job.IsFinished = true;
        job.IsSuccess = false;
        job.ErrorMessage = message;
        BridgeLog.Write($"AppID {job.AppId} 入库失败: {message}");
    }

    private static void Succeed(ImportJob job, string detail)
    {
        job.IsFinished = true;
        job.IsSuccess = true;
        job.ErrorMessage = null;
        BridgeLog.Write($"AppID {job.AppId} 入库成功: {detail}");
    }


}
