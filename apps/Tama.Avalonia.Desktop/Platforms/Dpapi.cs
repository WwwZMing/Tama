using System.Runtime.InteropServices;

namespace Tama.Avalonia.Desktop.Platforms;

/// <summary>
/// DPAPI（crypt32.dll）P/Invoke。
///
/// 为什么不引 <c>System.Security.Cryptography.ProtectedData</c> 包：只有两个函数用得上，
/// 直接调 crypt32 省一个依赖（Android 头也不会被它拖下水）。
/// CurrentUser 范围 = 只有同一 Windows 用户能解出密钥。
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [Flags]
    private enum CryptProtectFlags
    {
        None = 0,
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, CryptProtectFlags dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, CryptProtectFlags dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = ptr };
    }

    private static byte[] FromBlob(DATA_BLOB blob)
    {
        var result = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        LocalFree(blob.pbData);
        return result;
    }

    public static byte[] Protect(byte[] data)
    {
        var input = ToBlob(data);
        try
        {
            if (!CryptProtectData(ref input, "Tama biometric key", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectFlags.None, out var output))
                throw Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error())!;
            return FromBlob(output);
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }

    public static byte[] Unprotect(byte[] data)
    {
        var input = ToBlob(data);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectFlags.None, out var output))
                throw Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error())!;
            return FromBlob(output);
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }
}
