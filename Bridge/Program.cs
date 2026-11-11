using System;
using System.Threading;
using System.Windows;

namespace FluentLuaBridge;

/// <summary>
/// 商店页一键入库的桥接进程入口。
/// 常驻后台无主窗口：由 HTTP 回环接收扩展请求，入库过程用独立的小窗口展示行为链。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 同机只允许一个实例占用端口
        using var mutex = new Mutex(true, BridgeConstants.SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        // 常驻进程只处理偶尔几次入库，不需要为吞吐预留内存。
        // 交互式延迟模式配合紧凑 GC，能让密钥字典这类大对象在空闲时尽快被收回。
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;

        // 托管堆硬上限：密钥库解析后字典可达百兆量级，限住上限后
        // 超出部分会被更积极地回收，而不是一直扩堆占着工作集
        AppContext.SetData("GCHeapHardLimit", (ulong)(256L * 1024 * 1024));

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        var host = new BridgeHost();

        // HTTP 监听必须在消息循环建立前完成：进 Run() 之后再 Start 会拿到 E_FAIL 的无效句柄。
        if (!host.StartAsync().GetAwaiter().GetResult())
        {
            return;
        }

        app.Exit += (_, _) => host.Stop();

        app.Run();
    }
}
