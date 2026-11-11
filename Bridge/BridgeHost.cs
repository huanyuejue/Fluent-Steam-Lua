using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SteamLuaManager.Services;

namespace FluentLuaBridge;

/// <summary>
/// 本地回环 HTTP 宿主。手写极简 HTTP 而不走 HttpListener：
/// HttpListener 依赖 HTTP.SYS，在非系统盘符下会拿到无效句柄而无法绑定，
/// 换成 TcpListener 后与盘符、URLACL、管理员权限都无关。
/// 只实现本场景用到的 GET/POST 与固定头部，不追求通用性。
/// </summary>
internal sealed class BridgeHost : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private ServiceProvider? _services;
    private ImportRunner? _runner;

    /// <summary>进行中的入库任务，key 为任务编号。任务结束且窗口关闭后即移除。</summary>
    private readonly ConcurrentDictionary<string, ImportJob> _jobs = new();

    // 留存上限：正常情况下窗口关闭就会清掉，这里只是异常时的兜底
    private const int MaxRetainedJobs = 4;

    public Task<bool> StartAsync()
    {
        _services = BridgeServices.Build();
        _runner = _services.GetRequiredService<ImportRunner>();

        _cts = new CancellationTokenSource();

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, BridgeConstants.Port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            BridgeDiagnostics.ReportStartupFailure(ex);
            return Task.FromResult(false);
        }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        BridgeLog.Write($"桥接服务已启动，监听 127.0.0.1:{BridgeConstants.Port}");
        return Task.FromResult(true);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, ct), CancellationToken.None);
        }
    }

    /// <summary>处理单条连接：读请求头与正文，路由到对应处理器，写回响应后断开。</summary>
    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            using var stream = client.GetStream();
            var request = await HttpParser.ReadRequestAsync(stream, ct);
            if (request == null) return;

            var (status, body) = await RouteAsync(request, ct);

            var payload = Encoding.UTF8.GetBytes(body);
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(ReasonPhrase(status)).Append("\r\n");
            head.Append("Content-Type: application/json; charset=utf-8\r\n");
            head.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            head.Append("Connection: close\r\n");
            HttpParser.AppendCors(head, request.Origin);
            head.Append("\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            await stream.WriteAsync(headBytes, ct);
            if (request.Method != "HEAD")
            {
                await stream.WriteAsync(payload, ct);
            }
            await stream.FlushAsync(ct);
        }
        catch
        {
            // 连接被对端提前关闭或请求格式非法，丢弃即可
        }
    }

    private async Task<(int Status, string Body)> RouteAsync(HttpRequest request, CancellationToken ct)
    {
        var path = request.Path.TrimEnd('/');
        if (string.IsNullOrEmpty(path)) path = "/";

        if (request.Method == "OPTIONS")
        {
            return (204, string.Empty);
        }

        switch (path)
        {
            case "/api/health":
                return (200, JsonSerializer.Serialize(
                    new { ok = true, version = BridgeInfo.Version }, BridgeJson.Options));

            case "/api/import":
                return await HandleImportAsync(request);

            default:
                if (path.StartsWith("/api/progress/", StringComparison.Ordinal))
                {
                    return HandleProgress(path.Substring("/api/progress/".Length));
                }
                return (404, JsonSerializer.Serialize(new { ok = false, error = "not_found" }, BridgeJson.Options));
        }
    }

    private Task<(int, string)> HandleImportAsync(HttpRequest request)
    {
        if (request.Method != "POST")
        {
            return Task.FromResult((405, JsonSerializer.Serialize(
                new { ok = false, error = "method_not_allowed" }, BridgeJson.Options)));
        }

        ImportRequest? req = null;
        try
        {
            req = JsonSerializer.Deserialize<ImportRequest>(request.Body, BridgeJson.Options);
        }
        catch
        {
            // 落到下面的参数校验
        }

        if (req == null || req.AppId <= 0)
        {
            BridgeLog.Write($"收到非法入库请求，原始正文: {request.Body}");
            return Task.FromResult((400, JsonSerializer.Serialize(
                new { ok = false, error = "invalid_appid" }, BridgeJson.Options)));
        }

        // 同一 AppID 正在跑就不重复开窗，把已有任务号返回给扩展
        foreach (var pair in _jobs)
        {
            if (!pair.Value.IsFinished && pair.Value.AppId == req.AppId)
            {
                return Task.FromResult((200, JsonSerializer.Serialize(
                    new { ok = true, jobId = pair.Key, duplicate = true }, BridgeJson.Options)));
            }
        }

        var job = new ImportJob(req.AppId, req.Name ?? string.Empty);
        _jobs[job.Id] = job;

        // 窗口属于 UI 线程，这里从网络线程切过去创建
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return Task.FromResult((500, JsonSerializer.Serialize(
                new { ok = false, error = "no_ui_thread" }, BridgeJson.Options)));
        }

        dispatcher.Invoke(() =>
        {
            var win = new ImportProgressWindow(job);
            // 窗口关闭即释放任务：常驻进程要连续接收多次入库，
            // 旧任务连同步骤集合必须及时脱钩，否则内存会随次数持续上涨
            win.Closed += (_, _) => EvictJob(job.Id);
            win.Show();
            _ = RunJobAsync(job, win, win.Dispatcher);
        });

        return Task.FromResult((200, JsonSerializer.Serialize(
            new { ok = true, jobId = job.Id }, BridgeJson.Options)));
    }

    /// <summary>
    /// 移除已结束的任务记录。窗口关掉后进度接口不再需要这个任务，
    /// 留着只会让步骤集合与订阅一起常驻内存。
    /// </summary>
    private void EvictJob(string jobId)
    {
        _jobs.TryRemove(jobId, out _);

        // 窗口关闭后才真正释放：此时不会再有入库在跑，
        // 放掉密钥字典（数万条）并回收，空闲占用能降到很低
        // 密钥字典是这台常驻进程最大的内存项，且被单例强引用，
        // 不先放掉引用，后续 GC 等于白做
        try
        {
            if (_services?.GetService(typeof(ISteamDepotService)) is ISteamDepotService depot)
            {
                depot.ReleaseKeyCache();
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Write("释放密钥缓存失败: " + ex.Message);
        }

        MemoryTrimmer.Trim("窗口关闭");

        // 兜底：异常情况下窗口没关（比如进程假死），仍然限制留存数量
        if (_jobs.Count > MaxRetainedJobs)
        {
            foreach (var pair in _jobs)
            {
                if (_jobs.Count <= MaxRetainedJobs) break;
                if (pair.Value.IsFinished) _jobs.TryRemove(pair.Key, out _);
            }
        }
    }

    private async Task RunJobAsync(ImportJob job, ImportProgressWindow win, Dispatcher dispatcher)
    {
        try
        {
            await _runner!.RunAsync(job, CancellationToken.None);
        }
        catch (Exception ex)
        {
            job.IsFinished = true;
            job.IsSuccess = false;
            job.ErrorMessage = ex.Message;
        }
        finally
        {
            dispatcher.Invoke(() => win.Finish());
        }
    }

    private (int, string) HandleProgress(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return (404, JsonSerializer.Serialize(new { ok = false, error = "job_not_found" }, BridgeJson.Options));
        }

        var steps = new List<object>();
        foreach (var s in job.Steps)
        {
            steps.Add(new
            {
                title = s.Title,
                detail = s.Detail,
                state = s.State.ToString().ToLowerInvariant()
            });
        }

        return (200, JsonSerializer.Serialize(new
        {
            ok = true,
            jobId = job.Id,
            appId = job.AppId,
            name = job.DisplayName,
            finished = job.IsFinished,
            success = job.IsSuccess,
            error = job.ErrorMessage,
            steps
        }, BridgeJson.Options));
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "Internal Server Error"
    };

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _services?.Dispose(); } catch { }
        _listener = null;
    }

    public void Dispose() => Stop();
}

internal sealed class ImportRequest
{
    public int AppId { get; set; }
    public string? Name { get; set; }
}

internal static class BridgeInfo
{
    public static string Version =>
        typeof(BridgeInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

internal static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}

internal static class BridgeDiagnostics
{
    public static void ReportStartupFailure(Exception ex)
    {
        var detail = $"[{ex.GetType().FullName}] {ex.Message}";
        var message = $"桥接服务启动失败：端口 {BridgeConstants.Port} 可能已被占用。\n{detail}";
        try
        {
            MessageBox.Show(message, "Fluent Steam Lua", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 无交互会话时忽略
        }
    }
}
