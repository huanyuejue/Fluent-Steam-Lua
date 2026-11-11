using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FluentLuaBridge;

internal sealed class HttpRequest
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public string Query { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string? Origin { get; init; }
}

/// <summary>
/// 极简 HTTP/1.1 请求解析。只覆盖本场景：小体量 JSON、Connection: close、
/// 单次请求即断开，因此不需要分块编码、keep-alive 或管道化处理。
/// </summary>
internal static class HttpParser
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 256 * 1024;

    public static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var accumulated = new MemoryStream();
        var headerEnd = -1;

        // 逐步读到空行为止：请求头必须完整才能知道正文长度
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read <= 0) return null;

            accumulated.Write(buffer, 0, read);
            if (accumulated.Length > MaxHeaderBytes) return null;

            headerEnd = FindHeaderEnd(accumulated.GetBuffer(), (int)accumulated.Length);
        }

        var all = accumulated.GetBuffer();
        var headText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var method = requestLine[0].ToUpperInvariant();
        var target = requestLine[1];

        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target.Substring(0, queryIndex) : target;
        var query = queryIndex >= 0 ? target.Substring(queryIndex + 1) : string.Empty;

        string? origin = null;
        var contentLength = 0;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var name = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();

            if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                origin = value;
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(value, out contentLength);
            }
        }

        if (contentLength < 0 || contentLength > MaxBodyBytes) return null;

        // headerEnd 指向空行的第一个 \r，正文从其后 4 字节（\r\n\r\n）开始；
        // 少加这个偏移会把空行一起拷进 JSON，反序列化必然失败
        var bodyStart = headerEnd + 4;
        var alreadyConsumed = Math.Max(0, (int)accumulated.Length - bodyStart);
        var bodyBytes = new byte[contentLength];
        var copied = Math.Min(alreadyConsumed, contentLength);
        if (copied > 0)
        {
            Buffer.BlockCopy(all, bodyStart, bodyBytes, 0, copied);
        }

        var remaining = contentLength - copied;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(bodyBytes.AsMemory(copied, remaining), ct);
            if (read <= 0) break;
            copied += read;
            remaining -= read;
        }

        var body = contentLength > 0 ? Encoding.UTF8.GetString(bodyBytes, 0, copied) : string.Empty;

        return new HttpRequest
        {
            Method = method,
            Path = Uri.UnescapeDataString(path),
            Query = query,
            Body = body,
            Origin = origin
        };
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n')
            {
                return i - 3;
            }
        }
        return -1;
    }

    /// <summary>
    /// 跨域头。扩展从后台发出请求时 Origin 是 chrome-extension://，
    /// 而商店页直连时是 store.steampowered.com，两者都要放行；
    /// 另外带上私有网络许可，新版 Chromium 才允许公网来源访问回环地址。
    /// </summary>
    public static void AppendCors(StringBuilder sb, string? origin)
    {
        if (!string.IsNullOrEmpty(origin) && IsAllowedOrigin(origin))
        {
            sb.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\n");
        }
        else
        {
            sb.Append("Access-Control-Allow-Origin: *\r\n");
        }

        sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
        sb.Append("Access-Control-Max-Age: 600\r\n");
        sb.Append("Access-Control-Allow-Private-Network: true\r\n");
        sb.Append("Vary: Origin\r\n");
    }

    private static bool IsAllowedOrigin(string origin)
    {
        if (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var allowed in BridgeConstants.AllowedOrigins)
        {
            if (string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
