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

    public FeedbackService(IHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    public async Task SubmitAsync(string title, string body, string? contact, bool attachLog = false, IEnumerable<string>? attachmentPaths = null, CancellationToken ct = default)
    {
        title = title?.Trim() ?? string.Empty;
        body = body?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(body))
            throw new InvalidOperationException("请填写标题和问题描述");
        if (title.Length > 100 || body.Length > 20000)
            throw new InvalidOperationException("标题或正文过长，请精简后重试");

        if (string.IsNullOrEmpty(ResendApiKey) || ResendApiKey == "MISSING")
            throw new InvalidOperationException("反馈通道未配置，请前往 GitHub 提交 Issue");

        lock (_rateLock)
        {
            if (DateTime.UtcNow - _lastSubmitUtc < MinSubmitInterval)
                throw new InvalidOperationException("提交过于频繁，请稍后再试");
            _lastSubmitUtc = DateTime.UtcNow;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version is not null ? $"{version.Major}.{version.Minor}.{version.Build}" : "未知";
        var contactLine = string.IsNullOrWhiteSpace(contact) ? "未填写" : contact.Trim();
        var text = $"{body}\n\n---\n版本：{versionText}\n系统：{Environment.OSVersion.VersionString}\n联系方式：{contactLine}";

        var attachments = new List<Dictionary<string, string>>();
        if (attachLog)
            attachments.Add(await BuildLogAttachmentAsync(ct));
        var files = attachmentPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ?? new List<string>();
        foreach (var file in files)
        {
            if (!File.Exists(file))
                throw new InvalidOperationException($"附件不存在：{Path.GetFileName(file)}");
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
                throw new InvalidOperationException($"附件读取失败（{Path.GetFileName(file)}）：{ex.Message}", ex);
            }
            attachments.Add(new Dictionary<string, string>
            {
                ["filename"] = Path.GetFileName(file),
                ["content"] = Convert.ToBase64String(bytes),
            });
        }
        if (attachments.Sum(a => (long)a["content"].Length) > MaxAttachmentsBytes)
            throw new InvalidOperationException("附件总大小超过 40MB 上限，请移除部分文件后重试");

        var payload = new Dictionary<string, object>
        {
            ["from"] = FromAddress,
            ["to"] = new[] { ToAddress },
            ["subject"] = $"[Fluent Steam Lua 反馈] {title}",
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
            throw new InvalidOperationException($"反馈发送失败（网络异常）：{ex.Message}", ex);
        }

        if (statusCode is 200 or 201) return;
        var serverMsg = TryExtractMessage(respBody);
        throw statusCode switch
        {
            401 => new InvalidOperationException("反馈通道凭证失效，请前往 GitHub 提交 Issue"),
            429 => new InvalidOperationException("提交过于频繁，请稍后再试"),
            _ => new InvalidOperationException(
                string.IsNullOrEmpty(serverMsg)
                    ? $"反馈发送失败（{statusCode}），请前往 GitHub 提交 Issue"
                    : $"反馈发送失败（{statusCode}）：{serverMsg}"),
        };
    }

    // 运行日志整体打包；缺失、开关未开或单文件超限直接报错，引导先复现或精简附件
    private static async Task<Dictionary<string, string>> BuildLogAttachmentAsync(CancellationToken ct)
    {
        var logPath = LogService.LogFilePath;
        if (!LogService.IsEnabled || !File.Exists(logPath))
            throw new InvalidOperationException("运行日志文件不存在，请先在设置中打开“日志记录”开关，复现问题后再提交");
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
            throw new InvalidOperationException($"运行日志读取失败（{ex.Message}），请稍后重试", ex);
        }
        if (bytes.Length == 0)
            throw new InvalidOperationException("运行日志为空，请复现问题后再提交");
        if (bytes.Length > MaxAttachmentsBytes)
            throw new InvalidOperationException("运行日志文件超过 40MB 上限，请清理后复现问题再提交");
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
