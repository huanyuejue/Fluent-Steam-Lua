using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SteamLuaManager.Services;

/// <summary>
/// 常驻 worker 子进程会话：一个游戏对应一个子进程，进程内 SteamAppId 上下文固定，
/// 窗口打开期间多次加载/保存无需重复启动游戏。通过命名管道 JSON 行通信。
/// </summary>
public sealed class WorkerSession : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>最近一次通信/worker 报告的错误信息。</summary>
    public string? LastError { get; private set; }

    private WorkerSession(Process process, NamedPipeClientStream pipe)
    {
        _process = process;
        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        _reader = new StreamReader(pipe, Encoding.UTF8);
    }

    public static async Task<WorkerSession?> StartAsync(uint appId)
    {
        // 命名管道通信：stdin/stdout 会被 steamclient 的调试输出污染并可能在初始化完成后被关闭
        var pipeName = $"steam_lua_worker_{appId}_{Guid.NewGuid():N}";
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "SteamLuaManager.exe",
            Arguments = $"--worker serve {appId} {pipeName}",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process == null) return null;

            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(60_000);
            var session = new WorkerSession(process, pipe);

            string? readyLine;
            WorkerServeResponse? ready = null;
            do
            {
                readyLine = await Task.Run(() => session._reader.ReadLine())
                    .WaitAsync(TimeSpan.FromSeconds(60));
                if (readyLine == null)
                {
                    session.Dispose();
                    return null;
                }
            } while (!TryParseServeResponse(readyLine, out ready));

            if (ready == null || ready.Ok == false || ready.Ready == false)
            {
                session.Dispose();
                return null;
            }

            return session;
        }
        catch
        {
            process?.Kill();
            return null;
        }
    }

    public async Task<WorkerLoadResult?> LoadAsync()
    {
        var response = await SendAsync(new WorkerServeRequest { Cmd = "load" });
        if (response is { Ok: true, Load: not null }) return response.Load;
        if (response != null)
        {
            LastError = string.IsNullOrEmpty(response.Error) ? "worker 返回失败" : response.Error;
        }
        return null;
    }

    public async Task<WorkerSaveResult?> SaveAsync(WorkerSaveRequest request)
    {
        var response = await SendAsync(new WorkerServeRequest { Cmd = "save", Payload = request });
        if (response == null)
        {
            LastError = "会话无响应";
            return null;
        }
        if (response.Ok == false && string.IsNullOrEmpty(response.Error) == false)
        {
            LastError = response.Error;
        }
        return response.Save;
    }

    private async Task<WorkerServeResponse?> SendAsync(WorkerServeRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || _process.HasExited)
            {
                LastError = "worker 进程已退出";
                return null;
            }

            await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
            await _writer.FlushAsync();

            // 跳过 steamclient 可能输出的非 JSON 调试行
            string? line;
            WorkerServeResponse? response;
            do
            {
                line = await Task.Run(() => _reader.ReadLine())
                    .WaitAsync(TimeSpan.FromSeconds(60));
                if (line == null)
                {
                    LastError = "worker 无响应（stdin 已关闭）";
                    return null;
                }
            } while (!TryParseServeResponse(line, out response));

            return response;
        }
        catch (TimeoutException)
        {
            LastError = "worker 响应超时";
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryParseServeResponse(string line, out WorkerServeResponse? response)
    {
        try
        {
            response = JsonSerializer.Deserialize<WorkerServeResponse>(line);
            return response != null;
        }
        catch (JsonException)
        {
            response = null;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _writer.WriteLine(JsonSerializer.Serialize(new WorkerServeRequest { Cmd = "exit" }));
            _writer.Flush();
            if (!_process.HasExited && !_process.WaitForExit(5000))
            {
                _process.Kill();
            }
        }
        catch
        {
            try { _process.Kill(); } catch { }
        }

        _process.Dispose();
        _gate.Dispose();
        _pipe.Dispose();
    }
}
