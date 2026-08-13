using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace SteamLuaManager.Services;

/// <summary>
/// 授权提取 worker 子进程入口：进程启动时设置 SteamAppId / SteamGameId，
/// 再加载 steamclient64.dll 提取 AppTicket / ETicket，避免单进程上下文固定问题。
/// 原生 vtable 布局与 RikkoPoto/OpenSteamTool tools/extract_tickets 保持一致。
/// </summary>
public static class TicketWorker
{
    public static int Run(string appIdArg, string resultPath)
    {
        try
        {
            if (!uint.TryParse(appIdArg, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0)
                return WriteResult(resultPath, new TicketWorkerResult { Ok = false, ErrorMessage = "AppID 无效" });

            if (!Environment.Is64BitProcess)
                return WriteResult(resultPath, new TicketWorkerResult { Ok = false, ErrorMessage = "提取必须运行在 64 位进程" });

            using var session = new TicketWorkerNative.Session();
            var result = session.Extract(appId);
            return WriteResult(resultPath, result);
        }
        catch (Exception ex)
        {
            return WriteResult(resultPath, new TicketWorkerResult { Ok = false, ErrorMessage = ex.ToString() });
        }
    }

    private static int WriteResult(string resultPath, TicketWorkerResult result)
    {
        try
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 结果文件无法写入时已无法向主进程回传数据
            LogService.Error("提取", $"结果文件写入失败: {ex}");
        }
        return result.Ok ? 0 : 1;
    }
}

/// <summary>worker 使用的 JSON 结果（经文件通信，避免通过 stdout 传输敏感票据）。</summary>
public sealed class TicketWorkerResult
{
    public bool Ok { get; set; }
    public uint AppId { get; set; }
    public string? ErrorMessage { get; set; }
    public string? AppTicketBase64 { get; set; }
    public string? ETicketBase64 { get; set; }
    public ulong CurrentAccountSteamId { get; set; }
}

internal static class TicketWorkerNative
{
    private const string ClientInterfaceVersion = "SteamClient023";
    private const string UserInterfaceVersion = "SteamUser023";
    private const string UtilsInterfaceVersion = "SteamUtils010";
    private const string AppTicketInterfaceVersion = "STEAMAPPTICKET_INTERFACE_VERSION001";
    private const int EncryptedAppTicketResponseCallback = 100 + 54; // 154
    private const int MaxWaitMs = 15000;
    private const int StepMs = 50;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    private const uint LoadWithAlteredSearchPath = 8;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NativeCreateInterface(string version, IntPtr returnCode);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeCreateSteamPipe(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeBReleaseSteamPipe(IntPtr self, int pipe);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int NativeConnectToGlobalUser(IntPtr self, int pipe);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr NativeGetISteamUtils(IntPtr self, int pipe, IntPtr version);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr NativeGetISteamUser(IntPtr self, int user, int pipe, IntPtr version);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr NativeGetISteamGenericInterface(IntPtr self, int user, int pipe, IntPtr version);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint NativeGetAppOwnershipTicketData(
        IntPtr self, uint appId, IntPtr buffer, uint cbBuffer,
        out uint appIdOffset, out uint steamIdOffset, out uint signatureOffset, out uint signatureSize);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeIsApiCallCompleted(IntPtr self, ulong hCall, [MarshalAs(UnmanagedType.I1)] out bool failed);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeGetApiCallResult(
        IntPtr self, ulong hCall, IntPtr pCallback, int cbCallback, int iCallbackExpected,
        [MarshalAs(UnmanagedType.I1)] out bool failed);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate ulong NativeRequestEncryptedAppTicket(IntPtr self, IntPtr pData, int cbData);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool NativeGetEncryptedAppTicket(IntPtr self, IntPtr pTicket, int cbMax, out uint cbTicket);

    private static T GetSlot<T>(IntPtr objPtr, int slot)
        where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(objPtr);
        var fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        if (fn == IntPtr.Zero)
            throw new InvalidOperationException($"接口 vtable 槽位 {slot} 为空指针");
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    private static string? FindSteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath &&
                File.Exists(Path.Combine(steamPath, "steam.exe")))
                return steamPath;
        }
        catch { }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (key?.GetValue("InstallPath") is string installPath &&
                File.Exists(Path.Combine(installPath, "steam.exe")))
                return installPath;
        }
        catch { }

        return null;
    }

    /// <summary>单次会话：装载 dll、打开管道、提取两种票据。</summary>
    internal sealed class Session : IDisposable
    {
        private IntPtr _module;
        private IntPtr _clientPtr;
        private int _pipe;
        private readonly List<IntPtr> _ansiHandles = new();

        private IntPtr Utf8Ansi(string value)
        {
            var handle = Marshal.StringToHGlobalAnsi(value);
            _ansiHandles.Add(handle);
            return handle;
        }

    public TicketWorkerResult Extract(uint appId)
    {
        // SteamAppId / SteamGameId 必须在 steamclient64.dll 首次加载前设置
        Environment.SetEnvironmentVariable("SteamAppId", appId.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("SteamGameId", appId.ToString(CultureInfo.InvariantCulture));

        var steamPath = FindSteamPath();
        if (string.IsNullOrEmpty(steamPath))
            return Error("无法在注册表找到 Steam 安装路径，请确认 Steam 已正确安装");
        Log("提取", $"Steam 路径: {steamPath}");

        var dllPath = Path.Combine(steamPath, "steamclient64.dll");
        if (!File.Exists(dllPath))
            return Error($"未找到 steamclient64.dll：{dllPath}");

        SetDllDirectory(steamPath);
        _module = LoadLibraryEx(dllPath, IntPtr.Zero, LoadWithAlteredSearchPath);
        if (_module == IntPtr.Zero)
            return Error($"加载 steamclient64.dll 失败（错误码 {Marshal.GetLastWin32Error()}）");
        Log("提取", $"已加载 steamclient64.dll");

        var createInterface = Marshal.GetDelegateForFunctionPointer<NativeCreateInterface>(
            GetProcAddress(_module, "CreateInterface"));
        if (createInterface == null)
            return Error("steamclient64.dll 没有 CreateInterface 导出");

        _clientPtr = createInterface(ClientInterfaceVersion, IntPtr.Zero);
        if (_clientPtr == IntPtr.Zero)
            return Error($"CreateInterface({ClientInterfaceVersion}) 返回空指针");
        Log("提取", $"CreateInterface({ClientInterfaceVersion}) 成功");

        var createPipe = GetSlot<NativeCreateSteamPipe>(_clientPtr, 0);
        _pipe = createPipe(_clientPtr);
        if (_pipe == 0)
            return Error("CreateSteamPipe 失败，请确认 Steam 正在运行");
        Log("提取", $"CreateSteamPipe 成功 pipe={_pipe}");

        var connect = GetSlot<NativeConnectToGlobalUser>(_clientPtr, 2);
        var user = connect(_clientPtr, _pipe);
        if (user == 0)
            return Error("ConnectToGlobalUser 失败，请确认已登录 Steam 账号");
        Log("提取", $"ConnectToGlobalUser 成功 user={user}");

        // ===== AppTicket（ISteamAppTicket）=====
        var getGeneric = GetSlot<NativeGetISteamGenericInterface>(_clientPtr, 12);
        var appTicketInterface = getGeneric(_clientPtr, user, _pipe, Utf8Ansi(AppTicketInterfaceVersion));
        if (appTicketInterface == IntPtr.Zero)
            return Error($"GetISteamGenericInterface({AppTicketInterfaceVersion}) 返回空指针");
        Log("提取", $"GetISteamGenericInterface({AppTicketInterfaceVersion}) 成功");

        var getAppOwnershipTicketData = GetSlot<NativeGetAppOwnershipTicketData>(appTicketInterface, 0);
        var buffer = Marshal.AllocHGlobal(2048);
        try
        {
            var written = getAppOwnershipTicketData(
                appTicketInterface, appId, buffer, 2048,
                out _, out var steamIdOffset, out _, out _);
            if (written == 0 || written > 2048)
                return Error($"未获取到所有权票据（AppID {appId}）。请确认当前账号拥有该游戏并已在本机缓存授权信息，且 Steam 处于运行状态");

            var appTicket = new byte[written];
            Marshal.Copy(buffer, appTicket, 0, (int)written);
            Log("提取", $"获取所有权票据成功：{appTicket.Length} 字节");

            // 当前账号 SteamID：从所有权票据缓冲区 steamId 偏移处读取（与 OpenSteamTool 一致，
            // 不使用 ISteamUser::GetSteamID 以避免其 CSteamID 返回 ABI 在 x64 下的崩溃风险）
            var accountSteamId = steamIdOffset > 0 && steamIdOffset + 8 <= written
                ? (ulong)Marshal.ReadInt64(buffer, (int)steamIdOffset)
                : 0UL;
            Log("提取", $"当前账号 SteamID: {accountSteamId}");

            // ===== ETicket（请求 + 轮询 + 读取）=====
            var getUtils = GetSlot<NativeGetISteamUtils>(_clientPtr, 9);
            var utils = getUtils(_clientPtr, _pipe, Utf8Ansi(UtilsInterfaceVersion));
            if (utils == IntPtr.Zero)
                return Error($"GetISteamUtils({UtilsInterfaceVersion}) 返回空指针");
            Log("提取", $"GetISteamUtils({UtilsInterfaceVersion}) 成功");

            var getUser = GetSlot<NativeGetISteamUser>(_clientPtr, 5);
            var steamUser = getUser(_clientPtr, user, _pipe, Utf8Ansi(UserInterfaceVersion));
            if (steamUser == IntPtr.Zero)
                return Error($"GetISteamUser({UserInterfaceVersion}) 返回空指针");
            Log("提取", $"GetISteamUser({UserInterfaceVersion}) 成功");

            var request = GetSlot<NativeRequestEncryptedAppTicket>(steamUser, 21);
            var hCall = request(steamUser, IntPtr.Zero, 0);
            if (hCall == 0)
                return Error($"请求加密票据失败（AppID {appId}）。请确认 Steam 在线后重试");
            Log("提取", $"RequestEncryptedAppTicket 已发起 hCall={hCall}");

            var isCompleted = GetSlot<NativeIsApiCallCompleted>(utils, 11);
            var waited = 0;
            bool failed = false;
            while (!isCompleted(utils, hCall, out failed))
            {
                if (waited >= MaxWaitMs)
                    return Error("请求加密票据超时，请确认 Steam 在线后重试");
                Thread.Sleep(StepMs);
                waited += StepMs;
            }
            Log("提取", $"加密票据请求完成（等待 {waited}ms，failed={failed}）");

            var getResult = GetSlot<NativeGetApiCallResult>(utils, 13);
            var responseBytes = new byte[4];
            var pinned = GCHandle.Alloc(responseBytes, GCHandleType.Pinned);
            try
            {
                failed = false;
                var got = getResult(utils, hCall, pinned.AddrOfPinnedObject(), responseBytes.Length,
                    EncryptedAppTicketResponseCallback, out failed);
                if (!got || failed)
                    return Error("读取加密票据响应失败");
            }
            finally
            {
                pinned.Free();
            }

            if (BitConverter.ToInt32(responseBytes, 0) != 1) // k_EResultOK == 1
                return Error($"请求加密票据返回非成功状态（错误码 {BitConverter.ToInt32(responseBytes, 0)}），通常表示当前账号不拥有该游戏");
            Log("提取", "加密票据响应状态 OK");

            var getTicket = GetSlot<NativeGetEncryptedAppTicket>(steamUser, 22);
            uint cbTicket = 0;
            getTicket(steamUser, IntPtr.Zero, 0, out cbTicket);
            if (cbTicket == 0 || cbTicket > 4096)
                return Error("加密票据为空或长度异常");
            Log("提取", $"加密票据长度 {cbTicket} 字节，正在读取");

            var eticket = new byte[cbTicket];
            var ePinned = GCHandle.Alloc(eticket, GCHandleType.Pinned);
            bool ok;
            try
            {
                ok = getTicket(steamUser, ePinned.AddrOfPinnedObject(), (int)cbTicket, out cbTicket);
            }
            finally
            {
                ePinned.Free();
            }
            if (!ok)
                return Error("读取加密票据内容失败");

            eticket = eticket[..(int)cbTicket];
            Log("提取", $"获取加密票据成功：{eticket.Length} 字节");

            var result = new TicketWorkerResult
            {
                Ok = true,
                AppId = appId,
                CurrentAccountSteamId = accountSteamId,
                AppTicketBase64 = Convert.ToBase64String(appTicket),
                ETicketBase64 = Convert.ToBase64String(eticket)
            };
            Array.Clear(appTicket);
            Array.Clear(eticket);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

        private TicketWorkerResult Error(string message)
        {
            Log("提取", message);
            return new TicketWorkerResult { Ok = false, ErrorMessage = message };
        }

        public void Dispose()
        {
            try
            {
                var releasePipe = _clientPtr != IntPtr.Zero
                    ? GetSlot<NativeBReleaseSteamPipe>(_clientPtr, 1)
                    : null;
                if (_pipe != 0 && releasePipe != null)
                {
                    try { releasePipe(_clientPtr, _pipe); } catch { }
                    _pipe = 0;
                }
            }
            catch { }

            foreach (var handle in _ansiHandles)
            {
                try { Marshal.FreeHGlobal(handle); } catch { }
            }
            _ansiHandles.Clear();

            TryFreeModule();
        }

        private void TryFreeModule()
        {
            if (_module == IntPtr.Zero) return;
            try { FreeLibrary(_module); } catch { }
            _module = IntPtr.Zero;
        }

        private static void Log(string category, string message)
            => LogService.Info(category, message);
    }
}
