using System.IO;
using System.Text;

namespace SteamLuaManager.Services;

// 按上游 companion 与 DLL 的约定存取凭证文件：JSON 明文经 DPAPI（当前用户、无附加熵）
// 加密后原子落盘；读到遗留明文（首字节 '{'）直接沿用。加解密已收敛到 SecureTokenStorage
// 的无熵口（同一语义，旧文件可直接读）；有熵口专供 Steam 登录链路，双方互不串用。
internal static class CloudCredentialStore
{
    public static string? ReadJson(string path)
    {
        byte[] raw;
        try
        {
            if (!File.Exists(path)) return null;
            raw = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            // 只记路径不记内容；文件不存在走正常 null 分支，不记（防刷屏）
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Svc_CcsReadFailed", path, ex.Message));
            return null;
        }
        if (raw.Length == 0) return null;
        if (raw[0] == (byte)'{')
            return Encoding.UTF8.GetString(raw);
        try
        {
            var plain = Unprotect(raw);
            return plain == null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            // DPAPI 换机/换用户解不开从此有明确信号，凭此提示用户重登录
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Svc_CcsDecryptFailed", path, ex.Message));
            return null;
        }
    }

    public static bool WriteJson(string path, string json)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var blob = Protect(Encoding.UTF8.GetBytes(json));
            if (blob == null) return false;
            AtomicWriteAllBytes(path, blob);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Warn(L.Get("LogCat_Cloud"), L.Get("Svc_CcsWriteFailed", path, ex.Message));
            return false;
        }
    }

    // 原子落盘：固定 ".new" 临时名会被并发写互覆盖丢键，Guid 隔离；
    // 顺手清理历史遗留的 ".new"（旧版本崩溃残留）
    public static void AtomicWriteAllText(string path, string content)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (File.Exists(path + ".new")) File.Delete(path + ".new"); } catch { }
        }
    }

    public static void AtomicWriteAllBytes(string path, byte[] data)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (File.Exists(path + ".new")) File.Delete(path + ".new"); } catch { }
        }
    }

    // 加解密收敛到 SecureTokenStorage 的无熵口：同一 DPAPI 语义，旧文件可直接读；
    // 有熵口（Steam 登录链路）纹丝不动
    private static byte[]? Protect(byte[] data) => SecureTokenStorage.ProtectBytesNoEntropy(data);

    private static byte[]? Unprotect(byte[] data) => SecureTokenStorage.UnprotectBytesNoEntropy(data);
}
