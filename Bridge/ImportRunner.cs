using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SteamLuaManager.Models;
using SteamLuaManager.Services;
using SteamLuaManager;

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
    private readonly BridgeGameNameResolver _names;

    public ImportRunner(ISteamDepotService depot, ISteamPathService steamPath, ISettingsService settings, BridgeGameNameResolver names)
    {
        _depot = depot;
        _steamPath = steamPath;
        _settings = settings;
        _names = names;
    }

    /// <summary>
    /// 执行一次入库。行为链先铺满所有阶段再逐步点亮，
    /// 用户能直接看出还剩几步。
    /// </summary>
    public async Task RunAsync(ImportJob job, CancellationToken ct)
    {
        var stepPrepare = new ImportStep("Bridge_ImportStepPrepare");
        var stepQuery = new ImportStep("Bridge_ImportStepQuery");
        var stepBuild = new ImportStep("Bridge_ImportStepBuild");
        var stepDlc = new ImportStep("Bridge_ImportStepDlc");
        var stepWrite = new ImportStep("Bridge_ImportStepWrite");

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
                stepPrepare.SetLocalizedDetail("Bridge_ImportLuaFolderMissing");
                Fail(job, "Bridge_ImportSteamMissing");
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
                stepQuery.SetLocalizedDetail("Bridge_ImportKeysMissing");
                Fail(job, "Bridge_ImportKeysFailed");
                return;
            }
            var info = await _depot.QueryAppAsync(job.AppId, ct);
            if (info == null)
            {
                stepQuery.State = StepState.Failed;
                stepQuery.SetLocalizedDetail("Bridge_ImportQueryNoResponse");
                Fail(job, "Bridge_ImportQueryFailed");
                return;
            }
            var nameLanguage = BridgeLocalization.Language;
            var resolvedName = await _names.ResolveAsync(job.AppId,
                string.IsNullOrWhiteSpace(job.DisplayName) ? info.AppName : job.DisplayName, ct);
            if (nameLanguage == BridgeLocalization.Language) job.DisplayName = resolvedName;
            stepQuery.State = StepState.Done;
            // The title can be resolved again when the user changes language.
            stepQuery.SetLocalizedDetail("Bridge_ImportDepotCount", new Func<object?>(() => job.DisplayName), info.GameDepots.Count);

            if (info.GameDepots.Count == 0)
            {
                stepBuild.State = StepState.Failed;

                // 仓储查询为空有三种完全不同的原因，分开说清楚才不会让用户以为游戏没数据：
                // 有 token 说明这张表认可它，但要走 Steam 协议的完整查询，桥接进程不承担；
                // 没 token 说明这张表本身就不认识它，主程序也查不出更多东西。
                var hasToken = await _depot.HasAppTokenAsync(job.AppId, ct);
                if (hasToken)
                {
                    stepBuild.SetLocalizedDetail("Bridge_ImportTokenRequired");
                    Fail(job, "Bridge_ImportTokenProtected");
                }
                else
                {
                    stepBuild.SetLocalizedDetail("Bridge_ImportDepotsEmpty");
                    Fail(job, "Bridge_ImportDepotsUnavailable");
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
                    Fail(job, "Bridge_ImportGenerateFailed");
                    return;
                }
                stepDlc.State = StepState.Done;
                stepDlc.SetLocalizedDetail("Bridge_ImportDlcCount", dlcCount);

                stepWrite.State = StepState.Done;
                stepWrite.Detail = Path.GetFileName(luaPath);
                Succeed(job, stepWrite.Detail);
            }
            else
            {
                stepDlc.State = StepState.Skipped;
                stepDlc.SetLocalizedDetail("Bridge_ImportNoDlc");

                var luaPath = await _depot.GenerateLuaAsync(
                    job.AppId, ct, pinManifest: false, fetchNames: true);
                if (string.IsNullOrEmpty(luaPath))
                {
                    stepWrite.State = StepState.Failed;
                    Fail(job, "Bridge_ImportGenerateFailed");
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
            job.SetLocalizedError("Bridge_ImportCancelled");
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
            BridgeLog.Write(L.Get("Bridge_LogImportException", job.AppId, ex));
        }
    }

    private static void Fail(ImportJob job, string messageKey, params object?[] arguments)
    {
        job.IsFinished = true;
        job.IsSuccess = false;
        job.SetLocalizedError(messageKey, arguments);
        BridgeLog.Write(L.Get("Bridge_LogImportFailed", job.AppId, job.ErrorMessage));
    }

    private static void Succeed(ImportJob job, string detail)
    {
        job.IsFinished = true;
        job.IsSuccess = true;
        job.ErrorMessage = null;
        BridgeLog.Write(L.Get("Bridge_LogImportSuccess", job.AppId, detail));
    }


}
