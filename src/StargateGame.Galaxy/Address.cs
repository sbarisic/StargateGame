using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StargateGame.Galaxy;

public readonly record struct Position(double X, double Y, double Z)
{
    public static Position operator +(Position a, Position b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Position operator -(Position a, Position b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Position operator *(Position a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    public double LengthSquared => Dot(this, this);
    public bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public static double Dot(Position a, Position b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Position Cross(Position a, Position b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
public sealed record NamedRoute(string Record, string Name, int[] SourceSequence, int[] GameSequence, bool GameAuthored, bool Intergalactic)
{
    public ulong Key => Address.Canonical(GameSequence);
    public bool DhdCompatible => !Intergalactic && !GameSequence.Contains(13);
}
public sealed record ReferenceData(string Version, Position[] References, NamedRoute[] Routes);
public readonly record struct AddressSolution(ulong Key, Position Position, double Determinant);
public static class Address
{
    public const string Version = "rda-compatible-fit-20261106-v1";
    public const int ValidCount = 19_020_803, PairingCount = 41_410_215;
    public const double AcquisitionRadius = 1, MinimumDeterminant = .025;
    public static ReferenceData Data
    {
        get;
    } = Load();
    static ReferenceData Load()
    {
        using var stream = typeof(Address).Assembly.GetManifestResourceStream("StargateGame.Galaxy.references.json")!;
        var data = JsonSerializer.Deserialize<ReferenceData>(stream)!;
        if (data.Version != Version || data.References.Length != 38 || data.Routes.Count(r => !r.Intergalactic) != 35)
            throw new InvalidDataException("Invalid adopted reference data.");
        return data;
    }
    public static ulong Canonical(ReadOnlySpan<int> symbols)
    {
        if (symbols.Length != 6)
            throw new ArgumentException("Ordinary addresses require six destination glyphs; intergalactic routes are unsupported.");
        ulong seen = 0;
        Span<int> pairs = stackalloc int[3];
        for (int i = 0; i < 6; i++)
        {
            int n = symbols[i];
            if (n is < 2 or > 39)
                throw new ArgumentException("Origin is not a destination coordinate.");
            if ((seen & (1UL << n)) != 0)
                throw new ArgumentException("Destination glyphs must be distinct.");
            seen |= 1UL << n;
        }
        for (int i = 0; i < 3; i++)
        {
            int a = symbols[i * 2], b = symbols[i * 2 + 1];
            pairs[i] = (Math.Min(a, b) << 6) | Math.Max(a, b);
        }
        pairs.Sort();
        return ((ulong)pairs[0] << 24) | ((ulong)pairs[1] << 12) | (uint)pairs[2];
    }
    public static int[] Symbols(ulong key) => Enumerable.Range(0, 6).Select(i => (int)((key >> ((5 - i) * 6)) & 63)).ToArray();
    public static bool DhdCompatible(ulong key) => !Symbols(key).Contains(13);
    public readonly record struct Plane(int A, int B, Position Normal, double Offset)
    {
        public ulong Mask => (1UL << A) | (1UL << B);
        public ulong Packed => (ulong)((A << 6) | B);
    }
    public static Plane[] Planes()
    {
        List<Plane> result = [];
        for (int a = 2; a <= 39; a++)
            for (int b = a + 1; b <= 39; b++)
            {
                var p = Data.References[a - 2] * 1000;
                var q = Data.References[b - 2] * 1000;
                var d = q - p;
                double length = Math.Sqrt(d.LengthSquared);
                result.Add(new(a, b, d * (1 / length), (q.LengthSquared - p.LengthSquared) / (2 * length)));
            }
        return result.ToArray();
    }
    public static bool Solve(Plane a, Plane b, Plane c, out AddressSolution solution, out bool unstable)
    {
        var bc = Position.Cross(b.Normal, c.Normal);
        double det = Position.Dot(a.Normal, bc);
        solution = default;
        unstable = Math.Abs(det) < MinimumDeterminant;
        if (unstable)
            return false;
        var p = (bc * a.Offset + Position.Cross(c.Normal, a.Normal) * b.Offset + Position.Cross(a.Normal, b.Normal) * c.Offset) * (1 / det);
        if (!p.Finite || Math.Abs(Position.Dot(a.Normal, p) - a.Offset) > 1e-6 || Math.Abs(Position.Dot(b.Normal, p) - b.Offset) > 1e-6 || Math.Abs(Position.Dot(c.Normal, p) - c.Offset) > 1e-6)
            throw new InvalidDataException("Address intersection failed numerical residual check.");
        if ((p.X * p.X + p.Y * p.Y) / 2_500_000_000 + p.Z * p.Z / 64_000_000 > 1)
            return false;
        solution = new((a.Packed << 24) | (b.Packed << 12) | c.Packed, p, det);
        return true;
    }
    public static AddressSolution Resolve(ReadOnlySpan<int> symbols)
    {
        ulong key = Canonical(symbols);
        var canonical = Symbols(key);
        var planes = Planes();
        Plane Get(int i) => planes.First(p => p.A == canonical[i] && p.B == canonical[i + 1]);
        if (!Solve(Get(0), Get(2), Get(4), out var result, out var unstable))
            throw new ArgumentException(unstable ? "Address planes are numerically unstable." : "Address is outside the galactic boundary.");
        return result;
    }
    public static bool Acquired(Position corrected, Position retained) => corrected.Finite && retained.Finite && (corrected - retained).LengthSquared <= 1;
    public static Guid Identity(Guid universe, ulong key, string kind)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"galaxy:1:{universe:D}:{key:x9}:{kind}"));
        bytes[7] = (byte)((bytes[7] & 15) | 0x80);
        bytes[8] = (byte)((bytes[8] & 63) | 128);
        return new Guid(bytes.AsSpan(0, 16));
    }
    public static int ContentSeed(long universeSeed, Guid location) => BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"planet:1:{universeSeed}:{location:D}"))));
}
