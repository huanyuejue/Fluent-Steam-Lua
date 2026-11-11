namespace FluentLuaBridge;

/// <summary>
/// 桥接服务对外契约的集中定义。端口固定便于扩展写死，选高位冷门段降低冲突概率。
/// </summary>
internal static class BridgeConstants
{
    /// <summary>本地回环端口。扩展的 host_permissions 必须与此保持一致。</summary>
    public const int Port = 27361;

    // 仅用于日志与诊断展示，监听本身由 TcpListener 直接绑定回环地址

    public const string SingleInstanceMutexName = "FluentLuaBridge_SingleInstance";

    /// <summary>仅允许商店域名调用，避免任意网页触发入库。</summary>
    public static readonly string[] AllowedOrigins =
    {
        "https://store.steampowered.com",
        "https://steamcommunity.com"
    };
}
