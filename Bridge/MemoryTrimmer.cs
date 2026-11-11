using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FluentLuaBridge;

/// <summary>
/// 内存回收。
/// 托管堆里最大的一块是密钥字典（数万条键值，解析后可达上百兆），
/// 但它被单例字典持有，只靠 GC 收不回来，必须先让服务自己放掉引用，
/// 再由这里触发回收并把工作集还给系统。
/// </summary>
internal static class MemoryTrimmer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr hProcess, ref PROCESS_MEMORY_COUNTERS counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

    /// <summary>当前工作集（MB），用于日志观测回收效果。</summary>
    public static double WorkingSetMb()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return p.WorkingSet64 / 1048576.0;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 触发一次完整回收。顺序很重要：先 compaction 收掉大对象段，
    /// 再裁工作集把已释放页还给系统，否则内存仍挂在进程账上。
    /// </summary>
    public static void Trim(string reason)
    {
        var before = WorkingSetMb();
        try
        {
            // 两轮：第一轮清理可回收对象，finalizer 之后再来一轮处理残留
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            // 裁工作集：让系统把不再使用的物理页收回去
            using var proc = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(proc.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // 回收失败不影响功能
        }

        var after = WorkingSetMb();
        BridgeLog.Write($"内存回收({reason}): {before:F1} MB -> {after:F1} MB");
    }
}
