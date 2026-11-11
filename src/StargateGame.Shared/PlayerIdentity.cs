using System.Security.Cryptography;
using System.Text;
namespace StargateGame.Shared;

public sealed record IdentityChallenge(Guid Universe, Guid Connection, byte[] Nonce, int Version);
public sealed class PlayerIdentity : IDisposable
{
    readonly ECDsa key;
    public string? TestProfile
    {
        get;
    }
    public byte[] PublicKey => key.ExportSubjectPublicKeyInfo();
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(PublicKey));
    public PlayerIdentity(string? testProfile = null)
    {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TestProfile = testProfile;
    }
    public PlayerIdentity(byte[] privateKey, string? testProfile = null) : this(testProfile)
    {
        key.ImportPkcs8PrivateKey(privateKey, out int read);
        if (read != privateKey.Length || key.KeySize != 256)
            throw new CryptographicException("Invalid device key.");
    }
    public byte[] ExportPrivateKey() => key.ExportPkcs8PrivateKey();
    public static byte[] Payload(IdentityChallenge c) => Encoding.UTF8.GetBytes($"StargateGame/identity/v1/{c.Version}/{c.Universe:D}/{c.Connection:D}/{Convert.ToHexString(c.Nonce)}");
    public byte[] Sign(IdentityChallenge c)
    {
        if (c.Version != Protocol.Version || c.Connection == Guid.Empty || c.Nonce.Length != 32)
            throw new InvalidDataException("Invalid identity challenge.");
        return key.SignData(Payload(c), HashAlgorithmName.SHA256);
    }
    public static bool Verify(byte[] publicKey, IdentityChallenge c, byte[] signature)
    {
        if (publicKey.Length > 128 || signature.Length != 64 || c.Nonce.Length != 32 || c.Version != Protocol.Version)
            return false;
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out int read);
            return read == publicKey.Length && verifier.KeySize == 256 && verifier.ExportParameters(false).Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value && verifier.VerifyData(Payload(c), signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
    public void Dispose() => key.Dispose();
}
