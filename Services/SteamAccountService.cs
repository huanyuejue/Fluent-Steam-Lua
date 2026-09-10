using System.Text;
using System.Text.Json;
using System.IO;
using SteamKit2;
using SteamKit2.Authentication;
using CdnClient = SteamKit2.CDN.Client;
using CdnServer = SteamKit2.CDN.Server;

namespace SteamLuaManager.Services;

// 账号会话服务：自己持有一个 SteamClient 连接（和 SteamAppInfoService 的匿名会话隔离，互不顶登录态）。
// 登录三路：本机记住的凭证（local.vdf DPAPI 解密）> refresh token > 账号密码+2FA。
public class SteamAccountService : ISteamAccountService
{
    private const int ConnectTimeoutSeconds = 30;
    private const int LogonTimeoutSeconds = 30;
    private const string TokenEntropyPrefix = "FluentSteamLua:";

    private readonly ISettingsService _settingsService;
    private readonly ISteamPathService _steamPathService;
    private readonly SteamClient _client;
    private readonly CallbackManager _manager;
    private readonly SteamUser _user;
    private readonly SteamApps _apps;
    private readonly SteamContent _content;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _callbackLoop;
    private CancellationTokenSource? _loopCts;
    private TaskCompletionSource<bool>? _connectedTcs;
    private TaskCompletionSource<bool>? _nextLogonTcs;
    private EResult? _lastLogonResult;
    private bool _connected;
    private bool _disposed;

    private IReadOnlyCollection<CdnServer>? _cdnServers;

    public bool IsLoggedOn { get; private set; }
    public string? CurrentAccountName { get; private set; }

    public event Action? SessionChanged;

    /// <summary>缓存的账号名。显示层结合活会话一起决定"已登录"状态。</summary>
    public string? SavedAccountName
    {
        get
        {
            try
            {
                var name = _settingsService.Load().SavedAccountName;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch { return null; }
        }
    }

    public bool HasCachedToken()
    {
        try
        {
            var settings = _settingsService.Load();
            return !string.IsNullOrWhiteSpace(settings.SavedAccountName)
                && !string.IsNullOrWhiteSpace(settings.EncryptedRefreshToken);
        }
        catch { return false; }
    }

    /// <summary>用缓存凭证静默恢复会话。只在用户主动操作（点提取）时调用，启动时不调。</summary>
    public async Task<SteamLoginResult> TryRestoreSessionAsync(CancellationToken ct = default)
    {
        string username;
        string refreshToken;
        try
        {
            var settings = _settingsService.Load();
            username = settings.SavedAccountName;
            var encrypted = settings.EncryptedRefreshToken;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(encrypted))
                return new SteamLoginResult(false, "无缓存登录凭证");
            var decrypted = SecureTokenStorage.Unprotect(encrypted, TokenEntropyPrefix + username);
            if (string.IsNullOrEmpty(decrypted))
                return new SteamLoginResult(false, "缓存凭证已损坏，请重新登录");
            refreshToken = decrypted;
        }
        catch (Exception ex)
        {
            return new SteamLoginResult(false, $"读取缓存凭证失败：{ex.Message}");
        }
        return await LoginWithRefreshTokenAsync(username, refreshToken, ct).ConfigureAwait(false);
    }

    public SteamAccountService(ISettingsService settingsService, ISteamPathService steamPathService)
    {
        _settingsService = settingsService;
        _steamPathService = steamPathService;
        _client = new SteamClient();
        _manager = new CallbackManager(_client);
        _user = _client.GetHandler<SteamUser>()!;
        _apps = _client.GetHandler<SteamApps>()!;
        _content = _client.GetHandler<SteamContent>()!;

        _manager.Subscribe<SteamClient.ConnectedCallback>(_ =>
        {
            _connected = true;
            _connectedTcs?.TrySetResult(true);
        });
        _manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            _connected = false;
            IsLoggedOn = false;
            CurrentAccountName = null;
            _connectedTcs?.TrySetResult(false);
            _nextLogonTcs?.TrySetResult(false);
            RaiseSessionChanged();
        });
        _manager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
        {
            var ok = cb.Result == EResult.OK;
            IsLoggedOn = ok;
            _lastLogonResult = cb.Result;
            _nextLogonTcs?.TrySetResult(ok);
            RaiseSessionChanged();
        });
    }

    private void RaiseSessionChanged()
    {
        try { SessionChanged?.Invoke(); }
        catch (Exception ex) { LogService.Warn("账号", $"会话状态通知异常: {ex.Message}"); }
    }

    public IReadOnlyList<LocalSteamAccount> ListLocalUsers()
    {
        var list = new List<LocalSteamAccount>();
        try
        {
            var steamDir = DetectSteamDir();
            if (steamDir == null) return list;
            var path = Path.Combine(steamDir, "config", "loginusers.vdf");
            if (!File.Exists(path)) return list;
            var root = VdfParser.Parse(File.ReadAllText(path));
            if (!root.TryGetValue("users", out var usersObj) || usersObj is not VdfParser.VdfDict users) return list;
            foreach (var (steamId, uo) in users)
            {
                if (uo is not VdfParser.VdfDict u) continue;
                u.TryGetValue("AccountName", out var an);
                var account = an as string ?? "";
                if (string.IsNullOrEmpty(account)) continue;
                u.TryGetValue("PersonaName", out var pn);
                u.TryGetValue("Timestamp", out var ts);
                long.TryParse(ts as string, out var timestamp);
                list.Add(new LocalSteamAccount(steamId, account, pn as string ?? account, timestamp));
            }
        }
        catch (Exception ex) { LogService.Warn("账号", $"读取本机账号列表失败: {ex.Message}"); }
        return list.OrderByDescending(u => u.Timestamp).ToList();
    }

    public string? GetAvatarPath(string steamId)
    {
        try
        {
            var steamDir = _steamPathService.DetectSteamPath();
            if (string.IsNullOrEmpty(steamDir)) return null;
            var path = Path.Combine(steamDir, "config", "avatarcache", $"{steamId}.png");
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    public async Task<SteamLoginResult> LoginWithLocalAsync(string accountName, CancellationToken ct = default)
    {
        var token = ExtractLocalToken(accountName);
        if (string.IsNullOrEmpty(token))
            return new SteamLoginResult(false, $"本机无账号 {accountName} 的记住凭证（请在 Steam 客户端登录一次并勾选记住我）");
        var result = await LoginWithRefreshTokenAsync(accountName, token, ct);
        if (result.Success)
            LogService.Info("账号", $"本机凭证登录成功：{accountName}");
        return result;
    }

    public async Task<SteamLoginResult> LoginWithRefreshTokenAsync(string username, string refreshToken, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsLoggedOn && string.Equals(CurrentAccountName, username, StringComparison.OrdinalIgnoreCase))
                return new SteamLoginResult(true, "已登录");

            var (ok, refused) = await AttemptLogonAsync(username, refreshToken, ct).ConfigureAwait(false);
            if (!ok && refused == null)
            {
                // 超时/断开而非明确拒绝：冷连接偶发无响应，重建连接重试一次
                LogService.Info("账号", "首次登录无响应，重建连接后重试...");
                Reconnect();
                try { await Task.Delay(1500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new SteamLoginResult(false, "登录已取消"); }
                (ok, refused) = await AttemptLogonAsync(username, refreshToken, ct).ConfigureAwait(false);
            }
            if (!ok)
            {
                IsLoggedOn = false;
                CurrentAccountName = null;
                return new SteamLoginResult(false, DescribeRefusal(refused));
            }
            await TryRenewTokenAsync(username, refreshToken).ConfigureAwait(false);
            SaveToken(username, refreshToken, guardData: null);
            return new SteamLoginResult(true, $"登录成功：{username}");
        }
        catch (OperationCanceledException) { return new SteamLoginResult(false, "登录已取消"); }
        catch (Exception ex)
        {
            LogService.Warn("账号", $"token 登录异常: {ex.Message}");
            return new SteamLoginResult(false, $"登录异常：{ex.Message}");
        }
        finally { _gate.Release(); }
    }

    // 单次 token 登录尝试：返回（是否成功，明确拒绝时的 EResult；超时/断开则为 null）
    private async Task<(bool Ok, EResult? Refused)> AttemptLogonAsync(string username, string token, CancellationToken ct)
    {
        if (!await EnsureConnectedAsync(ct).ConfigureAwait(false))
            return (false, null);
        if (IsLoggedOn) LogOffInternal();
        CurrentAccountName = username;
        _lastLogonResult = null;
        var waiter = WaitNextLogon();
        _user.LogOn(new SteamUser.LogOnDetails
        {
            Username = username,
            AccessToken = token,
            ShouldRememberPassword = true,
        });
        var ok = await WaitAsync(waiter, LogonTimeoutSeconds, ct).ConfigureAwait(false);
        return ok ? (true, null) : (false, _lastLogonResult);
    }

    private static string DescribeRefusal(EResult? refused) => refused switch
    {
        EResult.InvalidPassword or EResult.Expired or EResult.Revoked or EResult.AccountNotFound
            => "token 无效或已过期，请在 Steam 客户端重新登录一次后重试",
        EResult e => $"登录被拒绝：{e}",
        null => "登录超时或连接不稳定，请重试",
    };

    private void Reconnect()
    {
        try { _client.Disconnect(); } catch { }
        _connected = false;
    }

    public async Task<SteamLoginResult> LoginWithCredentialsAsync(string username, string password, IAuthenticator authenticator, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureConnectedAsync(ct).ConfigureAwait(false))
                return new SteamLoginResult(false, "连接 Steam 服务器失败");

            string? guardData = LoadGuardData(username);
            AuthSession session;
            try
            {
                session = await _client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = true,
                    GuardData = guardData,
                    Authenticator = authenticator,
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new SteamLoginResult(false, $"认证发起失败：{ex.Message}");
            }

            AuthPollResult poll;
            try
            {
                poll = await session.PollingWaitForResultAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return new SteamLoginResult(false, "登录已取消"); }
            catch (Exception ex)
            {
                return new SteamLoginResult(false, $"认证失败：{ex.Message}");
            }

            var (ok, refused) = await AttemptLogonAsync(poll.AccountName, poll.RefreshToken, ct).ConfigureAwait(false);
            if (!ok && refused == null)
            {
                LogService.Info("账号", "登录无响应，重建连接后重试...");
                Reconnect();
                try { await Task.Delay(1500, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return new SteamLoginResult(false, "登录已取消"); }
                (ok, refused) = await AttemptLogonAsync(poll.AccountName, poll.RefreshToken, ct).ConfigureAwait(false);
            }
            if (!ok)
            {
                IsLoggedOn = false;
                CurrentAccountName = null;
                return new SteamLoginResult(false, DescribeRefusal(refused));
            }
            SaveToken(poll.AccountName, poll.RefreshToken, poll.NewGuardData);
            LogService.Info("账号", $"账号密码登录成功：{poll.AccountName}");
            return new SteamLoginResult(true, $"登录成功：{poll.AccountName}");
        }
        catch (OperationCanceledException) { return new SteamLoginResult(false, "登录已取消"); }
        catch (Exception ex)
        {
            LogService.Warn("账号", $"密码登录异常: {ex.Message}");
            return new SteamLoginResult(false, $"登录异常：{ex.Message}");
        }
        finally { _gate.Release(); }
    }

    public async Task<Dictionary<uint, byte[]>> GetDepotKeysAsync(IEnumerable<(uint DepotId, uint AppId)> targets, CancellationToken ct = default)
    {
        var result = new Dictionary<uint, byte[]>();
        if (!IsLoggedOn) throw new InvalidOperationException("账号未登录，无法获取 depot 密钥");
        // 单连接多 AsyncJob 并行下发，限流 6（串行 60+ 个 depot 太慢，见 Titanfall 2 这类 DLC 大户）
        var list = targets.Distinct().ToList();
        using var throttle = new SemaphoreSlim(6);
        var tasks = list.Select(async t => await FetchOneKeyAsync(t.DepotId, t.AppId, throttle, ct).ConfigureAwait(false)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var (id, key, error) in results)
        {
            if (key != null)
                result[id] = key;
            else
                LogService.Info("账号", $"depot {id} 密钥获取失败 ({error})，可能无该内容权限");
        }
        return result;
    }

    private async Task<(uint Id, byte[]? Key, string? Error)> FetchOneKeyAsync(uint depotId, uint appId, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var job = _apps.GetDepotDecryptionKey(depotId, appId);
            job.Timeout = TimeSpan.FromSeconds(30);
            var cb = await job.ToTask().ConfigureAwait(false);
            if (cb.Result == EResult.OK && cb.DepotKey is { Length: > 0 })
                return (depotId, cb.DepotKey, null);
            return (depotId, null, cb.Result.ToString());
        }
        catch (Exception ex)
        {
            LogService.Warn("账号", $"depot {depotId} 密钥请求异常: {ex.Message}");
            return (depotId, null, ex.Message);
        }
        finally { gate.Release(); }
    }

    public async Task<ManifestDownloadResult> DownloadManifestAsync(uint appId, uint depotId, ulong manifestGid, byte[]? depotKey, string destDir, CancellationToken ct = default)
    {
        if (!IsLoggedOn) return new ManifestDownloadResult(false, null, "账号未登录");
        if (manifestGid == 0) return new ManifestDownloadResult(false, null, "无 public manifest 版本号");
        try
        {
            var server = await PickCdnServerAsync().ConfigureAwait(false);
            if (server == null) return new ManifestDownloadResult(false, null, "无可用 CDN 服务器");

            var code = await _content.GetManifestRequestCode(depotId, appId, manifestGid, "public", null).ConfigureAwait(false);
            if (code == 0) return new ManifestDownloadResult(false, null, "无权访问该 manifest（账号无对应内容权限）");

            using var cdn = new CdnClient(_client);
            var key = depotKey ?? new byte[32];
            DepotManifest manifest;
            try
            {
                manifest = await cdn.DownloadManifestAsync(depotId, manifestGid, code, server, key, null, null).ConfigureAwait(false);
            }
            catch (SteamKitWebRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                // 403 时申请 CDN 授权 token 重试一次，demo 里验证过这招对公开 depot 有效
                string? cdnToken = null;
                try { cdnToken = (await _content.GetCDNAuthToken(appId, depotId, server.Host ?? "")).Token; }
                catch (Exception ex2) { LogService.Warn("账号", $"depot {depotId} 申请 CDN 授权失败: {ex2.Message}"); }
                manifest = await cdn.DownloadManifestAsync(depotId, manifestGid, code, server, key, null, cdnToken).ConfigureAwait(false);
            }
            if (depotKey != null) manifest.DecryptFilenames(depotKey);

            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
            var savePath = Path.Combine(destDir, $"{depotId}_{manifestGid}.manifest");
            manifest.SaveToFile(savePath);
            return new ManifestDownloadResult(true, savePath, $"已保存（{manifest.Files?.Count ?? 0} 个文件索引，未下载游戏数据）");
        }
        catch (Exception ex)
        {
            LogService.Warn("账号", $"depot {depotId} manifest 下载失败: {ex.Message}");
            return new ManifestDownloadResult(false, null, $"下载失败：{ex.Message}");
        }
    }

    public void LogOff()
    {
        try { _user.LogOff(); } catch { }
        IsLoggedOn = false;
        CurrentAccountName = null;
        // 退出即忘：清掉缓存凭证，否则状态区还会显示已登录
        try
        {
            var settings = _settingsService.Load();
            settings.SavedAccountName = string.Empty;
            settings.EncryptedRefreshToken = string.Empty;
            settings.EncryptedGuardData = string.Empty;
            _settingsService.Save(settings);
        }
        catch (Exception ex) { LogService.Warn("账号", $"清除缓存凭证失败: {ex.Message}"); }
        RaiseSessionChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _loopCts?.Cancel(); } catch { }
        try { _client.Disconnect(); } catch { }
        _gate.Dispose();
        _loopCts?.Dispose();
    }

    // ---- 内部 ----

    private void LogOffInternal()
    {
        try { _user.LogOff(); } catch { }
        IsLoggedOn = false;
        CurrentAccountName = null;
    }

    private Task<bool> WaitNextLogon()
    {
        _nextLogonTcs = new TaskCompletionSource<bool>();
        return _nextLogonTcs.Task;
    }

    private static async Task<bool> WaitAsync(Task<bool> task, int seconds, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try { return await task.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connected && _client.IsConnected) return true;
        if (_callbackLoop == null || _callbackLoop.IsCompleted)
        {
            _loopCts?.Dispose();
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _callbackLoop = Task.Run(() => RunCallbackLoop(token), token);
        }
        _connectedTcs = new TaskCompletionSource<bool>();
        _client.Connect();
        return await WaitAsync(_connectedTcs.Task, ConnectTimeoutSeconds, ct).ConfigureAwait(false) && _connected;
    }

    private void RunCallbackLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogService.Warn("账号", $"Steam 回调循环异常: {ex.Message}");
                try { Task.Delay(100, ct).Wait(ct); } catch { break; }
            }
        }
    }

    private async Task<CdnServer?> PickCdnServerAsync()
    {
        try
        {
            _cdnServers ??= await _content.GetServersForSteamPipe().ConfigureAwait(false);
            return _cdnServers.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LogService.Warn("账号", $"获取 CDN 服务器失败: {ex.Message}");
            return null;
        }
    }

    // token 还有 30 天以上有效期就不折腾；快过期则续期并回写，我抄的 SteamTokenDumper 策略
    private async Task TryRenewTokenAsync(string username, string refreshToken)
    {
        try
        {
            var exp = GetJwtExpiry(refreshToken);
            if (exp == null || DateTime.UtcNow.AddDays(30) < exp) return;
            var steamId = _client.SteamID;
            if (steamId == null) return;
            var renewed = await _client.Authentication
                .GenerateAccessTokenForAppAsync(steamId, refreshToken, allowRenewal: true)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(renewed.RefreshToken))
            {
                SaveToken(username, renewed.RefreshToken, guardData: null);
                LogService.Info("账号", "refresh token 已续期");
            }
        }
        catch (Exception ex) { LogService.Warn("账号", $"token 续期失败（不影响本次使用）: {ex.Message}"); }
    }

    private void SaveToken(string username, string refreshToken, string? guardData)
    {
        try
        {
            var settings = _settingsService.Load();
            settings.SavedAccountName = username;
            settings.EncryptedRefreshToken = SecureTokenStorage.Protect(refreshToken, TokenEntropyPrefix + username) ?? "";
            if (!string.IsNullOrEmpty(guardData))
                settings.EncryptedGuardData = SecureTokenStorage.Protect(guardData, TokenEntropyPrefix + username) ?? "";
            _settingsService.Save(settings);
        }
        catch (Exception ex) { LogService.Warn("账号", $"保存登录凭证失败: {ex.Message}"); }
    }

    private string? LoadGuardData(string username)
    {
        try
        {
            var settings = _settingsService.Load();
            if (string.IsNullOrEmpty(settings.EncryptedGuardData)) return null;
            return SecureTokenStorage.Unprotect(settings.EncryptedGuardData, TokenEntropyPrefix + username);
        }
        catch { return null; }
    }

    /// <summary>解密本机记住的 refresh token：多 blob 轮试，解出 JWT 即返回。</summary>
    private static string? ExtractLocalToken(string accountName)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var path = Path.Combine(localAppData, "Steam", "local.vdf");
            if (!File.Exists(path)) return null;
            var root = VdfParser.Parse(File.ReadAllText(path));
            if (!root.TryGetValue("MachineUserConfigStore", out var o1) || o1 is not VdfParser.VdfDict d1) return null;
            if (!d1.TryGetValue("Software", out var o2) || o2 is not VdfParser.VdfDict d2) return null;
            object? o3 = d2.TryGetValue("Valve", out var v3) ? v3 : d2.TryGetValue("valve", out var v3b) ? v3b : null;
            if (o3 is not VdfParser.VdfDict d3) return null;
            if (!d3.TryGetValue("Steam", out var o4) || o4 is not VdfParser.VdfDict d4) return null;
            if (!d4.TryGetValue("ConnectCache", out var o5) || o5 is not VdfParser.VdfDict cache) return null;

            var entropy = Encoding.UTF8.GetBytes(accountName);
            foreach (var (_, v) in cache)
            {
                if (v is not string hex || hex.Length < 64) continue;
                byte[] enc;
                try { enc = Convert.FromHexString(hex.Trim()); }
                catch { continue; }
                var dec = SecureTokenStorage.UnprotectBytes(enc, entropy);
                if (dec == null) continue;
                var token = Encoding.UTF8.GetString(dec);
                if (token.StartsWith("eyA", StringComparison.Ordinal)) return token;
            }
        }
        catch (Exception ex) { LogService.Warn("账号", $"解密本机凭证失败: {ex.Message}"); }
        return null;
    }

    private static string? DetectSteamDir()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var p = key?.GetValue("SteamPath") as string;
            return string.IsNullOrEmpty(p) ? null : p.Replace('/', Path.DirectorySeparatorChar);
        }
        catch { return null; }
    }

    private static DateTime? GetJwtExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.TryGetProperty("exp", out var exp))
                return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()).UtcDateTime;
        }
        catch { }
        return null;
    }
}
