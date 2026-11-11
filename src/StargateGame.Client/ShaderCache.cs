using System.Security.Cryptography;
using System.Text;
using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public bool ShaderCacheEnabled { get; set; } = true;
    string ShaderCachePath(string vertex, string fragment)
    {
        string identity = "shader-cache-v1\0" + graphics.ShaderCacheFingerprint + "\0" + vertex + "\0" + fragment;
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(StargateGame.Shared.UserData.Root, "shader-cache", hash + ".bin");
    }
    ShaderProgram? ReadShaderCache(string path)
    {
        if (!ShaderCacheEnabled) return null;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is < 37 or > 64 * 1024 * 1024) return null;
            byte[] data = File.ReadAllBytes(path);
            if (!SHA256.HashData(data.AsSpan(32)).AsSpan().SequenceEqual(data.AsSpan(0, 32))) return null;
            return Own(graphics.CreateShaderProgramBinary(data.AsSpan(32)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            StartupTrace.Mark("shader-cache-fallback", detail: error.Message);
            return null;
        }
    }
    void SaveShaderCache(string path, ShaderProgram program)
    {
        if (!ShaderCacheEnabled) return;
        byte[] data = program.GetBinary();
        if (data.Length == 0) return;
        _ = Task.Run(() =>
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var file = File.Create(temp)) { file.Write(SHA256.HashData(data)); file.Write(data); }
                File.Move(temp, path, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            finally { try { File.Delete(temp); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
        });
    }
}
