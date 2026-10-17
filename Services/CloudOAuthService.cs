using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamLuaManager.Services;

// 复刻上游 companion 的 OAuth 回环登录：浏览器拉起授权、本地 HttpListener 收码、
// 换 token 后按 DLL 约定的 {access_token, refresh_token, expires_at} 落盘，DLL 负责后续自动刷新。
// 应用凭证复用上游公开值（Google 与 DLL 内硬编码同一套，OneDrive 用 rclone 公开 ID），
// 来源见 https://github.com/Selectively11/CloudRedirect/blob/master/ui/Services/OAuthService.cs。
public sealed class CloudOAuthService : IDisposable
{
    public const string ProviderGDrive = "gdrive";
    public const string ProviderOneDrive = "onedrive";

    // 凭证常量对同程序集内开放：远端读取（CloudProviderStore）复用同一套刷新
    internal const string GDriveClientId = "1072944905499-vm2v2i5dvn0a0d2o4ca36i1vge8cvbn0.apps.googleusercontent.com";
    internal const string GDriveClientSecret = "v6V3fKV_zWU7iw1DrpO1rknX";
    internal const string GDriveScope = "https://www.googleapis.com/auth/drive.file";
    internal const string GDriveAuthUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    internal const string GDriveTokenUrl = "https://oauth2.googleapis.com/token";

    internal const string OneDriveClientId = "b15665d9-eda6-4092-8539-0eec376afd59";
    internal const string OneDriveClientSecret = "qtyfaBBYA403=unZUP40~_#";
    internal const string OneDriveScope = "Files.ReadWrite offline_access";
    internal const string OneDriveAuthUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    internal const string OneDriveTokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const int OneDrivePort = 53682;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private HttpListener? _listener;
    private bool _disposed;

    public async Task<bool> AuthorizeAsync(string provider, string tokenPath, Action<string> log, CancellationToken cancel = default)
    {
        if (provider != ProviderGDrive && provider != ProviderOneDrive)
            throw new ArgumentException(L.Get("Svc_OAuthBadProvider", provider), nameof(provider));
        if (string.IsNullOrWhiteSpace(tokenPath))
            throw new ArgumentException(L.Get("Svc_OAuthNoTokenPath"), nameof(tokenPath));

        int port;
        string redirectUri;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _listener = new HttpListener();
        try
        {
            if (provider == ProviderOneDrive)
            {
                // rclone 的 Azure 应用只注册了这个固定端口，换端口必失败
                port = OneDrivePort;
                _listener.Prefixes.Add($"http://localhost:{port}/");
                try { _listener.Start(); }
                catch (HttpListenerException ex)
                {
                    log(L.Get("Svc_OAuthPortBusy", port, ex.Message));
                    return false;
                }
                redirectUri = $"http://localhost:{port}/";
            }
            else
            {
                port = -1;
                redirectUri = "";
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    port = FindAvailablePort();
                    redirectUri = $"http://localhost:{port}/callback";
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://localhost:{port}/callback/");
                    try
                    {
                        _listener.Start();
                        break;
                    }
                    catch (HttpListenerException) when (attempt < 4)
                    {
                        log(L.Get("Svc_OAuthPortRetry", port));
                        _listener.Close();
                        _listener = new HttpListener();
                    }
                }
                if (!_listener.IsListening)
                {
                    log(L.Get("Svc_OAuthListenFailed"));
                    return false;
                }
            }

            var state = GenerateRandomString(32);
            var verifier = GenerateRandomString(64);
            var challenge = ComputeCodeChallenge(verifier);
            var authUrl = provider == ProviderGDrive
                ? $"{GDriveAuthUrl}?client_id={Uri.EscapeDataString(GDriveClientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&response_type=code&scope={Uri.EscapeDataString(GDriveScope)}" +
                  $"&access_type=offline&prompt=consent" +
                  $"&state={Uri.EscapeDataString(state)}" +
                  $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256"
                : $"{OneDriveAuthUrl}?client_id={Uri.EscapeDataString(OneDriveClientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&response_type=code&scope={Uri.EscapeDataString(OneDriveScope)}" +
                  $"&prompt=consent" +
                  $"&state={Uri.EscapeDataString(state)}" +
                  $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=S256";

            log(L.Get("Svc_OAuthOpening"));
            try
            {
                Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                log(L.Get("Svc_OAuthBrowserFailed", authUrl));
                LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Svc_OAuthBrowserLog", ex.Message));
            }

            log(L.Get("Svc_OAuthWaiting"));
            var code = await WaitForCallbackAsync(state, cts.Token);
            if (string.IsNullOrEmpty(code))
            {
                log(L.Get("Svc_OAuthNoCode"));
                return false;
            }

            log(L.Get("Svc_OAuthExchanging"));
            var tokens = provider == ProviderGDrive
                ? await ExchangeCodeAsync(GDriveTokenUrl, GDriveClientId, GDriveClientSecret, GDriveScope, code, redirectUri, verifier, null, cts.Token)
                : await ExchangeCodeAsync(OneDriveTokenUrl, OneDriveClientId, OneDriveClientSecret, OneDriveScope, code, redirectUri, verifier, log, cts.Token);
            if (tokens == null || string.IsNullOrEmpty(tokens.Value.RefreshToken))
            {
                log(L.Get("Svc_OAuthNoRefreshToken"));
                return false;
            }

            var payload = JsonSerializer.Serialize(new
            {
                access_token = tokens.Value.AccessToken,
                refresh_token = tokens.Value.RefreshToken,
                expires_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + tokens.Value.ExpiresIn,
            }, new JsonSerializerOptions { WriteIndented = true });
            if (!CloudCredentialStore.WriteJson(tokenPath, payload))
            {
                log(L.Get("Svc_OAuthTokenSaveFailed"));
                return false;
            }
            log(L.Get("Svc_OAuthSuccess", tokenPath));
            return true;
        }
        catch (OperationCanceledException)
        {
            log(L.Get("Svc_OAuthCancelled"));
            return false;
        }
        catch (Exception ex)
        {
            log(L.Get("Svc_OAuthFail", ex.Message));
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Svc_OAuthExLog", ex.Message));
            return false;
        }
        finally
        {
            StopListener();
        }
    }

    // 检查 token 文件是否含有 refresh_token；只读不解密失败即判未认证
    public static (bool Ok, string Message) CheckTokenStatus(string tokenPath)
    {
        if (string.IsNullOrEmpty(tokenPath) || !File.Exists(tokenPath))
            return (false, L.Get("Svc_OAuthTokenMissing"));
        try
        {
            var json = CloudCredentialStore.ReadJson(tokenPath);
            if (json == null) return (false, L.Get("Svc_OAuthTokenDecryptFailed"));
            using var doc = JsonDocument.Parse(json);
            var ok = doc.RootElement.TryGetProperty("refresh_token", out var rt)
                && rt.GetString()?.Length > 0;
            return ok ? (true, L.Get("Svc_OAuthAuthenticated")) : (false, L.Get("Svc_OAuthTokenNoRefresh"));
        }
        catch (Exception ex)
        {
            return (false, L.Get("Svc_OAuthTokenReadFailed", ex.Message));
        }
    }

    private async Task<string?> WaitForCallbackAsync(string state, CancellationToken cancel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
        while (true)
        {
            // 重入的第二次授权会 Stop/Close 旧监听器，快照后判空按取消返回，不再串台抛 NRE
            var listener = _listener;
            if (listener == null || !listener.IsListening)
                return null;
            var ctx = await listener.GetContextAsync().WaitAsync(linked.Token);
            var query = ctx.Request.QueryString;
            string? code = query["code"];
            string? error = query["error"];
            string? gotState = query["state"];
            if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error) && string.IsNullOrEmpty(gotState))
            {
                // favicon 之类杂请求直接放行
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                continue;
            }
            if (state != gotState)
            {
                error = "state_mismatch";
                code = null;
            }
            var ok = !string.IsNullOrEmpty(code);
            var html = ok
                ? L.Get("Svc_OAuthHtmlOk")
                : L.Get("Svc_OAuthHtmlFail", WebUtility.HtmlEncode(error ?? "unknown"));
            var buf = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf, linked.Token);
            ctx.Response.Close();
            return code;
        }
    }

    private async Task<(string AccessToken, string RefreshToken, long ExpiresIn)?> ExchangeCodeAsync(
        string tokenUrl, string clientId, string clientSecret, string scope,
        string code, string redirectUri, string verifier, Action<string>? log, CancellationToken cancel)
    {
        var fields = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        };
        if (tokenUrl.Contains("microsoftonline", StringComparison.OrdinalIgnoreCase))
            fields["scope"] = scope;
        using var resp = await _http.PostAsync(tokenUrl, new FormUrlEncodedContent(fields), cancel);
        var body = await resp.Content.ReadAsStringAsync(cancel);
        if (!resp.IsSuccessStatusCode)
        {
            log?.Invoke(L.Get("Svc_OAuthExchangeFailed", (int)resp.StatusCode));
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Svc_OAuthExchangeLog", (int)resp.StatusCode, body));
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return (
                root.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "",
                root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "",
                root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out var s) ? s : 3600);
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Svc_OAuthParseFailed", ex.Message));
            return null;
        }
    }

    private static string GenerateRandomString(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "")[..length];
    }

    private static string ComputeCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private void StopListener()
    {
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopListener();
        _http.Dispose();
    }
}
