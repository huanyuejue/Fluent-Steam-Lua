using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SAM.API;
using SAM.API.Types;
using SteamLuaManager.Models;

namespace SteamLuaManager.Services;

public sealed class SteamAchievementService : ISteamAchievementService
{
    private Client? _client;
    private DispatcherTimer? _callbackTimer;
    private TaskCompletionSource<UserStatsReceived>? _pendingStatsTcs;
    private string _steamPath = "";

    private static readonly string[] AppTypes =
    [
        "Game", "Demo", "Mod"
    ];

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
                LastError = "无法获取 Steam 安装路径";
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

            _callbackTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _callbackTimer.Tick += (_, _) =>
            {
                try { client.RunCallbacks(false); }
                catch { }
            };
            _callbackTimer.Start();

            _client = client;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"连接 Steam 失败：{ex}";
            TryDisposeClient();
            return false;
        }
    }

    private void OnUserStatsReceived(UserStatsReceived param)
    {
        _pendingStatsTcs?.TrySetResult(param);
    }

    private void TryDisposeClient()
    {
        _callbackTimer?.Stop();
        _callbackTimer = null;
        _client?.Dispose();
        _client = null;
    }

    /// <summary>手动泵回调（worker 进程内无 UI 线程轮询时的等待方式）。</summary>
    private void PumpCallbacks()
    {
        try { _client?.RunCallbacks(false); }
        catch { }
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
            LastError = "请求统计数据失败";
            return false;
        }

        var sw = Stopwatch.StartNew();
        while (!tcs.Task.IsCompleted && sw.ElapsedMilliseconds < 15000)
        {
            PumpCallbacks();
            Thread.Sleep(20);
        }
        _pendingStatsTcs = null;

        if (!tcs.Task.IsCompleted)
        {
            LastError = "等待 Steam 响应超时";
            return false;
        }

        param = tcs.Task.Result;
        if (param.Result != 1)
        {
            LastError = $"获取统计数据失败（错误码 {param.Result}），通常表示你不拥有该游戏";
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
                    LastError = $"未找到 appinfo.vdf（{appInfoPath}）";
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
                    LastError = "appinfo.vdf 未解析出任何游戏条目";
                }

                return result;
            }
            catch (Exception ex)
            {
                LastError = $"加载游戏列表失败：{ex}";
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
            LastError = "尚未连接 Steam";
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
                LastError = "未能加载成就定义（缺少 schema 文件）";
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
            LastError = $"加载成就失败:{ex.Message}";
            return null;
        }
    }

    /// <summary>worker 进程内同步应用修改并写回 Steam。</summary>
    public WorkerSaveResult SaveWorkerData(uint appId, WorkerSaveRequest request)
    {
        var result = new WorkerSaveResult();
        if (_client == null)
        {
            result.Message = "尚未连接 Steam";
            return result;
        }

        try
        {
            if (WaitForStatsReady(out _) == false)
            {
                result.Message = LastError ?? "无法获取该游戏统计数据";
                return result;
            }

            foreach (var change in request.Achievements)
            {
                if (_client.SteamUserStats.SetAchievement(change.Id, change.Achieved) == false)
                {
                    result.Message = $"设置成就「{change.Id}」失败";
                    return result;
                }
            }

            if (_client.SteamUserStats.StoreStats() == false)
            {
                result.Message = "保存到 Steam 失败（StoreStats 失败）";
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
                                achievements.Add(new AchievementDefinition
                                {
                                    Id = id,
                                    Name = GetLocalizedString(bit["display"]["name"], language, id),
                                    Description = GetLocalizedString(bit["display"]["desc"], language, ""),
                                    IconNormal = bit["display"]["icon"].AsString(""),
                                    IconLocked = bit["display"]["icon_gray"].AsString(""),
                                    IsHidden = bit["display"]["hidden"].AsBoolean(false),
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
