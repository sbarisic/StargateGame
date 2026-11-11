using System.Runtime.InteropServices;
using System.Security.Cryptography;
using StargateGame.Shared;
namespace StargateGame.Client;

static class DeviceIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    struct Blob
    {
        public int Size;
        public nint Data;
    }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);
    static byte[] Protect(byte[] data, bool encrypt)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            bool ok = encrypt ? CryptProtectData(ref input, "StargateGame device identity", 0, 0, 0, 1, out output) : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok)
                throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (int i = 0; i < input.Size; i++)
                Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != 0)
                LocalFree(output.Data);
        }
    }
    public static PlayerIdentity Load(string? profile)
    {
        if (string.IsNullOrEmpty(profile))
            profile = null;
        if (profile is not null && (profile.Length > 40 || profile.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
            throw new ArgumentException("Identity profile must contain 1-40 letters, digits, '-' or '_'.");
        string root = StargateGame.Shared.UserData.Root;
        string path = profile is null ? Path.Combine(root, "identity.dpapi") : Path.Combine(root, "TestIdentities", profile + ".dpapi");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var mutex = new Mutex(false, "Local\\StargateGameIdentity-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))));
        try
        {
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
            }
            if (!File.Exists(path))
            {
                using var identity = new PlayerIdentity(profile);
                byte[] secret = identity.ExportPrivateKey();
                try
                {
                    string temp = path + "." + Guid.NewGuid().ToString("N");
                    File.WriteAllBytes(temp, Protect(secret, true));
                    try
                    {
                        File.Move(temp, path, false);
                    }
                    catch (IOException) when (File.Exists(path))
                    {
                        File.Delete(temp);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            }
            byte[] bytes = Protect(File.ReadAllBytes(path), false);
            try
            {
                return new PlayerIdentity(bytes, profile);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
