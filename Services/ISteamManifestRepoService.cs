namespace SteamLuaManager.Services;

public interface ISteamManifestRepoService
{
    /// <summary>镜像源可用性测速：直连 + 各镜像并发下载同一探测文件，按完成顺序回报（仅供内核包下载参考）。</summary>
    Task<List<(string Name, long LatencyMs, bool IsSuccess)>> TestMirrorSpeedAsync(
        IProgress<(string Name, long LatencyMs, bool IsSuccess)>? progress = null);
}
