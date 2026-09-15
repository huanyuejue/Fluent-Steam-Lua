namespace SteamLuaManager.Models;

/// <summary>内核检测到新版本通知消息（启动检测 → 主页横幅）。</summary>
public sealed class KernelUpdateAvailableMessage
{
    public KernelUpdateAvailableMessage(string localVersion, string remoteVersion)
    {
        LocalVersion = localVersion;
        RemoteVersion = remoteVersion;
    }

    public string LocalVersion { get; }

    public string RemoteVersion { get; }
}
