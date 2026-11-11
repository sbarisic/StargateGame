using System.Security.Cryptography;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    byte[] ReadRuntimeAsset(string key)
    {
        MountedAssetStore.ValidateKey(key);
        // SGA1 carried only models; its remaining content still lives beside it.
        // A category catalog is authoritative, including missing/tombstoned keys.
        return modelAssets.Store?.HasCatalog == true || modelAssets.Exists(key)
            ? modelAssets.Read(key) : File.ReadAllBytes(Asset(key));
    }

    string AtmosphereSourceHash()
    {
        IEnumerable<string> keys = modelAssets.Store?.HasCatalog == true
            ? modelAssets.Store.Current.Blocks.Keys.Where(k => k.StartsWith("shaders/atmosphere", StringComparison.Ordinal)
                || k.StartsWith("shaders/bruneton/", StringComparison.Ordinal) && k.EndsWith(".glsl", StringComparison.Ordinal))
            : Directory.GetFiles(Asset("shaders"), "atmosphere*")
                .Concat(Directory.GetFiles(Asset("shaders", "bruneton"), "*.glsl"))
                .Select(p => Path.GetRelativePath(Asset(), p).Replace('\\', '/'));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string key in keys.Order(StringComparer.Ordinal)) hash.AppendData(ReadRuntimeAsset(key));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
