using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamLuaManager.Services;

public interface IOpenSteamToolService
{
    bool IsInstalled { get; }
    string? GetSteamPath();
    Task<string?> GetLocalVersionAsync();
    Task<(string version, string downloadUrl, string releaseUrl)> GetRemoteInfoAsync();
    Task InstallAsync(string downloadUrl, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default);
    Task UninstallAsync();
}

public class OpenSteamToolService : IOpenSteamToolService
{
    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;
    private const string GitHubLatestUrl = "https://api.github.com/repos/OpenSteam001/OpenSteamTool/releases/latest";
    private static readonly string[] RequiredDlls = ["dwmapi.dll", "xinput1_4.dll", "OpenSteamTool.dll"];
    private static readonly Dictionary<string, string> EmbeddedVersionMap = new()
    {
        ["115ec256c7c5b066926015a24120cf6e7d9e5a7a5b87441817c2de11cc3f9fec"] = "v1.2.0",
        ["494bc762351b4dc80ca2f36cc005fc89b976f24e6e77c12945229e3e05502e93"] = "v1.3.0",
        ["8d4cb44bc57565e8183b9dab72eda873305c4257e080e29d57bbfda4cc755585"] = "v1.3.1",
        ["6daeef8b0a085c22ca43a6efeceee1f8547c3044573c394ec7cd4945fba13430"] = "v1.3.2",
        ["a1c4ffc819d96d9c397d132cb718aa7d7d44651375845e4bb9258499e643857d"] = "v1.4.0",
        ["550f9edfede4a4403f7aefdd5c4a40fdd92be22135443857fd997b415d7ced1e"] = "v1.4.1",
        ["962f5c7700a0ddde46cd419763ed15f95baf5a4a93525559f7bb6453aa1b1aac"] = "v1.4.2",
        ["d578da0170d18cd8f7cdee36a617a80147bddc8945701e3d5d1f11315d7e36fd"] = "v1.4.3",
        ["9113dce46b7a807e30abc018ee8469f188c51e2d277279c2f15427efc2f52226"] = "v1.4.4",
        ["5ec8351d5949c10c97210759efb5d618741d8414d67ff650e328d036e352c10c"] = "v1.4.5",
        ["cd7266e06d7416d3b02335386c54b909df68e2e0605941f70bb21ed392ee639f"] = "v1.4.6",
        ["9a2c459ad5124eeb48e4a1c7ac9808e5fad6eda54f7df8ad4b2e4465818f50f7"] = "v1.4.6-fix",
        ["09d26118c7cf796cf37562c4cb965d1b213f5866c02ca04fe0fafc4c2f22b0bc"] = "v1.4.7",
    };
    private static readonly byte[] VersionMarker = [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00];

    public OpenSteamToolService(ISteamPathService steamPathService, IHttpClientProvider httpClientProvider)
    {
        _steamPathService = steamPathService;
        _httpClientProvider = httpClientProvider;
    }



    public bool IsInstalled => _steamPathService.DetectSteamToolType() == SteamToolType.OpenSteamTool;

    public string? GetSteamPath()
    {
        var path = _steamPathService.DetectSteamPath();
        return !string.IsNullOrEmpty(path) ? path : null;
    }

    public Task<string?> GetLocalVersionAsync()
    {
        var steamPath = GetSteamPath();
        if (steamPath == null) return Task.FromResult<string?>(null);
        var dllPath = Path.Combine(steamPath, "OpenSteamTool.dll");
        if (!File.Exists(dllPath)) return Task.FromResult<string?>(null);

        // 1. 嵌入 SHA256 字典（覆盖 pre-1.4.8 所有官方构建）
        var localHash = ComputeSha256(dllPath);
        if (EmbeddedVersionMap.TryGetValue(localHash, out var embeddedVer))
            return Task.FromResult<string?>(embeddedVer);

        // 2. 二进制标记位解析（覆盖 1.4.8+ 版本）
        try
        {
            var bytes = File.ReadAllBytes(dllPath);
            for (int i = 0; i <= bytes.Length - 12; i++)
            {
                var found = true;
                for (int j = 0; j < VersionMarker.Length; j++)
                {
                    if (bytes[i + j] != VersionMarker[j]) { found = false; break; }
                }
                if (!found) continue;

                var start = i + VersionMarker.Length;
                var end = start;
                while (end < bytes.Length && bytes[end] != 0) end++;
                if (end > start)
                {
                    var ver = Encoding.ASCII.GetString(bytes, start, end - start);
                    if (Regex.IsMatch(ver, @"^v?\d+\.\d+\.\d+"))
                        return Task.FromResult<string?>(ver);
                }
            }
        }
        catch (Exception ex) { LogService.Warn("内核", $"读取本地版本失败: {ex.Message}"); }

        return Task.FromResult<string?>(null);
    }

    public async Task<(string version, string downloadUrl, string releaseUrl)> GetRemoteInfoAsync()
    {
        var json = await _httpClientProvider.SendWithProxyRetryAsync(
            "open-steam-tool",
            TimeSpan.FromSeconds(120),
            client => client.GetStringAsync(GitHubLatestUrl),
            HttpHeaderHelper.ConfigureApp);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "0.0.0";
        var releaseUrl = doc.RootElement.TryGetProperty("html_url", out var htmlUrl)
            ? htmlUrl.GetString() ?? ""
            : $"https://github.com/OpenSteam001/OpenSteamTool/releases/tag/{tag}";
        var downloadUrl = "";
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.Contains("-Release") && name.EndsWith(".zip"))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    break;
                }
            }
        }
        return (tag, downloadUrl, releaseUrl);
    }

    public async Task InstallAsync(string downloadUrl, IProgress<string>? status = null, IProgress<int>? downloadProgress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        status?.Report("正在下载 OpenSteamTool...");

        var tempZip = Path.Combine(Path.GetTempPath(), $"OpenSteamTool_{Guid.NewGuid():N}.zip");
        try
        {
            using (var response = await _httpClientProvider.SendWithProxyRetryAsync(
                       "open-steam-tool",
                       TimeSpan.FromSeconds(120),
                       client => client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead),
                       HttpHeaderHelper.ConfigureApp))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1;

                await using var httpStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = File.Create(tempZip);

                var buffer = new byte[81920];
                long readBytes = 0;
                int bytesRead;
                while ((bytesRead = await httpStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    readBytes += bytesRead;
                    if (totalBytes > 0 && downloadProgress != null)
                    {
                        var percent = (int)(readBytes * 100 / totalBytes);
                        downloadProgress.Report(Math.Clamp(percent, 0, 100));
                    }
                }
            }

            ct.ThrowIfCancellationRequested();
            status?.Report("正在解压并安装 DLL...");
            using var archive = ZipFile.OpenRead(tempZip);
            var extracted = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(entry.Name);
                if (string.IsNullOrEmpty(fileName)) continue;
                if (!RequiredDlls.Contains(fileName, StringComparer.OrdinalIgnoreCase)) continue;

                var targetPath = Path.Combine(steamPath, fileName);
                entry.ExtractToFile(targetPath, overwrite: true);
                extracted++;
            }

            if (extracted == 0)
                throw new InvalidOperationException("压缩包中未找到 OpenSteamTool DLL 文件");

            status?.Report("安装完成");
        }
        finally
        {
            try { File.Delete(tempZip); } catch { }
        }
    }

    public Task UninstallAsync()
    {
        var steamPath = GetSteamPath() ?? throw new InvalidOperationException("无法检测 Steam 路径");
        var removed = 0;
        foreach (var dll in RequiredDlls)
        {
            var path = Path.Combine(steamPath, dll);
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"无法删除 {dll}，请确保 Steam 已关闭后再试");
                }
            }
        }
        if (removed == 0)
            throw new InvalidOperationException("未检测到已安装的 OpenSteamTool 文件");
        return Task.CompletedTask;
    }

    // ========== 辅助方法 ==========

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
