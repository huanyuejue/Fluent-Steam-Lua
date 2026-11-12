using Microsoft.Win32;

namespace SteamLuaManager.Services;

/// <summary>
/// OpenSteamTool 使用的 Steam 凭据注册表存储封装。
/// 目标：HKCU\Software\Valve\Steam\Apps\{AppId}，值 AppTicket / ETicket（REG_BINARY）。
/// 与 OpenSteamTool Lua 的 setAppTicket / setETicket 写入行为一致。
/// </summary>
public sealed class SteamCredentialStore
{
    private const string SteamKeyRoot = @"Software\Valve\Steam\Apps";

    /// <summary>注册表写入是否可用（Steam 凭据目录必须为 64 位视图，要求 64 位进程）。</summary>
    public static bool IsSupported => Environment.Is64BitProcess;

    public static string GetAppsKey(uint appId) => $@"{SteamKeyRoot}\{appId}";

    /// <summary>
    /// 写入 AppTicket / ETicket，并用本次导入的新值无条件覆盖旧值。
    /// 注册表里原有的 SteamID 一并清除，让票据内嵌值成为唯一授权来源：
    /// 内核启动时拿注册表 SteamID 校验票据，两者不同会以 88500012 拒绝启动。
    /// 支持补偿式回滚：写入过程中任一值失败或回读不一致时，恢复原值（原值不存在则删除）。
    /// </summary>
    public static (bool Ok, string? Error, string? Warning) WriteTickets(
        uint appId, byte[] appTicket, byte[] eticket, ulong ticketSteamId)
    {
        if (!IsSupported)
            return (false, L.Get("Svc_CredNeed64"), null);

        var existing = ReadExisting(appId);
        var warning = DescribeSteamIdClear(existing.SteamId, ticketSteamId);

        try
        {
            using var key = OpenAppsKey(appId, writable: true);
            if (key == null)
                return (false, L.Get("Svc_CredOpenFailed"), warning);

            key.SetValue("AppTicket", appTicket, RegistryValueKind.Binary);
            key.SetValue("ETicket", eticket, RegistryValueKind.Binary);

            // 有旧值就删，不管是否与票据一致：留着它迟早和票据打架，
            // 而且换票后容易忘记同步这一处
            if (existing.SteamId.Exists)
            {
                DeleteValue(key, "SteamID");
                Log("reg", L.Get("Svc_CredSteamIdCleared", appId));
            }

            var verificationError = VerifyWrite(key, appTicket, eticket);
            if (verificationError == null)
            {
                Log("reg", L.Get("Svc_CredWritten", appId, appTicket.Length, eticket.Length));
                return (true, null, warning);
            }

            var rollbackError = Rollback(appId, existing);
            return rollbackError == null
                ? (false, L.Get("Svc_CredVerifyFailedRestored", verificationError), warning)
                : (false, L.Get("Svc_CredVerifyFailedPartial", verificationError, rollbackError),
                    warning);
        }
        catch (Exception ex)
        {
            var rollbackError = Rollback(appId, existing);
            Log("reg", L.Get("Svc_CredWriteFailedLog", ex.Message,
                rollbackError == null ? L.Get("Common_Success") : L.Get("Svc_CredRestoreInlineFailed", rollbackError)));
            return rollbackError == null
                ? (false, L.Get("Svc_CredWriteFailedRestored", ex.Message), warning)
                : (false, L.Get("Svc_CredWriteFailedPartial", ex.Message, rollbackError), warning);
        }
    }

    /// <summary>
    /// 根据已读到的 SteamID 快照给出提示。存在就清，一致时也清，
    /// 只是说法不同：用户需要知道注册表被动过。
    /// </summary>
    private static string? DescribeSteamIdClear(ExistingValue storedSteamId, ulong ticketSteamId)
    {
        if (!storedSteamId.Exists) return null;

        // 票据没解析出 SteamID 时照样清：留着旧值必然和票据打架
        var same = ticketSteamId != 0
                   && storedSteamId.Data is string raw
                   && ulong.TryParse(raw, out var stored)
                   && stored == ticketSteamId;

        return L.Get(same ? "Svc_CredSteamIdSame" : "Svc_CredSteamIdMismatch");
    }

    private static RegistryKey? OpenAppsKey(uint appId, bool writable)
    {
        try
        {
            var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            return writable
                ? baseKey.CreateSubKey(GetAppsKey(appId), writable: true)
                : baseKey.OpenSubKey(GetAppsKey(appId), writable: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>注册表原值快照（用于回滚）。Data 容纳二进制与字符串两种形态。</summary>
    private sealed record ExistingValue(bool Exists, RegistryValueKind Kind, object? Data);

    private static (ExistingValue AppTicket, ExistingValue ETicket, ExistingValue SteamId) ReadExisting(uint appId)
    {
        using var key = OpenAppsKey(appId, writable: false);
        return (ReadValue(key, "AppTicket"), ReadValue(key, "ETicket"), ReadValue(key, "SteamID"));
    }

    private static ExistingValue ReadValue(RegistryKey? key, string name)
    {
        if (key == null) return new ExistingValue(false, RegistryValueKind.Unknown, null);
        try
        {
            var kind = key.GetValueKind(name);
            if (kind == RegistryValueKind.None)
                return new ExistingValue(false, RegistryValueKind.Unknown, null);

            // SteamID 是 REG_SZ，票据是 REG_BINARY，原值快照要能容纳两种
            return new ExistingValue(true, kind, key.GetValue(name));
        }
        catch
        {
            return new ExistingValue(false, RegistryValueKind.Unknown, null);
        }
    }

    private static string? VerifyWrite(RegistryKey key, byte[] appTicket, byte[] eticket)
    {
        if (key.GetValueKind("AppTicket") != RegistryValueKind.Binary)
            return L.Get("Svc_CredAppTicketNotBinary");
        if (key.GetValueKind("ETicket") != RegistryValueKind.Binary)
            return L.Get("Svc_CredEticketNotBinary");

        if (key.GetValue("AppTicket") is not byte[] readApp || !readApp.AsSpan().SequenceEqual(appTicket))
            return L.Get("Svc_CredAppTicketMismatch");
        if (key.GetValue("ETicket") is not byte[] readE || !readE.AsSpan().SequenceEqual(eticket))
            return L.Get("Svc_CredEticketMismatch");

        return null;
    }

    private static string? Rollback(uint appId,
        (ExistingValue AppTicket, ExistingValue ETicket, ExistingValue SteamId) existing)
    {
        try
        {
            using var key = OpenAppsKey(appId, writable: true);
            if (key == null) return L.Get("Svc_CredOpenForRollback");

            RestoreOrDelete(key, "AppTicket", existing.AppTicket);
            RestoreOrDelete(key, "ETicket", existing.ETicket);
            RestoreOrDelete(key, "SteamID", existing.SteamId);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static void RestoreOrDelete(RegistryKey key, string name, ExistingValue value)
    {
        if (value.Exists)
        {
            if (value.Data != null)
                key.SetValue(name, value.Data, value.Kind);
        }
        else
        {
            DeleteValue(key, name);
        }
    }

    /// <summary>删除指定值；原本就不存在时视为成功。</summary>
    private static void DeleteValue(RegistryKey key, string name)
    {
        try { key.DeleteValue(name, throwOnMissingValue: false); } catch { }
    }

    private static void Log(string category, string message)
        => LogService.Info(category, message);
}
