using System.Globalization;
using System.Text.RegularExpressions;
using System.IO;

namespace SteamLuaManager.Services;

public record ManifestRequest(int AppId, int DepotId, string ManifestId);

public class ManifestMonitorService
{
    // content_log 里 Steam 请求 manifest 的行，App/Depot/Manifest 一次齐全
    private static readonly Regex RequestRegex = new(
        @"BYldRequestDepotManifest\(App:\s*(\d+),\s*Depot:\s*(\d+),\s*Manifest:\s*(\d+)",
        RegexOptions.Compiled);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private const int MaxConcurrency = 3;
    private const int MaxSeen = 10000;
    // 请求间隔：Steam 一次触发一批 depot，无间隔 3 并发全打出去必吃 429；
    // 强制错开请求起始时间，并发槽照拿，只是起跑错峰
    private static readonly TimeSpan RequestPacing = TimeSpan.FromMilliseconds(800);
    private readonly object _paceLock = new();
    private DateTime _lastRequestStartUtc = DateTime.MinValue;

    private readonly IManifestFetcher _fetcher;
    private readonly ISteamPathService _steamPathService;
    private readonly SemaphoreSlim _gate = new(MaxConcurrency, MaxConcurrency);
    private readonly HashSet<(int Depot, string Manifest)> _seen = new();
    private readonly object _seenLock = new();
    // 同一 App 的在途批次：在途归零即落定（Steam 更新开始时一次性要全，不存在细水长流）
    private readonly Dictionary<int, AppBatch> _batches = new();
    private readonly object _batchLock = new();
    // 完成提示去抖（全局）：任意 App 落定一波后都只预约提示，5s 内 Steam
    // 又要新清单（任意 App）就续波不提示；彻底安静满 5s 才汇总输出一条
    // "已齐请重试"，避免多波/多 App 下载时反复打扰用户
    private static readonly TimeSpan CompletionQuietWindow = TimeSpan.FromSeconds(5);
    private GlobalCompletion? _pendingCompletion;

    private sealed class GlobalCompletion
    {
        public CancellationTokenSource? Cts;
        public readonly Dictionary<int, (int Done, int Failed)> PerApp = new();
    }

    private sealed class AppBatch
    {
        public int Active;
        public int Done;
        public int Failed;
    }
    private CancellationTokenSource? _cts;
    private bool _keyInvalidLatched;
    private bool _noConnHinted;

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    public event Action<string>? Log;
    public event Action<ManifestRequest, bool>? RequestCompleted;

    public ManifestMonitorService(IManifestFetcher fetcher, ISteamPathService steamPathService)
    {
        _fetcher = fetcher;
        _steamPathService = steamPathService;
    }

    public Task<(bool Ok, string? Error)> StartAsync()
    {
        if (IsRunning) return Task.FromResult((true, (string?)null));
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            return Task.FromResult((false, (string?)"未检测到 Steam 路径"));
        var logPath = Path.Combine(steamPath, "logs", "content_log.txt");
        if (!File.Exists(logPath))
            return Task.FromResult((false, (string?)"找不到 content_log.txt，请先启动 Steam（若仍没有，在 Steam 快捷方式加上 -dev -console 后重启）"));

        _keyInvalidLatched = false;
        _noConnHinted = false;
        lock (_seenLock) { _seen.Clear(); }
        lock (_batchLock) { _batches.Clear(); }
        _lastRequestStartUtc = DateTime.MinValue;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => WatchLoop(logPath, ct), ct);
        return Task.FromResult((true, (string?)null));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        lock (_batchLock)
        {
            _batches.Clear();
            // 主动停止后不再补"已齐"提示，避免关页面后又弹一条造成困惑
            CancelPendingCompletionLocked();
        }
    }

    private void CancelPendingCompletionLocked()
    {
        if (_pendingCompletion == null) return;
        try { _pendingCompletion.Cts?.Cancel(); } catch { }
        try { _pendingCompletion.Cts?.Dispose(); } catch { }
        _pendingCompletion = null;
    }

    private void Emit(string message)
    {
        try { Log?.Invoke(message); } catch { }
    }

    // 后台线程里同步 tail：文件只增不改，读到 EOF 就等一轮；文件被轮转（变小）就重开
    private void WatchLoop(string logPath, CancellationToken ct)
    {
        FileStream? fs = null;
        StreamReader? reader = null;
        try
        {
            (fs, reader) = OpenTail(logPath);
            Emit("开始实时监控 Steam 下载请求...");
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = reader.ReadLine(); }
                catch { break; }
                if (line == null)
                {
                    if (ct.WaitHandle.WaitOne(PollInterval)) break;
                    try
                    {
                        if (fs.Length < fs.Position)
                        {
                            reader.Dispose();
                            (fs, reader) = OpenTail(logPath);
                            fs.Seek(0, SeekOrigin.Begin);
                            Emit("日志文件已轮转，从头继续监控");
                        }
                    }
                    catch { break; }
                    continue;
                }
                var m = RequestRegex.Match(line);
                if (!m.Success)
                {
                    // Steam 连不上内容服务器是它自己的网络问题，跟清单无关；
                    // 提示一次即可，避免刷屏
                    if (!_noConnHinted && line.Contains("No connection to content servers", StringComparison.Ordinal))
                    {
                        _noConnHinted = true;
                        Emit("检测到 Steam 无法连接内容服务器：已获取的清单不受影响，恢复网络后 Steam 会自动继续，可检查代理/VPN");
                    }
                    continue;
                }
                // 组1=App，组2=Depot，组3=Manifest（gid 超过 int 范围，必须保持字符串）
                if (!int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var appId) ||
                    !int.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out var depotId))
                {
                    Emit($"忽略无法解析的清单请求行: {line.Trim()}");
                    continue;
                }
                var req = new ManifestRequest(appId, depotId, m.Groups[3].Value);
                bool isNew;
                lock (_seenLock)
                {
                    if (_seen.Count >= MaxSeen) _seen.Clear();
                    isNew = _seen.Add((req.DepotId, req.ManifestId));
                }
                if (!isNew) continue;
                if (TrackRequest(req.AppId))
                    Emit($"App {req.AppId} 开始获取清单，请等待全部下载完成后再重试游戏下载");
                Emit($"发现新清单: App {req.AppId} | Depot {req.DepotId} | Manifest {req.ManifestId}");
                _ = HandleAsync(req, ct);
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Emit($"监控异常停止: {ex.Message}");
        }
        finally
        {
            try { reader?.Dispose(); } catch { }
            if (!ct.IsCancellationRequested)
                Emit("监控已停止");
        }
    }

    private static (FileStream, StreamReader) OpenTail(string logPath)
    {
        var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(0, SeekOrigin.End);
        return (fs, new StreamReader(fs));
    }

    private async Task HandleAsync(ManifestRequest req, CancellationToken ct)
    {
        lock (_seenLock)
        {
            // Key 已确认失效就不再占下载槽，本轮直接跳过（计数照样落定，避免批次永远等它）
            if (_keyInvalidLatched) { BatchProgress(req, null); return; }
        }
        var gated = false;
        bool? ok = null;
        try
        {
            await _gate.WaitAsync(ct);
            gated = true;
            await PaceAsync(ct);
            var r = await _fetcher.FetchAsync(req.DepotId, req.ManifestId, ct, Emit);
            if (r.KeyInvalid)
            {
                lock (_seenLock)
                {
                    if (!_keyInvalidLatched)
                    {
                        _keyInvalidLatched = true;
                        Emit("API Key 无效或已过期，后续请求将跳过，请重新获取并重启监听");
                    }
                }
            }
            ok = r.Success;
            Emit(r.Success
                ? $"下载成功: Depot {req.DepotId} ({req.ManifestId})"
                : $"下载失败: Depot {req.DepotId} ({r.Error})");
            try { RequestCompleted?.Invoke(req, r.Success); } catch { }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ok = false;
            Emit($"下载异常: Depot {req.DepotId} ({ex.Message})");
        }
        finally
        {
            if (gated) { try { _gate.Release(); } catch { } }
            BatchProgress(req, ok);
        }
    }

    // 起跑错峰：上一个请求未满间隔就等一等；预占下一起点，后到的顺延排队
    private async Task PaceAsync(CancellationToken ct)
    {
        TimeSpan wait;
        var now = DateTime.UtcNow;
        lock (_paceLock)
        {
            var elapsed = now - _lastRequestStartUtc;
            if (elapsed >= RequestPacing)
            {
                wait = TimeSpan.Zero;
                _lastRequestStartUtc = now;
            }
            else
            {
                wait = RequestPacing - elapsed;
                _lastRequestStartUtc = now + wait;
            }
        }
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);
    }

    // 新请求记入所属 App 批次；返回 true 表示该 App 新开一波（提示用户等待）。
    // 若有待触发的全局完成提示，取消它（续波不提示），累计计数保留。
    private bool TrackRequest(int appId)
    {
        lock (_batchLock)
        {
            if (_pendingCompletion != null)
            {
                try { _pendingCompletion.Cts?.Cancel(); } catch { }
                try { _pendingCompletion.Cts?.Dispose(); } catch { }
                _pendingCompletion.Cts = null;
            }
            if (!_batches.TryGetValue(appId, out var batch))
            {
                batch = new AppBatch();
                _batches[appId] = batch;
                batch.Active = 1;
                return true;
            }
            batch.Active++;
            return false;
        }
    }

    // 单个请求落定：成功/失败计数，跳过只减在途；在途归零本波即结束
    private void BatchProgress(ManifestRequest req, bool? ok)
    {
        string? progressMessage = null;
        (int AppId, int Done, int Failed)? finished = null;
        lock (_batchLock)
        {
            if (!_batches.TryGetValue(req.AppId, out var batch)) return;
            batch.Active--;
            if (ok == true) batch.Done++;
            else if (ok == false) batch.Failed++;
            if (batch.Active > 0)
            {
                progressMessage = $"App {req.AppId} 已获取 {batch.Done} 个清单，还剩 {batch.Active} 个…";
            }
            else
            {
                _batches.Remove(req.AppId);
                if (batch.Done > 0 || batch.Failed > 0)
                    finished = (req.AppId, batch.Done, batch.Failed);
            }
        }
        if (progressMessage != null) Emit(progressMessage);
        if (finished is (int appId, int done, int failed))
            FinishBatch(appId, done, failed);
    }

    private void FinishBatch(int appId, int done, int failed)
    {
        // 一波落定不立即提示：并入全局累计并预约 5s 后汇总输出；
        // 期间任意 App 来新请求都会被 TrackRequest 取消续波
        CancellationTokenSource cts;
        lock (_batchLock)
        {
            _pendingCompletion ??= new GlobalCompletion();
            var pending = _pendingCompletion;
            if (pending.PerApp.TryGetValue(appId, out var cur))
                pending.PerApp[appId] = (cur.Done + done, cur.Failed + failed);
            else
                pending.PerApp[appId] = (done, failed);
            try { pending.Cts?.Cancel(); } catch { }
            try { pending.Cts?.Dispose(); } catch { }
            pending.Cts = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(CompletionQuietWindow, cts.Token);
            }
            catch (OperationCanceledException) { return; }
            Dictionary<int, (int Done, int Failed)> snapshot;
            lock (_batchLock)
            {
                if (_pendingCompletion == null || !ReferenceEquals(_pendingCompletion.Cts, cts))
                    return;
                snapshot = new Dictionary<int, (int Done, int Failed)>(_pendingCompletion.PerApp);
                _pendingCompletion = null;
            }
            try { cts.Dispose(); } catch { }
            EmitCompletionSummary(snapshot);
        });
    }

    // 全局只提示一次：单个 App 沿用老文案；多个 App 先分行列各家结果，最后统一一句请重试
    private void EmitCompletionSummary(Dictionary<int, (int Done, int Failed)> perApp)
    {
        if (perApp.Count == 1)
        {
            foreach (var (appId, (done, failed)) in perApp)
            {
                if (failed == 0)
                    Emit($"App {appId} 清单已齐（共 {done} 个），请在 Steam 中重试下载游戏");
                else
                    Emit($"App {appId} 清单获取结束（成功 {done} / 失败 {failed}），失败的可重新触发 Steam 下载后再试");
            }
            return;
        }
        int totalDone = 0, totalFailed = 0;
        foreach (var (appId, (done, failed)) in perApp)
        {
            totalDone += done;
            totalFailed += failed;
            Emit(failed == 0
                ? $"App {appId} 清单已齐（共 {done} 个）"
                : $"App {appId} 清单获取结束（成功 {done} / 失败 {failed}）");
        }
        Emit(totalFailed == 0
            ? $"共 {perApp.Count} 个 App、{totalDone} 个清单已齐，请在 Steam 中重试下载游戏"
            : $"共 {perApp.Count} 个 App（成功 {totalDone} / 失败 {totalFailed}），失败的可重新触发 Steam 下载后再试");
    }
}

// Hub 监听：用 ManifestHub 抓取
public sealed class HubManifestMonitorService : ManifestMonitorService
{
    public HubManifestMonitorService(HubManifestFetcher fetcher, ISteamPathService steamPathService)
        : base(fetcher, steamPathService) { }
}

// Code 监听：取码 + 官方 CDN 下载
public sealed class CodeManifestMonitorService : ManifestMonitorService
{
    public CodeManifestMonitorService(CodeCdnManifestFetcher fetcher, ISteamPathService steamPathService)
        : base(fetcher, steamPathService) { }
}
