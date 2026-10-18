using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AC.Infrastructure;

/// <summary>
/// Шифрование API-ключа средствами Windows (DPAPI, область текущего пользователя).
/// Расшифровать может только этот пользователь на этом компьютере.
/// Сделано через P/Invoke, чтобы не тянуть NuGet-пакет ProtectedData.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Dpapi
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AchievementCompanion.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(Run(data, protect: true));
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return "";
        try
        {
            var data = Convert.FromBase64String(protectedBase64);
            return Encoding.UTF8.GetString(Run(data, protect: false));
        }
        catch (Exception e) when (e is FormatException or CryptographicFailure)
        {
            return "";
        }
    }

    private sealed class CryptographicFailure(string message) : Exception(message);

    private static byte[] Run(byte[] input, bool protect)
    {
        var inBlob = new DataBlob();
        var entropyBlob = new DataBlob();
        var outBlob = new DataBlob();
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = input.Length;
            inBlob.pbData = inHandle.AddrOfPinnedObject();
            entropyBlob.cbData = Entropy.Length;
            entropyBlob.pbData = entHandle.AddrOfPinnedObject();

            bool ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, ref outBlob);
            if (!ok) throw new CryptographicFailure($"DPAPI ошибка {Marshal.GetLastWin32Error()}");

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            inHandle.Free();
            entHandle.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private const int CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
