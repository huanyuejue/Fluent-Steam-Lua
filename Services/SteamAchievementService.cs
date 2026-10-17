using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SAM.API;
using SAM.API.Types;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public sealed class SteamAchievementService : ISteamAchievementService
{
    private Client? _client;
    private TaskCompletionSource<UserStatsReceived>? _pendingStatsTcs;
    private string _steamPath = "";
    private bool _callbackErrorLogged;

    private static readonly string[] AppTypes =
    [
        "Game", "Demo", "Mod"
    ];

    private const int StatsReadyTimeoutMs = 15000;
    private const int StatsPollIntervalMs = 20;

    public bool IsConnected => _client != null;

    public string? LastError { get; private set; }

    public bool Connect(uint appId = 0)
    {
        if (_client != null) return true;

        try
        {
            _steamPath = Steam.GetInstallPath() ?? "";
            if (string.IsNullOrEmpty(_steamPath))
            {
                LastError = L.Get("Svc_AchNoSteamPath");
                return false;
            }

            // SteamAppId 必须在 steamclient 首次加载前设置，否则该进程的
            // stats 上下文固定为默认值（worker 子进程模式依赖此行为）
            if (appId > 0)
            {
                Environment.SetEnvironmentVariable("SteamAppId", appId.ToString(CultureInfo.InvariantCulture));
            }

            var client = new Client();
            client.Initialize(0);

            var callback = client.CreateAndRegisterCallback<SAM.API.Callbacks.UserStatsReceived>();
            callback.OnRun += OnUserStatsReceived;

            // 回调只在 WaitForStatsReady 等待期内由手动 PumpCallbacks 消费，
            // 常驻 UI 计时器泵在等待期外零可观察效应，直接去掉省 10Hz UI 唤醒
            _client = client;
            LastError = null;
            return true;
        }
        catch (ClientInitializeException ex)
        {
            LastError = TranslateInitFailure(ex);
            LogService.Error(L.Get("LogCat_Achieve"), L.Get("Svc_AchConnectInitFailed", ex));
            TryDisposeClient();
            return false;
        }
        catch (Exception ex)
        {
            LastError = L.Get("Svc_AchConnectFailed", ex.Message);
            LogService.Error(L.Get("LogCat_Achieve"), L.Get("Svc_AchConnectFailed", ex));
            TryDisposeClient();
            return false;
        }
    }

    private static string TranslateInitFailure(ClientInitializeException ex)
    {
        switch (ex.Failure)
        {
            case ClientInitializeFailure.GetInstallPath:
                return L.Get("Svc_AchInitGetPath");

            case ClientInitializeFailure.Load:
                return L.Get("Svc_AchInitLoad");

            case ClientInitializeFailure.CreateSteamClient:
                return L.Get("Svc_AchInitCreateClient");

            case ClientInitializeFailure.CreateSteamPipe:
            {
                // 数组本身不可释放，逐个释放元素句柄
                var procs = Process.GetProcessesByName("steam");
                try
                {
                    return procs.Length == 0
                        ? L.Get("Svc_AchInitNoSteamProc")
                        : L.Get("Svc_AchInitProcBusy");
                }
                finally
                {
                    foreach (var p in procs)
                        try { p.Dispose(); } catch { }
                }
            }

            case ClientInitializeFailure.ConnectToGlobalUser:
                return L.Get("Svc_AchInitConnectUser");

            case ClientInitializeFailure.AppIdMismatch:
                return L.Get("Svc_AchInitAppIdMismatch");

            default:
                return L.Get("Svc_AchConnectFailed", ex.Message);
        }
    }

    private void OnUserStatsReceived(UserStatsReceived param)
    {
        _pendingStatsTcs?.TrySetResult(param);
    }

    private void TryDisposeClient()
    {
        _client?.Dispose();
        _client = null;
    }

    /// <summary>手动泵回调（worker 进程内无 UI 线程轮询时的等待方式）。</summary>
    private void PumpCallbacks()
    {
        try { _client?.RunCallbacks(false); _callbackErrorLogged = false; }
        catch (Exception ex)
        {
            if (!_callbackErrorLogged)
            {
                _callbackErrorLogged = true;
                LogService.Warn(L.Get("LogCat_Achieve"), L.Get("Svc_AchCallbackFailed", ex.Message));
            }
        }
    }

    /// <summary>预热：等待首次 UserStatsReceived 回调（steamclient 会话就绪），供 serve 模式启动时调用。</summary>
    public void WarmUpStats()
    {
        WaitForStatsReady(out _);
    }

    /// <summary>等待 UserStatsReceived 回调（自旋 pump，最多 15 秒）。</summary>
    private bool WaitForStatsReady(out UserStatsReceived param)
    {
        param = default;
        if (_client == null) return false;

        var tcs = new TaskCompletionSource<UserStatsReceived>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingStatsTcs = tcs;

        var steamId = _client.SteamUser.GetSteamId();
        if (_client.SteamUserStats.RequestUserStats(steamId) == CallHandle.Invalid)
        {
            _pendingStatsTcs = null;
            LastError = L.Get("Svc_AchRequestStatsFailed");
            return false;
        }

        var sw = Stopwatch.StartNew();
        while (!tcs.Task.IsCompleted && sw.ElapsedMilliseconds < StatsReadyTimeoutMs)
        {
            PumpCallbacks();
            Thread.Sleep(StatsPollIntervalMs);
        }
        _pendingStatsTcs = null;

        if (!tcs.Task.IsCompleted)
        {
            LastError = L.Get("Svc_AchWaitTimeout");
            return false;
        }

        param = tcs.Task.Result;
        if (param.Result != 1)
        {
            LastError = L.Get("Svc_AchGetStatsFailed", param.Result);
            return false;
        }

        return true;
    }

    public Task<List<AchievementGameInfo>> ParseOwnedGamesAsync()
    {
        return Task.Run(() =>
        {
            var result = new List<AchievementGameInfo>();
            if (_client == null) return result;

            try
            {
                var appInfoPath = Path.Combine(_steamPath, "appcache", "appinfo.vdf");
                if (File.Exists(appInfoPath) == false)
                {
                    LastError = L.Get("Svc_AchAppInfoMissing", appInfoPath);
                    return result;
                }

                var entries = AppInfoVdf.Parse(appInfoPath);
                foreach (var entry in entries)
                {
                    if (AppTypes.Contains(entry.Type) == false)
                    {
                        continue;
                    }

                    result.Add(new AchievementGameInfo
                    {
                        AppId = entry.AppId,
                        Name = string.IsNullOrEmpty(entry.Name) ? $"App {entry.AppId}" : entry.Name!
                    });
                }

                if (result.Count == 0)
                {
                    LastError = L.Get("Svc_AchAppInfoEmpty");
                }

                return result;
            }
            catch (Exception ex)
            {
                LastError = L.Get("Svc_AchLoadGamesFailed", ex);
                return result;
            }
        });
    }

    public List<AchievementGameInfo> FilterSubscribed(List<AchievementGameInfo> games)
    {
        var result = new List<AchievementGameInfo>(games.Count);
        foreach (var game in games)
        {
            if (_client?.SteamApps008.IsSubscribedApp(game.AppId) == true)
            {
                result.Add(game);
            }
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>worker 进程内同步读取成就与统计（SteamAppId 已在 Connect 前设置）。</summary>
    public WorkerLoadResult? LoadWorkerData(uint appId)
    {
        if (_client == null)
        {
            LastError = L.Get("Svc_AchNotConnected");
            return null;
        }

        if (WaitForStatsReady(out _) == false)
        {
            return null;
        }

        try
        {
            var language = _client.SteamApps008.GetCurrentGameLanguage() ?? "english";

            var schema = LoadSchema(appId, language);
            if (schema == null)
            {
                LastError = L.Get("Svc_AchSchemaMissing");
                return null;
            }

            var achievements = new List<WorkerAchievementData>();
            foreach (var def in schema.AchievementDefinitions)
            {
                if (string.IsNullOrEmpty(def.Id)) continue;

                if (_client.SteamUserStats.GetAchievementAndUnlockTime(def.Id, out var isAchieved, out var unlockTime) == false)
                {
                    continue;
                }

                achievements.Add(new WorkerAchievementData
                {
                    Id = def.Id,
                    Name = def.Name,
                    Description = def.Description,
                    IconNormal = def.IconNormal,
                    IconLocked = def.IconLocked,
                    Hidden = def.IsHidden,
                    Permission = def.Permission,
                    Achieved = isAchieved,
                    UnlockTimeUtc = isAchieved && unlockTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(unlockTime).ToUnixTimeSeconds() : null
                });
            }

            return new WorkerLoadResult
            {
                Ok = true,
                AppId = appId,
                Achievements = achievements
            };
        }
        catch (Exception ex)
        {
            LastError = L.Get("Svc_AchLoadFailed", ex.Message);
            return null;
        }
    }

    /// <summary>worker 进程内同步应用修改并写回 Steam。</summary>
    public WorkerSaveResult SaveWorkerData(uint appId, WorkerSaveRequest request)
    {
        var result = new WorkerSaveResult();
        if (_client == null)
        {
            result.Message = L.Get("Svc_AchNotConnected");
            return result;
        }

        try
        {
            if (WaitForStatsReady(out _) == false)
            {
                result.Message = LastError ?? L.Get("Svc_AchNoGameStats");
                return result;
            }

            foreach (var change in request.Achievements)
            {
                if (_client.SteamUserStats.SetAchievement(change.Id, change.Achieved) == false)
                {
                    result.Message = L.Get("Svc_AchSetFailed", change.Id);
                    return result;
                }
            }

            if (_client.SteamUserStats.StoreStats() == false)
            {
                result.Message = L.Get("Svc_AchStoreStatsFailed");
                return result;
            }

            result.Ok = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Message = ex.ToString();
            return result;
        }
    }

    private sealed record SchemaData(
        List<AchievementDefinition> AchievementDefinitions);

    private SchemaData? LoadSchema(uint appId, string language)
    {
        var path = Path.Combine(_steamPath, "appcache", "stats", $"UserGameStatsSchema_{appId}.bin");
        var kv = KeyValue.LoadAsBinary(path);
        if (kv == null) return null;

        var stats = kv[appId.ToString(CultureInfo.InvariantCulture)]["stats"];
        if (stats.Valid == false || stats.Children == null) return null;

        var achievements = new List<AchievementDefinition>();

        foreach (var stat in stats.Children)
        {
            if (stat.Valid == false) continue;

            UserStatType type;

            var typeNode = stat["type"];
            if (typeNode.Valid == true && typeNode.Type == KeyValueType.String)
            {
                if (Enum.TryParse((string)typeNode.Value, true, out type) == false)
                {
                    type = UserStatType.Invalid;
                }
            }
            else
            {
                type = UserStatType.Invalid;
            }

            if (type == UserStatType.Invalid)
            {
                var typeIntNode = stat["type_int"];
                var rawType = typeIntNode.Valid == true
                    ? typeIntNode.AsInteger(0)
                    : typeNode.AsInteger(0);
                type = (UserStatType)rawType;
            }

            switch (type)
            {
                case UserStatType.Invalid:
                    break;

                case UserStatType.Achievements:
                case UserStatType.GroupAchievements:
                {
                    if (stat.Children != null)
                    {
                        foreach (var bits in stat.Children.Where(
                            b => string.Compare(b.Name, "bits", StringComparison.InvariantCultureIgnoreCase) == 0))
                        {
                            if (bits.Valid == false || bits.Children == null) continue;

                            foreach (var bit in bits.Children)
                            {
                                var id = bit["name"].AsString("");
                                // display 节点每成就只取一次复用，避免同一小列表重复线性查找 5 次
                                var display = bit["display"];
                                achievements.Add(new AchievementDefinition
                                {
                                    Id = id,
                                    Name = GetLocalizedString(display["name"], language, id),
                                    Description = GetLocalizedString(display["desc"], language, ""),
                                    IconNormal = display["icon"].AsString(""),
                                    IconLocked = display["icon_gray"].AsString(""),
                                    IsHidden = display["hidden"].AsBoolean(false),
                                    Permission = bit["permission"].AsInteger(0)
                                });
                            }
                        }
                    }

                    break;
                }
            }
        }

        return new SchemaData(achievements);
    }

    private static string GetLocalizedString(KeyValue kv, string language, string defaultValue)
    {
        var name = kv[language].AsString("");
        if (string.IsNullOrEmpty(name) == false)
        {
            return name;
        }

        if (language != "english")
        {
            name = kv["english"].AsString("");
            if (string.IsNullOrEmpty(name) == false)
            {
                return name;
            }
        }

        name = kv.AsString("");
        if (string.IsNullOrEmpty(name) == false)
        {
            return name;
        }

        return defaultValue;
    }

    public void Dispose()
    {
        TryDisposeClient();
    }
}
