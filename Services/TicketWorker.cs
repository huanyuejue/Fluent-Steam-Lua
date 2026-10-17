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
                return WriteResult(resultPath, new TicketWorkerResult { Ok = false, ErrorMessage = L.Get("Ticket_InvalidAppId") });

            if (!Environment.Is64BitProcess)
                return WriteResult(resultPath, new TicketWorkerResult { Ok = false, ErrorMessage = L.Get("Ticket_Need64Bit") });

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
            LogService.Error(L.Get("LogCat_Extract"), L.Get("Ticket_ResultWriteFailed", ex));
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
            throw new InvalidOperationException(L.Get("Ticket_VtableNull", slot));
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
            return Error(L.Get("Ticket_NoSteamPath"));
        Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogSteamPath", steamPath));

        var dllPath = Path.Combine(steamPath, "steamclient64.dll");
        if (!File.Exists(dllPath))
            return Error(L.Get("Ticket_DllNotFound", dllPath));

        SetDllDirectory(steamPath);
        _module = LoadLibraryEx(dllPath, IntPtr.Zero, LoadWithAlteredSearchPath);
        if (_module == IntPtr.Zero)
            return Error(L.Get("Ticket_LoadDllFailed", Marshal.GetLastWin32Error()));
        Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogDllLoaded"));

        var createInterface = Marshal.GetDelegateForFunctionPointer<NativeCreateInterface>(
            GetProcAddress(_module, "CreateInterface"));
        if (createInterface == null)
            return Error(L.Get("Ticket_NoCreateInterface"));

        _clientPtr = createInterface(ClientInterfaceVersion, IntPtr.Zero);
        if (_clientPtr == IntPtr.Zero)
            return Error(L.Get("Ticket_CreateInterfaceNull", ClientInterfaceVersion));
        Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogCreateInterfaceOk", ClientInterfaceVersion));

        var createPipe = GetSlot<NativeCreateSteamPipe>(_clientPtr, 0);
        _pipe = createPipe(_clientPtr);
        if (_pipe == 0)
            return Error(L.Get("Ticket_PipeFailed"));
        Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogPipeOk", _pipe));

        var connect = GetSlot<NativeConnectToGlobalUser>(_clientPtr, 2);
        var user = connect(_clientPtr, _pipe);
        if (user == 0)
            return Error(L.Get("Ticket_ConnectFailed"));
        Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogConnectOk", user));

        // ===== AppTicket（ISteamAppTicket）=====
        var getGeneric = GetSlot<NativeGetISteamGenericInterface>(_clientPtr, 12);
        var appTicketInterface = getGeneric(_clientPtr, user, _pipe, Utf8Ansi(AppTicketInterfaceVersion));
        if (appTicketInterface == IntPtr.Zero)
            return Error(L.Get("Ticket_GenericNull", AppTicketInterfaceVersion));
        Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogGenericOk", AppTicketInterfaceVersion));

        var getAppOwnershipTicketData = GetSlot<NativeGetAppOwnershipTicketData>(appTicketInterface, 0);
        var buffer = Marshal.AllocHGlobal(2048);
        try
        {
            var written = getAppOwnershipTicketData(
                appTicketInterface, appId, buffer, 2048,
                out var appIdOffset, out var steamIdOffset, out _, out _);
            if (written == 0 || written > 2048)
                return Error(L.Get("Ticket_NoOwnershipTicket", appId));

            var appTicket = new byte[written];
            Marshal.Copy(buffer, appTicket, 0, (int)written);
            Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogOwnershipOk", appTicket.Length));

            // 当前账号 SteamID：从所有权票据缓冲区 steamId 偏移处读取（与 OpenSteamTool 一致，
            // 不使用 ISteamUser::GetSteamID 以避免其 CSteamID 返回 ABI 在 x64 下的崩溃风险）
            var accountSteamId = steamIdOffset > 0 && steamIdOffset + 8 <= written
                ? (ulong)Marshal.ReadInt64(buffer, (int)steamIdOffset)
                : 0UL;
            Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogAccountId", accountSteamId));
            // Steam 返回的才是真偏移（布局随票据类型变化，不一定是 appid@16/steamid@8），记下来备查
            var embeddedAppId = appIdOffset + 4 <= written
                ? (uint)Marshal.ReadInt32(buffer, (int)appIdOffset)
                : 0;
            Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogOffsets", appIdOffset, steamIdOffset, embeddedAppId, appId));

            // ===== ETicket（请求 + 轮询 + 读取）=====
            var getUtils = GetSlot<NativeGetISteamUtils>(_clientPtr, 9);
            var utils = getUtils(_clientPtr, _pipe, Utf8Ansi(UtilsInterfaceVersion));
            if (utils == IntPtr.Zero)
                return Error(L.Get("Ticket_UtilsNull", UtilsInterfaceVersion));
            Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogUtilsOk", UtilsInterfaceVersion));

            var getUser = GetSlot<NativeGetISteamUser>(_clientPtr, 5);
            var steamUser = getUser(_clientPtr, user, _pipe, Utf8Ansi(UserInterfaceVersion));
            if (steamUser == IntPtr.Zero)
                return Error(L.Get("Ticket_UserNull", UserInterfaceVersion));
            Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogUserOk", UserInterfaceVersion));

            var request = GetSlot<NativeRequestEncryptedAppTicket>(steamUser, 21);
            var hCall = request(steamUser, IntPtr.Zero, 0);
            if (hCall == 0)
                return Error(L.Get("Ticket_RequestTicketFailed", appId));
            Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogRequestSent", hCall));

            var isCompleted = GetSlot<NativeIsApiCallCompleted>(utils, 11);
            var waited = 0;
            bool failed = false;
            while (!isCompleted(utils, hCall, out failed))
            {
                if (waited >= MaxWaitMs)
                    return Error(L.Get("Ticket_RequestTimeout"));
                Thread.Sleep(StepMs);
                waited += StepMs;
            }
            Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogRequestDone", waited, failed));

            var getResult = GetSlot<NativeGetApiCallResult>(utils, 13);
            var responseBytes = new byte[4];
            var pinned = GCHandle.Alloc(responseBytes, GCHandleType.Pinned);
            try
            {
                failed = false;
                var got = getResult(utils, hCall, pinned.AddrOfPinnedObject(), responseBytes.Length,
                    EncryptedAppTicketResponseCallback, out failed);
                if (!got || failed)
                    return Error(L.Get("Ticket_ReadResponseFailed"));
            }
            finally
            {
                pinned.Free();
            }

            if (BitConverter.ToInt32(responseBytes, 0) != 1) // k_EResultOK == 1
                return Error(L.Get("Ticket_ResponseNotOk", BitConverter.ToInt32(responseBytes, 0)));
            Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogResponseOk"));

            var getTicket = GetSlot<NativeGetEncryptedAppTicket>(steamUser, 22);
            uint cbTicket = 0;
            getTicket(steamUser, IntPtr.Zero, 0, out cbTicket);
            if (cbTicket == 0 || cbTicket > 4096)
                return Error(L.Get("Ticket_TicketEmpty"));
            Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogTicketLen", cbTicket));

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
                return Error(L.Get("Ticket_ReadTicketFailed"));

            eticket = eticket[..(int)cbTicket];
            Log(L.Get("LogCat_Extract"), L.Get("Ticket_LogTicketOk", eticket.Length));

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
            Log(L.Get("LogCat_Extract"), message);
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
