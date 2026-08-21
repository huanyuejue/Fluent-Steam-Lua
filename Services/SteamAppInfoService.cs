using SteamKit2;

namespace SteamLuaManager.Services;

/// <summary>
/// 通过 SteamKit2 匿名登录 + AppAccessToken 向 Steam 服务器查询完整 appinfo 的兜底实现。
/// 连接与登录为惰性单例，程序生命周期内复用同一条连接，多次查询走同一回调循环。
/// </summary>
public sealed class SteamAppInfoService : ISteamAppInfoService, IDisposable
{
    private readonly Lazy<SteamClient> _client;
    private readonly Lazy<CallbackManager> _manager;
    private readonly Lazy<SteamUser> _user;
    private readonly Lazy<SteamApps> _apps;

    private volatile bool _loggedOn;
    private volatile bool _disconnected;
    private Task? _callbackLoop;
    private CancellationTokenSource? _loopCts;
    private readonly SemaphoreSlim _logonLock = new(1, 1);
    private readonly TaskCompletionSource _firstLogonTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private const double CallbackTimeoutSeconds = 20;

    public SteamAppInfoService()
    {
        _client = new Lazy<SteamClient>(() => new SteamClient(), LazyThreadSafetyMode.ExecutionAndPublication);

        _manager = new Lazy<CallbackManager>(() => new CallbackManager(_client.Value), LazyThreadSafetyMode.ExecutionAndPublication);
        _user = new Lazy<SteamUser>(() => _client.Value.GetHandler<SteamUser>()!, LazyThreadSafetyMode.ExecutionAndPublication);
        _apps = new Lazy<SteamApps>(() => _client.Value.GetHandler<SteamApps>()!, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public void Dispose()
    {
        _disconnected = true;
        try { _loopCts?.Cancel(); } catch { }
        try { _loopCts?.Dispose(); } catch { }
        _loopCts = null;
        try { _callbackLoop?.Wait(500); } catch { }
        _callbackLoop = null;
        if (_client.IsValueCreated)
        {
            try { _client.Value.Disconnect(); } catch { }
        }
        _logonLock.Dispose();
    }

    /// <summary>确保已连接并匿名登录。连接建立后可复用多次查询。</summary>
    private async Task<bool> EnsureLoggedOnAsync(CancellationToken ct = default)
    {
        if (_loggedOn) return true;

        await _logonLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loggedOn) return true;
            if (_disconnected) throw new InvalidOperationException("Steam 连接已断开，无法重连");

            if (_callbackLoop == null || _callbackLoop.IsCompleted)
            {
                AttachLogonHandlers();
                _loopCts?.Dispose();
                _loopCts = new CancellationTokenSource();
                var token = _loopCts.Token;
                _callbackLoop = Task.Run(() => RunCallbackLoop(token), token);
                _client.Value.Connect();
            }

            if (_firstLogonTcs.Task.IsCompleted) return _loggedOn;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CallbackTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await _firstLogonTcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            return _loggedOn;
        }
        catch (OperationCanceledException)
        {
            LogService.Warn("AppInfo", "等待 Steam 登录超时或已取消");
            return false;
        }
        finally
        {
            _logonLock.Release();
        }
    }

    private void RunCallbackLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disconnected)
        {
            try
            {
                _manager.Value.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogService.Warn("AppInfo", $"Steam 回调循环异常: {ex.Message}");
                try { Task.Delay(100, ct).Wait(ct); } catch { break; }
            }
        }
    }

    private void AttachLogonHandlers()
    {
        _manager.Value.Subscribe<SteamClient.ConnectedCallback>(_ => _user.Value.LogOnAnonymous());
        _manager.Value.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        {
            _loggedOn = cb.Result == EResult.OK;
            if (!_firstLogonTcs.Task.IsCompleted)
                _firstLogonTcs.TrySetResult();
        });
        _manager.Value.Subscribe<SteamClient.DisconnectedCallback>(cb =>
        {
            _disconnected = true;
            _loggedOn = false;
            if (!_firstLogonTcs.Task.IsCompleted)
                _firstLogonTcs.TrySetResult();
        });
    }

    public async Task<AppInfoQueryResult?> QueryFullAppInfoAsync(int appId, ulong accessToken, CancellationToken ct = default)
    {
        if (accessToken == 0) return null;
        if (!await EnsureLoggedOnAsync(ct).ConfigureAwait(false)) return null;

        var req = new SteamApps.PICSRequest((uint)appId, accessToken);
        var job = _apps.Value.PICSGetProductInfo(req, null);
        job.Timeout = TimeSpan.FromSeconds(CallbackTimeoutSeconds);
        try
        {
            var rs = await job.ToTask().ConfigureAwait(false);
#pragma warning disable CS8602
            foreach (var cb in rs.Results)
#pragma warning restore CS8602
            {
                if (cb == null) continue;
                if (cb.Apps.TryGetValue((uint)appId, out var info) && info != null)
                {
                    var kv = info.KeyValues;
                    var result = new AppInfoQueryResult();
                    result.AppName = kv["common"]?["name"]?.AsString() ?? $"App {appId}";

                    var depotsNode = kv["depots"];
                    if (depotsNode != null)
                    {
                        LogService.Info("AppInfo", $"AppID {appId} depots 节点子项数: {depotsNode.Children.Count}");
                        foreach (var depot in depotsNode.Children)
                        {
                            var name = depot.Name;
                            if (string.IsNullOrEmpty(name) || !char.IsDigit(name[0])) continue;
                            var depotFromApp = depot["depotfromapp"];
                            if (depotFromApp != null)
                            {
                                var fromAppVal = depotFromApp.AsString();
                                if (!string.IsNullOrEmpty(fromAppVal) && fromAppVal != appId.ToString())
                                    continue; // 跳过指向其他 app 的共享仓库
                            }
                            if (int.TryParse(name, out var id)) result.DepotIds.Add(id);
                        }
                    }
                    else
                    {
                        LogService.Warn("AppInfo", $"AppID {appId} 未找到 depots 节点");
                    }

                    var dlcNode = kv["extended"]?["listofdlc"];
                    if (dlcNode != null)
                    {
                        var dlcText = dlcNode.AsString();
                        if (!string.IsNullOrWhiteSpace(dlcText))
                        {
                            var parts = dlcText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            foreach (var part in parts)
                            {
                                if (int.TryParse(part, out var dlcId))
                                    result.DlcAppIds.Add(dlcId);
                            }
                        }
                    }

                    if (result.DepotIds.Count == 0 && result.DlcAppIds.Count == 0 && string.IsNullOrEmpty(result.AppName))
                        return null;

                    return result;
                }
            }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogService.Warn("AppInfo", $"查询 AppID {appId} 超时");
            return null;
        }
        catch (OperationCanceledException)
        {
            // 调用方主动取消
            return null;
        }
        catch (Exception ex)
        {
            LogService.Warn("AppInfo", $"查询 AppID {appId} 失败: {ex.Message}");
            return null;
        }
    }
}