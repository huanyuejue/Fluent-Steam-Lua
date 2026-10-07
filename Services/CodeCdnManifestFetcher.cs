using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.IO;

namespace SteamLuaManager.Services;

// Code 直下：按固定顺序向 5 个上游取 manifest_request_code，
// 再从官方 Steam CDN 下载 manifest 文件。失败/超时才试下一个源，挂掉的源冷却 60s。
public sealed class CodeCdnManifestFetcher : IManifestFetcher
{
    private sealed record CodeSource(string Name, string UrlTemplate, bool UseManifestDexUa, bool IsSteamRunJson);

    // 顺序即顺位：20770407 → SDM → manifestdex → wudrm → steamrun（opensteamtool 不用）
    // 模板统一用 {0}=depot、{1}=gid，多余参数会被 Format 忽略
    private static readonly CodeSource[] Sources =
    [
        new("20770407", "https://20770407.xyz/manifest/{0}/{1}", false, false),
        new("SDM", "https://steamapi.993499094.xyz/manifest/{0}/{1}", false, false),
        new("manifestdex", "https://manifest.manifestdex.com/{0}", true, false),
        new("wudrm", "http://gmrc.wudrm.com/manifest/{0}", false, false),
        new("steamrun", "https://manifest.steam.run/api/manifest/{0}", false, true),
    ];

    private static readonly TimeSpan CodeTimeout = TimeSpan.FromSeconds(10);
    // 单 host 下载超时：manifest 本体 MB 级，25s 足够；配合动态 host 数量上限，
    // 避免全部挂死时一个 depot 卡住监听并发槽过久
    private static readonly TimeSpan CdnTimeout = TimeSpan.FromSeconds(25);
    // 动态列表只取前 N 个（已按负载排序），再多对成功率无意义，只烧时间
    private const int MaxDynamicHosts = 8;
    // 单源失败后的跳过时长，避免每个 depot 重复支付超时（对齐内核 kProviderCooldownMs）
    private static readonly TimeSpan SourceCooldown = TimeSpan.FromSeconds(60);
    private const string FixedCdnHost = "steampipe.akamaized.net";
    private const string ManifestDexUserAgent = "ManifestDeX/1.0";

    private readonly IHttpClientProvider _httpClientProvider;
    private readonly ISteamPathService _steamPathService;
    private readonly CdnServerListService _cdnServerList;
    private readonly object _coolLock = new();
    private readonly Dictionary<string, DateTime> _deadUntil = new(StringComparer.Ordinal);

    public CodeCdnManifestFetcher(
        IHttpClientProvider httpClientProvider,
        ISteamPathService steamPathService,
        CdnServerListService cdnServerList)
    {
        _httpClientProvider = httpClientProvider;
        _steamPathService = steamPathService;
        _cdnServerList = cdnServerList;
    }

    public async Task<ManifestFetchResult> FetchAsync(int depotId, string manifestId, CancellationToken ct, Action<string>? progress = null)
    {
        var failures = new List<string>();
        foreach (var source in Sources)
        {
            if (IsCoolingDown(source.Name))
            {
                failures.Add($"{source.Name}=冷却中");
                continue;
            }
            var (ok, code, desc) = await TryFetchCodeAsync(source, depotId, manifestId, ct);
            if (!ok)
            {
                MarkFailed(source.Name);
                failures.Add($"{source.Name}={desc}");
                progress?.Invoke($"Depot {depotId} 取码：{source.Name}失败（{desc}），换下一个源…");
                continue;
            }
            RecordSuccess(source.Name);
            progress?.Invoke($"Depot {depotId} 取码成功（源：{source.Name}），开始从 CDN 下载…");
            var (dlResult, codeRejected) = await DownloadFromCdnAsync(depotId, manifestId, code, source.Name, ct, progress);
            if (dlResult.Success)
                return dlResult;
            if (codeRejected)
            {
                // 所有 CDN 都 401/403：码是死的，换下一个源重取（源记失败进冷却）
                MarkFailed(source.Name);
                failures.Add($"{source.Name}=code被CDN拒绝");
                progress?.Invoke($"Depot {depotId} 取码：{source.Name} 的码被CDN拒绝，换下一个源重取…");
                continue;
            }
            // CDN/网络类失败：换码也救不了，直接返回详情
            return dlResult;
        }
        return new ManifestFetchResult(false, false, $"取码失败：{string.Join("，", failures)}");
    }

    private bool IsCoolingDown(string name)
    {
        lock (_coolLock)
            return _deadUntil.TryGetValue(name, out var until) && DateTime.UtcNow < until;
    }

    private void RecordSuccess(string name)
    {
        lock (_coolLock) { _deadUntil.Remove(name); }
    }

    private void MarkFailed(string name)
    {
        lock (_coolLock) { _deadUntil[name] = DateTime.UtcNow + SourceCooldown; }
    }

    private async Task<(bool Ok, ulong Code, string Desc)> TryFetchCodeAsync(
        CodeSource source, int depotId, string manifestId, CancellationToken ct)
    {
        // 统一传双参数（{0}=depot，{1}=gid），多余参数会被 Format 忽略，缺参数才会炸
        var url = string.Format(CultureInfo.InvariantCulture, source.UrlTemplate, depotId, manifestId);
        string body;
        int statusCode;
        try
        {
            // manifestdex 用独立 client 名：它的 UA 会写进共享实例的默认头，混用会污染其他源
            var clientName = source.UseManifestDexUa ? "manifest-code-dex" : "manifest-code";
            // 单次请求不重试：失败直接换下一个源；超时靠 HttpClient.Timeout 转成取消
            using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                clientName, CodeTimeout,
                client => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct),
                ConfigureForSource(source),
                maxAttempts: 1);
            statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return (false, 0, $"HTTP {statusCode}");
            body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (string.IsNullOrEmpty(body))
                return (false, 0, "空响应");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return (false, 0, "超时");
        }
        catch (HttpRequestException ex)
        {
            return (false, 0, ex.StatusCode.HasValue ? $"HTTP {(int)ex.StatusCode.Value}" : "网络异常");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }

        if (!TryParseCode(source, body, out var code))
            return (false, 0, "解析失败");
        if (code == 0)
            return (false, 0, "官方拒发（0）");
        return (true, code, string.Empty);
    }

    // manifestdex 必须用它自己的 UA；用独立 client 名避免污染共享实例的默认请求头
    private static Action<HttpClient> ConfigureForSource(CodeSource source)
    {
        if (!source.UseManifestDexUa)
            return HttpHeaderHelper.ConfigureBrowser;
        return client =>
        {
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ManifestDexUserAgent);
        };
    }

    private static bool TryParseCode(CodeSource source, string body, out ulong code)
    {
        code = 0;
        var text = body;
        if (source.IsSteamRunJson)
        {
            // {"content":"12345",...}：找 content 后的第一个引号字符串
            var key = body.IndexOf("\"content\"", StringComparison.Ordinal);
            if (key < 0) return false;
            var q1 = body.IndexOf('"', key + 9);
            if (q1 < 0) return false;
            var q2 = body.IndexOf('"', q1 + 1);
            if (q2 < 0) return false;
            text = body.Substring(q1 + 1, q2 - q1 - 1);
        }
        return ulong.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out code);
    }

    // 返回下载结果 + 码是否被拒：所有 host 一致 401/403 说明码是死的，调用方换源重取
    private async Task<(ManifestFetchResult Result, bool CodeRejected)> DownloadFromCdnAsync(
        int depotId, string manifestId, ulong code, string codeSource, CancellationToken ct, Action<string>? progress)
    {
        var steamPath = _steamPathService.DetectSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            return (new ManifestFetchResult(false, false, "未检测到 Steam 路径"), false);

        var hosts = new List<string> { FixedCdnHost };
        var dynamicHosts = await _cdnServerList.GetHostsAsync(ct);
        if (dynamicHosts.Count == 0)
            progress?.Invoke($"Depot {depotId} 动态CDN列表暂不可用，仅使用固定CDN下载");
        foreach (var h in dynamicHosts)
        {
            if (hosts.Count >= 1 + MaxDynamicHosts) break;
            if (!hosts.Contains(h, StringComparer.OrdinalIgnoreCase))
                hosts.Add(h);
        }

        var failures = new List<string>();
        var authFailures = 0;
        foreach (var host in hosts)
        {
            var url = $"https://{host}/depot/{depotId}/manifest/{manifestId}/5/{code}";
            byte[] bytes;
            try
            {
                using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                    "manifest-cdn", CdnTimeout,
                    client => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct),
                    HttpHeaderHelper.ConfigureBrowser,
                    maxAttempts: 1);
                if (!response.IsSuccessStatusCode)
                {
                    failures.Add($"{host}=HTTP {(int)response.StatusCode}");
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        authFailures++;
                    continue;
                }
                bytes = await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                failures.Add($"{host}=超时");
                continue;
            }
            catch (HttpRequestException ex)
            {
                failures.Add($"{host}={(ex.StatusCode.HasValue ? $"HTTP {(int)ex.StatusCode.Value}" : "网络异常")}");
                if (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    authFailures++;
                continue;
            }
            catch (Exception ex)
            {
                failures.Add($"{host}={ex.Message}");
                continue;
            }

            if (!TryUnwrapManifest(bytes, out var manifestBytes, out var wrapDesc))
            {
                failures.Add($"{host}=内容异常");
                continue;
            }

            var error = PlaceManifest(steamPath, depotId, manifestId, manifestBytes);
            if (error != null)
                return (new ManifestFetchResult(false, false, error), false);
            var via = host.Equals(FixedCdnHost, StringComparison.OrdinalIgnoreCase) ? "固定CDN" : host;
            progress?.Invoke($"Depot {depotId} 下载成功（{via}，{wrapDesc}）");
            return (new ManifestFetchResult(true, false, null, false, $"{codeSource}+{via}"), false);
        }
        var allRejected = failures.Count > 0 && authFailures == failures.Count;
        return (new ManifestFetchResult(false, false, $"CDN 下载失败：{string.Join("，", failures)}"), allRejected);
    }

    // 内容校验 + 脱壳：CDN 回来的可能是 ZIP 套壳（单 entry，一般叫 z），里面才是
    // depotcache 格式的 manifest；合法文件都以 D0-17-F6-71 魔数开头
    //（已用本机 3 个现存 depotcache + 实测新包交叉验证）。
    // 裸包（非 ZIP）沿用旧逻辑直接存，保证前后兼容。
    private static readonly byte[] ManifestMagic = [0xD0, 0x17, 0xF6, 0x71];

    private static bool TryUnwrapManifest(byte[] bytes, out byte[] manifest, out string wrap)
    {
        manifest = bytes;
        wrap = "裸包";
        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                var entry = zip.Entries.FirstOrDefault(e => e.Name == "z")
                    ?? (zip.Entries.Count == 1 ? zip.Entries[0] : null);
                if (entry == null || entry.Length <= 0) return false;
                using var es = entry.Open();
                using var outMs = new MemoryStream();
                es.CopyTo(outMs);
                manifest = outMs.ToArray();
                wrap = $"ZIP解包({entry.FullName},{manifest.Length}字节)";
            }
            catch { return false; }
        }
        // 错误页是短 HTML（以 < 开头）；合法 manifest 必须带魔数，
        // 否则宁可换下一个 CDN，也绝不把坏文件存进 depotcache 害 Steam 删文件
        if (manifest.Length < 32) return false;
        if (manifest[0] == (byte)'<') return false;
        for (int i = 0; i < ManifestMagic.Length; i++)
            if (manifest[i] != ManifestMagic[i]) return false;
        return true;
    }

    // 落盘用临时文件 + 同卷原子改名，避免 Steam 读到半截文件（与 Hub 页签同策略）
    private static string? PlaceManifest(string steamPath, int depotId, string manifestId, byte[] bytes)
    {
        var depotCacheDir = Path.Combine(steamPath, "depotcache");
        var destPath = Path.Combine(depotCacheDir, $"{depotId}_{manifestId}.manifest");
        var tmpPath = destPath + $".tmp_{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(depotCacheDir);
            File.WriteAllBytes(tmpPath, bytes);
            File.Move(tmpPath, destPath, true);
            return null;
        }
        catch (Exception ex)
        {
            return $"写入 depotcache 失败：{ex.Message}";
        }
        finally
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
    }
}
