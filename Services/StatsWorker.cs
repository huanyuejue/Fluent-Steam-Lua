using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SteamLuaManager.Services;

/// <summary>worker 子进程模式：进程启动时设置 SteamAppId 再初始化 steamclient，
/// 避免单进程内 SteamAppId 上下文固定无法切换游戏的问题（SAM 同款架构）。</summary>
public static class StatsWorker
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length >= 4 && args[1] == "serve")
            {
                var appId = uint.Parse(args[2]);
                return RunServe(appId, args[3]);
            }

            File.WriteAllText(GetErrorPath(), JsonSerializer.Serialize(new WorkerSaveResult
            {
                Ok = false,
                Message = "worker 参数错误"
            }));
            return -1;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(GetErrorPath(), JsonSerializer.Serialize(new WorkerSaveResult
                {
                    Ok = false,
                    Message = ex.ToString()
                }));
            }
            catch { }
            return -1;
        }
    }

    private static string GetErrorPath()
    {
        return Path.Combine(Path.GetTempPath(), "steam_lua_worker_error.json");
    }

    /// <summary>常驻模式：通过命名管道与主进程通信（stdin/stdout 会被 steamclient 输出污染，不可用）。
    /// 行协议：主进程发命令（load/save/exit），worker 回 JSON 响应。</summary>
    private static int RunServe(uint appId, string pipeName)
    {
        try
        {
            using var pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            pipe.WaitForConnection();

            using var reader = new StreamReader(pipe, Encoding.UTF8);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

            using var svc = new SteamAchievementService();
            if (!svc.Connect(appId))
            {
                writer.WriteLine(JsonSerializer.Serialize(new WorkerServeResponse
                {
                    Ok = false,
                    Error = svc.LastError ?? "连接 Steam 失败"
                }));
                writer.Flush();
                return 1;
            }

            // 预热 stats 会话：避免主进程首次 load 时 RequestUserStats 尚未就绪而超时
            svc.WarmUpStats();

            writer.WriteLine(JsonSerializer.Serialize(new WorkerServeResponse { Ok = true, Ready = true }));
            writer.Flush();

            while (true)
            {
                var line = reader.ReadLine();
                if (line == null) break;

                WorkerServeResponse response;
                try
                {
                    var request = JsonSerializer.Deserialize<WorkerServeRequest>(line);
                    switch (request?.Cmd)
                    {
                        case "load":
                            var load = svc.LoadWorkerData(appId);
                            response = load == null
                                ? new WorkerServeResponse { Ok = false, Error = svc.LastError ?? "加载失败" }
                                : new WorkerServeResponse { Ok = true, Load = load };
                            break;

                        case "save":
                            if (request.Payload == null)
                            {
                                response = new WorkerServeResponse { Ok = false, Error = "无保存数据" };
                                break;
                            }
                            var save = svc.SaveWorkerData(appId, request.Payload);
                            response = new WorkerServeResponse { Ok = save.Ok, Save = save };
                            break;

                        case "exit":
                            return 0;

                        default:
                            response = new WorkerServeResponse { Ok = false, Error = $"未知命令：{request?.Cmd}" };
                            break;
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    response = new WorkerServeResponse { Ok = false, Error = ex.ToString() };
                }

                writer.WriteLine(JsonSerializer.Serialize(response));
                writer.Flush();
            }

            return 0;
        }
        catch (Exception ex)
        {
            LogService.Error("Worker", $"serve 循环异常退出: {ex.Message}");
            return -1;
        }
    }

}

public sealed class WorkerAchievementData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string IconNormal { get; set; } = "";
    public string IconLocked { get; set; } = "";
    public bool Hidden { get; set; }
    public int Permission { get; set; }
    public bool Achieved { get; set; }
    public long? UnlockTimeUtc { get; set; }
}

public sealed class WorkerLoadResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public uint AppId { get; set; }
    public List<WorkerAchievementData> Achievements { get; set; } = new();
}

public sealed class WorkerSaveRequest
{
    public List<WorkerAchievementChange> Achievements { get; set; } = new();
}

public sealed class WorkerAchievementChange
{
    public string Id { get; set; } = "";
    public bool Achieved { get; set; }
}

public sealed class WorkerSaveResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
}

public sealed class WorkerServeRequest
{
    public string Cmd { get; set; } = "";
    public WorkerSaveRequest? Payload { get; set; }
}

public sealed class WorkerServeResponse
{
    public bool Ok { get; set; }
    public bool Ready { get; set; }
    public string Error { get; set; } = "";
    public WorkerLoadResult? Load { get; set; }
    public WorkerSaveResult? Save { get; set; }
}
