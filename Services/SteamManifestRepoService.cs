using System.Net.Http;

namespace SteamLuaManager.Services;

// 手动获取已下线，这里只剩镜像测速（供内核包下载选源参考）。
public class SteamManifestRepoService : ISteamManifestRepoService
{
    private const string ClientName = "mirror-test";

    private readonly IHttpClientProvider _httpClientProvider;

    public SteamManifestRepoService(IHttpClientProvider httpClientProvider)
    {
        _httpClientProvider = httpClientProvider;
    }

    // 镜像测速仅供内核包下载参考：用本仓库的稳定小文件做探针
    private const string SpeedTestRaw = "https://raw.githubusercontent.com/huanyuejue/OpenSteamTool/main/README.md";

    /// <summary>直连 + 各镜像并发下载同一探测文件；单个 15s 超时，互不等待。</summary>
    public async Task<List<(string Name, long LatencyMs, bool IsSuccess)>> TestMirrorSpeedAsync(
        IProgress<(string Name, long LatencyMs, bool IsSuccess)>? progress = null)
    {
        var urls = GitHubMirror.WithAssetMirrors(SpeedTestRaw);

        var taskList = urls.Select(async url =>
        {
            var name = GitHubMirror.SourceDisplayName(url);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var response = await _httpClientProvider.SendWithProxyRetryAsync(
                    ClientName, TimeSpan.FromSeconds(15),
                    client => client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead),
                    HttpHeaderHelper.ConfigureBrowser);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();
                sw.Stop();
                return (name, sw.ElapsedMilliseconds, bytes.Length > 0);
            }
            catch
            {
                sw.Stop();
                return (name, sw.ElapsedMilliseconds, false);
            }
        }).Select(async task =>
        {
            var result = await task;
            progress?.Report(result);
            return result;
        }).ToList();

        var pending = new List<Task<(string Name, long LatencyMs, bool IsSuccess)>>(taskList);
        var results = new List<(string Name, long LatencyMs, bool IsSuccess)>();
        while (pending.Count > 0)
        {
            var doneTask = await Task.WhenAny(pending);
            pending.Remove(doneTask);
            results.Add(await doneTask);
        }
        return results;
    }
}
