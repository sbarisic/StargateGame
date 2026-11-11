using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace StargateGame.Galaxy;

/// <summary>Sorted, fixed-size double precision records and a bounded uniform-grid index. No per-world heap objects.</summary>
public sealed class Catalogue : IDisposable
{
    public const string AdoptedChecksum = "BCB668107F424CDA6E06987B718AA77936DA1B468F5FE15F9674ACE393F30650";
    const int Header = 256, RecordSize = 32, GridX = 128, GridY = 128, GridZ = 32, Cells = GridX * GridY * GridZ;
    const long RecordsEnd = Header + (long)Address.ValidCount * RecordSize;
    const long IndexStart = RecordsEnd + (Cells + 1L) * 4;
    const long FileLength = IndexStart + (long)Address.ValidCount * 4;
    readonly MemoryMappedFile file;
    readonly MemoryMappedViewAccessor view;
    public string Checksum
    {
        get;
    }
    public int Count => Address.ValidCount;
    public long Bytes => FileLength;
    public Catalogue(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
        {
            if (stream.Length != FileLength || reader.ReadString() != "SGGALAXY1" || reader.ReadString() != Address.Version || reader.ReadInt32() != Count)
                throw new InvalidDataException("Incompatible or incomplete galaxy catalogue. Rebuild explicitly; the saved universe has not been changed.");
            Checksum = Convert.ToHexString(reader.ReadBytes(32));
            stream.Position = Header;
            if (Checksum != AdoptedChecksum || Convert.ToHexString(SHA256.HashData(stream)) != Checksum)
                throw new InvalidDataException("Galaxy catalogue checksum mismatch.");
        }
        file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
    }
    public AddressSolution At(int index)
    {
        if ((uint)index >= Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        long p = Header + (long)index * RecordSize;
        return new(view.ReadUInt64(p), new(view.ReadDouble(p + 8), view.ReadDouble(p + 16), view.ReadDouble(p + 24)), 0);
    }
    public AddressSolution Find(ulong key)
    {
        int lo = 0, hi = Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            var r = At(mid);
            if (r.Key == key)
                return r;
            if (r.Key < key)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        throw new ArgumentException("Address has no provisioned destination.");
    }
    static int X(double x) => Math.Clamp((int)Math.Floor((x + 50000) / 100000 * GridX), 0, GridX - 1);
    static int Y(double y) => Math.Clamp((int)Math.Floor((y + 50000) / 100000 * GridY), 0, GridY - 1);
    static int Z(double z) => Math.Clamp((int)Math.Floor((z + 8000) / 16000 * GridZ), 0, GridZ - 1);
    static int Cell(Position p) => (Z(p.Z) * GridY + Y(p.Y)) * GridX + X(p.X);
    public IEnumerable<AddressSolution> Nearby(Position p)
    {
        if (!p.Finite)
            yield break;
        for (int z = Z(p.Z - 1); z <= Z(p.Z + 1); z++)
            for (int y = Y(p.Y - 1); y <= Y(p.Y + 1); y++)
                for (int x = X(p.X - 1); x <= X(p.X + 1); x++)
                {
                    int cell = (z * GridY + y) * GridX + x;
                    uint start = view.ReadUInt32(RecordsEnd + cell * 4L), end = view.ReadUInt32(RecordsEnd + (cell + 1L) * 4);
                    for (uint i = start; i < end; i++)
                    {
                        var entry = At((int)view.ReadUInt32(IndexStart + i * 4L));
                        if (Address.Acquired(p, entry.Position))
                            yield return entry;
                    }
                }
    }
    public ulong HomeKey()
    {
        var named = Address.Data.Routes.Where(r => !r.Intergalactic).Select(r => r.Key).ToHashSet();
        for (int i = 0; i < Count; i++)
        {
            ulong key = At(i).Key;
            if (Address.DhdCompatible(key) && !named.Contains(key))
                return key;
        }
        throw new InvalidDataException("No compatible home address.");
    }
    public void Dispose()
    {
        view.Dispose();
        file.Dispose();
    }

    public static void Build(string path, Action<string>? progress = null, CancellationToken cancellation = default)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Never publish a partial file; cancellation leaves the previous catalogue usable.
        string temporary = path + ".building-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 1024))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                stream.Position = Header;
                var planes = Address.Planes();
                int total = 0, valid = 0, unstable = 0;
                uint[] counts = new uint[Cells + 1];
                for (int i = 0; i < planes.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var a = planes[i];
                    for (int j = i + 1; j < planes.Length; j++)
                    {
                        var b = planes[j];
                        if ((a.Mask & b.Mask) != 0)
                            continue;
                        for (int k = j + 1; k < planes.Length; k++)
                        {
                            var c = planes[k];
                            if (((a.Mask | b.Mask) & c.Mask) != 0)
                                continue;
                            total++;
                            if (!Address.Solve(a, b, c, out var r, out bool bad))
                            {
                                if (bad)
                                    unstable++;
                                continue;
                            }
                            writer.Write(r.Key);
                            writer.Write(r.Position.X);
                            writer.Write(r.Position.Y);
                            writer.Write(r.Position.Z);
                            counts[Cell(r.Position) + 1]++;
                            valid++;
                        }
                    }
                    if (i % 16 == 0)
                        progress?.Invoke($"Catalogue: {total:N0}/{Address.PairingCount:N0} pairings, {valid:N0} valid");
                }
                if (total != Address.PairingCount || valid != Address.ValidCount || unstable != 1_585_194)
                    throw new InvalidDataException($"Catalogue census mismatch: total={total}, valid={valid}, unstable={unstable}.");
                for (int i = 1; i < counts.Length; i++)
                    counts[i] += counts[i - 1];
                foreach (uint n in counts)
                    writer.Write(n);
                progress?.Invoke("Building spatial index...");
                writer.Flush();
                uint[] offsets = (uint[])counts.Clone(), indices = new uint[valid];
                using var reader = new BinaryReader(stream, Encoding.UTF8, true);
                stream.Position = Header;
                for (int i = 0; i < valid; i++)
                {
                    if ((i & 65535) == 0)
                        cancellation.ThrowIfCancellationRequested();
                    reader.ReadUInt64();
                    var p = new Position(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
                    indices[offsets[Cell(p)]++] = (uint)i;
                }
                stream.Position = IndexStart;
                foreach (uint index in indices)
                    writer.Write(index);
                writer.Flush();
                cancellation.ThrowIfCancellationRequested();
                stream.Position = Header;
                byte[] checksum = SHA256.HashData(stream);
                stream.Position = 0;
                if (Convert.ToHexString(checksum) != AdoptedChecksum)
                    throw new InvalidDataException("Adopted catalogue geometry changed without a version change.");
                writer.Write("SGGALAXY1");
                writer.Write(Address.Version);
                writer.Write(valid);
                writer.Write(checksum);
                writer.Flush();
                stream.Flush(true);
                progress?.Invoke($"Complete: {valid:N0} worlds, {FileLength:N0} bytes, SHA256 {Convert.ToHexString(checksum)}");
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
