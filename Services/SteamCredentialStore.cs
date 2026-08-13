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
            return (false, "当前进程不是 64 位，无法写入 Steam 凭据注册表", null);

        // SteamID 一致性仅检查提示，不自动修改注册表 SteamID（与 Lua setAppTicket 行为一致）
        var warning = CheckSteamIdConsistency(appId, ticketSteamId);

        var existing = ReadExisting(appId);
        try
        {
            using var key = OpenAppsKey(appId, writable: true);
            if (key == null)
                return (false, "无法打开注册表键 Software\\Valve\\Steam\\Apps（请确认权限）", warning);

            key.SetValue("AppTicket", appTicket, RegistryValueKind.Binary);
            key.SetValue("ETicket", eticket, RegistryValueKind.Binary);

            var verificationError = VerifyWrite(key, appTicket, eticket);
            if (verificationError == null)
            {
                Log("reg", $"AppID {appId} 票据已写入注册表并回读一致（AppTicket {appTicket.Length}B / ETicket {eticket.Length}B）");
                return (true, null, warning);
            }

            var rollbackError = Rollback(appId, existing);
            return rollbackError == null
                ? (false, $"写入校验失败：{verificationError}（原值已恢复）", warning)
                : (false, $"写入校验失败：{verificationError}；且原值恢复失败（{rollbackError}），注册表可能处于部分更新状态，请手动检查",
                    warning);
        }
        catch (Exception ex)
        {
            var rollbackError = Rollback(appId, existing);
            Log("reg", $"写注册表失败：{ex.Message}；原值恢复：{(rollbackError == null ? "成功" : "失败(" + rollbackError + ")")}");
            return rollbackError == null
                ? (false, $"写入注册表失败：{ex.Message}（原值已恢复）", warning)
                : (false, $"写入注册表失败：{ex.Message}；且原值恢复失败（{rollbackError}），请手动检查", warning);
        }
    }

    private static string? CheckSteamIdConsistency(uint appId, ulong ticketSteamId)
    {
        if (ticketSteamId == 0) return null;
        var (exists, stored) = GetStoredSteamId(appId);
        if (!exists || stored == ticketSteamId) return null;

        return "注册表中已有 SteamID 与本次 AppTicket 内嵌 SteamID 不一致。OpenSteamTool 会优先使用注册表 SteamID，授权可能失败。当前仅覆盖 AppTicket/ETicket，未修改注册表 SteamID。";
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
            return "AppTicket 类型不是 REG_BINARY";
        if (key.GetValueKind("ETicket") != RegistryValueKind.Binary)
            return "ETicket 类型不是 REG_BINARY";

        if (key.GetValue("AppTicket") is not byte[] readApp || !readApp.AsSpan().SequenceEqual(appTicket))
            return "回读 AppTicket 与写入内容不一致";
        if (key.GetValue("ETicket") is not byte[] readE || !readE.AsSpan().SequenceEqual(eticket))
            return "回读 ETicket 与写入内容不一致";

        return null;
    }

    private static string? Rollback(uint appId, (ExistingValue AppTicket, ExistingValue ETicket) existing)
    {
        try
        {
            using var key = OpenAppsKey(appId, writable: true);
            if (key == null) return "无法打开注册表键以恢复原值";

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
