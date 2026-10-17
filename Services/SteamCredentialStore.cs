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

    /// <summary>读取注册表中已保存的 SteamID（可能存在也可能不存在）。</summary>
    public static (bool Exists, ulong Value) GetStoredSteamId(uint appId)
    {
        if (!IsSupported) return (false, 0);
        try
        {
            using var key = OpenAppsKey(appId, writable: false);
            if (key?.GetValueKind("SteamID") is not RegistryValueKind kind ||
                kind != RegistryValueKind.String)
                return (false, 0);

            if (key.GetValue("SteamID") is string raw &&
                ulong.TryParse(raw, out var steamId) &&
                steamId != 0)
                return (true, steamId);

            return (false, 0);
        }
        catch
        {
            return (false, 0);
        }
    }

    /// <summary>
    /// 写入 AppTicket / ETicket，并用本次导入的新值无条件覆盖旧值。
    /// 支持补偿式回滚：写入过程中任一值失败或回读不一致时，恢复原值（原值不存在则删除）。
    /// </summary>
    public static (bool Ok, string? Error, string? Warning) WriteTickets(
        uint appId, byte[] appTicket, byte[] eticket, ulong ticketSteamId)
    {
        if (!IsSupported)
            return (false, L.Get("Svc_CredNeed64"), null);

        // SteamID 一致性仅检查提示，不自动修改注册表 SteamID（与 Lua setAppTicket 行为一致）
        var warning = CheckSteamIdConsistency(appId, ticketSteamId);

        var existing = ReadExisting(appId);
        try
        {
            using var key = OpenAppsKey(appId, writable: true);
            if (key == null)
                return (false, L.Get("Svc_CredOpenFailed"), warning);

            key.SetValue("AppTicket", appTicket, RegistryValueKind.Binary);
            key.SetValue("ETicket", eticket, RegistryValueKind.Binary);

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

    private static string? CheckSteamIdConsistency(uint appId, ulong ticketSteamId)
    {
        if (ticketSteamId == 0) return null;
        var (exists, stored) = GetStoredSteamId(appId);
        if (!exists || stored == ticketSteamId) return null;

        return L.Get("Svc_CredSteamIdMismatch");
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

    /// <summary>AppTicket/ETicket 原值快照（用于回滚）。</summary>
    private sealed record ExistingValue(bool Exists, RegistryValueKind Kind, byte[]? Data);

    private static (ExistingValue AppTicket, ExistingValue ETicket) ReadExisting(uint appId)
    {
        using var key = OpenAppsKey(appId, writable: false);
        return (ReadValue(key, "AppTicket"), ReadValue(key, "ETicket"));
    }

    private static ExistingValue ReadValue(RegistryKey? key, string name)
    {
        if (key == null) return new ExistingValue(false, RegistryValueKind.Unknown, null);
        try
        {
            var kind = key.GetValueKind(name);
            if (kind == RegistryValueKind.None)
                return new ExistingValue(false, RegistryValueKind.Unknown, null);

            if (key.GetValue(name) is byte[] data)
                return new ExistingValue(true, kind, data);

            return new ExistingValue(true, kind, null);
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

    private static string? Rollback(uint appId, (ExistingValue AppTicket, ExistingValue ETicket) existing)
    {
        try
        {
            using var key = OpenAppsKey(appId, writable: true);
            if (key == null) return L.Get("Svc_CredOpenForRollback");

            RestoreOrDelete(key, "AppTicket", existing.AppTicket);
            RestoreOrDelete(key, "ETicket", existing.ETicket);
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
            try { key.DeleteValue(name, throwOnMissingValue: false); } catch { }
        }
    }

    private static void Log(string category, string message)
        => LogService.Info(category, message);
}
