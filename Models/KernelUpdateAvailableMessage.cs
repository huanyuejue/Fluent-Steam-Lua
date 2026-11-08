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

/// <summary>内核更新完成通知（内核窗口 → 主页收起横幅）。</summary>
public sealed class KernelUpdatedMessage
{
}
