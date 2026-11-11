using System;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using SteamLuaManager.Services;

namespace FluentLuaBridge;

/// <summary>
/// 桥接进程的最小服务容器。只注册入库必需的服务，
/// 刻意不引入 SteamKit2 与 UI 相关实现，保持进程轻量。
/// </summary>
internal static class BridgeServices
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IHttpClientProvider, HttpClientProvider>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISteamPathService, SteamPathService>();

        // 主程序的兜底实现依赖 SteamKit2 匿名登录，桥接进程不承担这条路：
        // 拿不到的稀有游戏会明确提示回主程序入库，避免为了少数场景拉进来几 MB 依赖
        services.AddSingleton<ISteamAppInfoService, NullAppInfoService>();

        services.AddSingleton<ISteamDepotService, SteamDepotService>();
        services.AddSingleton<ImportRunner>();

        return services.BuildServiceProvider();
    }
}

/// <summary>
/// 空兜底：入库主链路走 api.steamcmd.net，只有结果为空时才会调用到这里。
/// 返回 null 与「兜底查询失败」在原实现里行为一致，不影响正常游戏入库。
/// </summary>
internal sealed class NullAppInfoService : ISteamAppInfoService
{
    public System.Threading.Tasks.Task<AppInfoQueryResult?> QueryFullAppInfoAsync(
        int appId, ulong accessToken, System.Threading.CancellationToken ct = default)
        => System.Threading.Tasks.Task.FromResult<AppInfoQueryResult?>(null);
}
