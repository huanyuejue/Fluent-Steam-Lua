using System.Runtime.InteropServices;
using System.Text;

namespace SteamLuaManager.Services;

// 我拿 DPAPI（当前 Windows 用户隔离）存取敏感字符串，refresh token 这类凭证只存密文。
// 换机器/换系统用户就解不开，这是系统级保证，不是我加的限制。
public static class SecureTokenStorage
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>DPAPI 加密，entropy 按账号隔离。返回 base64，失败返回 null。</summary>
    public static string? Protect(string plainText, string entropyText)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(plainText)) return null;
        var data = Encoding.UTF8.GetBytes(plainText);
        var entropy = Encoding.UTF8.GetBytes(entropyText);
        IntPtr pIn = IntPtr.Zero, pEnt = IntPtr.Zero;
        DataBlob outBlob = default;
        try
        {
            pIn = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, pIn, data.Length);
            pEnt = Marshal.AllocHGlobal(entropy.Length);
            Marshal.Copy(entropy, 0, pEnt, entropy.Length);
            var inBlob = new DataBlob { cbData = data.Length, pbData = pIn };
            var entBlob = new DataBlob { cbData = entropy.Length, pbData = pEnt };
            if (!CryptProtectData(ref inBlob, null, ref entBlob,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                return null;
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return Convert.ToBase64String(result);
        }
        catch { return null; }
        finally
        {
            if (pIn != IntPtr.Zero) Marshal.FreeHGlobal(pIn);
            if (pEnt != IntPtr.Zero) Marshal.FreeHGlobal(pEnt);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    /// <summary>DPAPI 解密，entropy 必须与加密时一致。失败返回 null。</summary>
    public static string? Unprotect(string protectedBase64, string entropyText)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(protectedBase64)) return null;
        byte[] data;
        try { data = Convert.FromBase64String(protectedBase64); }
        catch { return null; }
        var decrypted = UnprotectBytes(data, Encoding.UTF8.GetBytes(entropyText));
        return decrypted == null ? null : Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>解密 Steam 客户端 local.vdf 里 ConnectCache 的原始字节（hex 解码后的输入）。</summary>
    public static byte[]? UnprotectBytes(byte[] data, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows() || data.Length == 0) return null;
        IntPtr pIn = IntPtr.Zero, pEnt = IntPtr.Zero;
        DataBlob outBlob = default;
        try
        {
            pIn = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, pIn, data.Length);
            pEnt = Marshal.AllocHGlobal(entropy.Length);
            Marshal.Copy(entropy, 0, pEnt, entropy.Length);
            var inBlob = new DataBlob { cbData = data.Length, pbData = pIn };
            var entBlob = new DataBlob { cbData = entropy.Length, pbData = pEnt };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                return null;
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        catch { return null; }
        finally
        {
            if (pIn != IntPtr.Zero) Marshal.FreeHGlobal(pIn);
            if (pEnt != IntPtr.Zero) Marshal.FreeHGlobal(pEnt);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
