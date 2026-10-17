using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SteamLuaManager.Services;

public interface IFeedbackService
{
    Task SubmitAsync(string title, string body, string? contact, bool attachLog = false, IEnumerable<string>? attachmentPaths = null, CancellationToken ct = default);
}

public class FeedbackService : IFeedbackService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly IOpenSteamToolService _openSteamToolService;

    // 反馈通道凭证构建时注入（环境变量 RESEND_API_KEY），源码与仓库中永不出现明文
    private static string ResendApiKey => FeedbackSecrets.ResendApiKey;
    private const string FromAddress = "onboarding@resend.dev";
    private const string ToAddress = "w1376158303@gmail.com";
    private const string Endpoint = "https://api.resend.com/emails";

    private static readonly object _rateLock = new();
    private static DateTime _lastSubmitUtc = DateTime.MinValue;
    private static readonly TimeSpan MinSubmitInterval = TimeSpan.FromMinutes(2);

    // 附件总大小上限（Base64 编码后），与接口限制对齐
    public const long MaxAttachmentsBytes = 40L * 1024 * 1024;

    public FeedbackService(IHttpClientProvider httpClientProvider, IOpenSteamToolService openSteamToolService)
    {
        _httpClientProvider = httpClientProvider;
        _openSteamToolService = openSteamToolService;
    }

    public async Task SubmitAsync(string title, string body, string? contact, bool attachLog = false, IEnumerable<string>? attachmentPaths = null, CancellationToken ct = default)
    {
        title = title?.Trim() ?? string.Empty;
        body = body?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(body))
            throw new InvalidOperationException(L.Get("Svc_FbNoTitleBody"));
        if (title.Length > 100 || body.Length > 20000)
            throw new InvalidOperationException(L.Get("Svc_FbTooLong"));

        if (string.IsNullOrEmpty(ResendApiKey) || ResendApiKey == "MISSING")
            throw new InvalidOperationException(L.Get("Svc_FbNotConfigured"));

        lock (_rateLock)
        {
            if (DateTime.UtcNow - _lastSubmitUtc < MinSubmitInterval)
                throw new InvalidOperationException(L.Get("Svc_FbRateLimit"));
            _lastSubmitUtc = DateTime.UtcNow;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version is not null ? $"{version.Major}.{version.Minor}.{version.Build}" : L.Get("Svc_FbUnknown");
        var contactLine = string.IsNullOrWhiteSpace(contact) ? L.Get("Svc_FbNotFilled") : contact.Trim();
        // 内核版本读本地 DLL 解析，超时直接记未知，不拖提交
        var kernelText = L.Get("Svc_FbUnknown");
        try
        {
            var kernelTask = _openSteamToolService.GetLocalVersionAsync();
            var finished = await Task.WhenAny(kernelTask, Task.Delay(TimeSpan.FromSeconds(5), ct));
            if (finished == kernelTask)
            {
                var v = await kernelTask;
                // 版本读不到不代表没装：老内核既不在 SHA 表里也没打标记位，单独区分
                kernelText = v ?? (_openSteamToolService.IsInstalled ? L.Get("Svc_FbInstalledUnknown") : L.Get("Svc_FbNotInstalled"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            kernelText = L.Get("Svc_FbUnknown");
        }
        var text = L.Get("Svc_FbMailBody", body, versionText, kernelText, Environment.OSVersion.VersionString, contactLine);

        var attachments = new List<Dictionary<string, string>>();
        if (attachLog)
            attachments.Add(await BuildLogAttachmentAsync(ct));
        var files = attachmentPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ?? new List<string>();
        foreach (var file in files)
        {
            if (!File.Exists(file))
                throw new InvalidOperationException(L.Get("Svc_FbAttachMissing", Path.GetFileName(file)));
            // 同日志：被其他进程写入中的文件，读时带 ReadWrite 共享
            byte[] bytes;
            try
            {
                await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException(L.Get("Svc_FbAttachReadFailed", Path.GetFileName(file), ex.Message), ex);
            }
            attachments.Add(new Dictionary<string, string>
            {
                ["filename"] = Path.GetFileName(file),
                ["content"] = Convert.ToBase64String(bytes),
            });
        }
        if (attachments.Sum(a => (long)a["content"].Length) > MaxAttachmentsBytes)
            throw new InvalidOperationException(L.Get("Svc_FbAttachTooBig"));

        var payload = new Dictionary<string, object>
        {
            ["from"] = FromAddress,
            ["to"] = new[] { ToAddress },
            ["subject"] = L.Get("Svc_FbSubject", title),
            ["text"] = text,
        };
        if (attachments.Count > 0)
            payload["attachments"] = attachments;
        var payloadJson = JsonSerializer.Serialize(payload);

        int statusCode;
        string respBody;
        try
        {
            // 请求消息每次新建：provider 失败会重试，复用已发送的 message 会抛异常；
            // 认证头挂单次 message，不污染共享 client
            (statusCode, respBody) = await _httpClientProvider.SendWithProxyRetryAsync(
                "feedback",
                TimeSpan.FromSeconds(20),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ResendApiKey);
                    request.Headers.Accept.ParseAdd("application/json");
                    request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, ct);
                    return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
                },
                HttpHeaderHelper.ConfigureApp);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(L.Get("Svc_FbNetworkFail", ex.Message), ex);
        }

        if (statusCode is 200 or 201) return;
        var serverMsg = TryExtractMessage(respBody);
        throw statusCode switch
        {
            401 => new InvalidOperationException(L.Get("Svc_FbAuthFailed")),
            429 => new InvalidOperationException(L.Get("Svc_FbRateLimit")),
            _ => new InvalidOperationException(
                string.IsNullOrEmpty(serverMsg)
                    ? L.Get("Svc_FbSendFailed", statusCode)
                    : L.Get("Svc_FbSendFailedDetail", statusCode, serverMsg)),
        };
    }

    // 运行日志整体打包；缺失、开关未开或单文件超限直接报错，引导先复现或精简附件
    private static async Task<Dictionary<string, string>> BuildLogAttachmentAsync(CancellationToken ct)
    {
        var logPath = LogService.LogFilePath;
        if (!LogService.IsEnabled || !File.Exists(logPath))
            throw new InvalidOperationException(L.Get("Svc_FbLogMissing"));
        // 日志正被本程序追加写入，读时必须带上 ReadWrite 共享，否则撞独占锁
        byte[] bytes;
        try
        {
            await using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            using var ms = new MemoryStream();
            await fs.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new InvalidOperationException(L.Get("Svc_FbLogReadFailed", ex.Message), ex);
        }
        if (bytes.Length == 0)
            throw new InvalidOperationException(L.Get("Svc_FbLogEmpty"));
        if (bytes.Length > MaxAttachmentsBytes)
            throw new InvalidOperationException(L.Get("Svc_FbLogTooBig"));
        return new Dictionary<string, string>
        {
            ["filename"] = "app.log",
            ["content"] = Convert.ToBase64String(bytes),
        };
    }

    private static string TryExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m))
                return m.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }
}
