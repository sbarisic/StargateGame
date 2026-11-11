using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using StargateGame.Shared;

namespace StargateGame.WorldGen;

// Render-only data: no canonical heights, water queries, or collision arrays.
public sealed record FarTile(int Version, ChunkCoordinate Coordinate, TerrainMeshData Mesh, RockInstance[] Rocks, TreeInstance[] Trees)
{
    public int Level { get; init; }
    public FarTileKey Key => new(Coordinate, Level);
    public const int MaxBytes = 4 * 1024 * 1024, MaxDecodedBytes = 16 * 1024 * 1024;
    float? centreHeight;
    public float CentreHeight => centreHeight ??= (Mesh.Positions.MinBy(p => Vector2.DistanceSquared(new(p.X, p.Z), new(Coordinate.X * 64 + 32, Coordinate.Z * 64 + 32))).Y);
    public long MemoryBytes => (long)Mesh.Positions.Length * 64 + (long)Mesh.WaterPositions.Length * 24 + (Rocks.Length + Trees.Length) * 192;
    public static FarTile Build(TerrainChunkData data, string preset, string? fixture = null) => new(2, data.Coordinate, TerrainMesher.Build(data, 8), RockScenery.Resolve(data, preset).ToArray(), TreeScenery.Resolve(data, preset, fixture).ToArray());
    sealed record Scenery(RockInstance[] Rocks, TreeInstance[] Trees);
    public byte[] Encode()
    {
        using var output = new MemoryStream();
        using (var zip = new BrotliStream(output, CompressionLevel.Optimal, true))
        using (var w = new BinaryWriter(zip))
        {
            w.Write(0x33464753);
            w.Write(Coordinate.X);
            w.Write(Coordinate.Z);
            w.Write(Level);
            var indices = new int[Mesh.Positions.Length];
            var unique = new Dictionary<(Vector3, Vector3, Vector4), int>();
            for (int i = 0; i < indices.Length; i++)
            {
                var vertex = (Mesh.Positions[i], Mesh.Normals[i], Mesh.Materials[i]);
                if (!unique.TryGetValue(vertex, out int index))
                {
                    index = unique.Count;
                    unique.Add(vertex, index);
                }
                indices[i] = index;
            }
            w.Write(unique.Count);
            foreach (var (p, n, m) in unique.Keys)
            {
                Write(w, p);
                w.Write((short)MathF.Round(n.X * 32767));
                w.Write((short)MathF.Round(n.Y * 32767));
                w.Write((short)MathF.Round(n.Z * 32767));
                w.Write((byte)MathF.Round(m.X * 255));
                w.Write((byte)MathF.Round(m.Y * 255));
                w.Write((byte)MathF.Round(m.Z * 255));
                w.Write((byte)MathF.Round(m.W * 255));
            }
            w.Write(indices.Length);
            foreach (int index in indices)
            {
                if (unique.Count <= 65535)
                    w.Write((ushort)index);
                else
                    w.Write(index);
            }
            var water = new Dictionary<(Vector3, Vector2), int>();
            var waterIndices = new int[Mesh.WaterPositions.Length];
            for (int i = 0; i < waterIndices.Length; i++)
            {
                var vertex = (Mesh.WaterPositions[i], Mesh.WaterFlow[i]);
                if (!water.TryGetValue(vertex, out int index))
                {
                    index = water.Count;
                    water.Add(vertex, index);
                }
                waterIndices[i] = index;
            }
            w.Write(water.Count);
            foreach (var (p, f) in water.Keys)
            {
                Write(w, p);
                w.Write(f.X);
                w.Write(f.Y);
            }
            w.Write(waterIndices.Length);
            foreach (int index in waterIndices)
                w.Write(index);
            var depths = TerrainMesher.WaterAttributes(Mesh);
            w.Write(depths.Length); foreach(var d in depths) w.Write(d.X);
            w.Write(Mesh.Ecology.Length); foreach(var e in Mesh.Ecology) { w.Write(e.X);w.Write(e.Y); }
            w.Write(Mesh.Landscape.Length); foreach(var e in Mesh.Landscape) {w.Write(e.X);w.Write(e.Y);w.Write(e.Z);w.Write(e.W);}
            var scenery = JsonSerializer.SerializeToUtf8Bytes(new Scenery(Rocks, Trees), Protocol.Json);
            w.Write(scenery.Length);
            w.Write(scenery);
        }
        if (output.Length > MaxBytes)
            throw new InvalidDataException("Far tile exceeds transfer limit.");
        return output.ToArray();
    }
    static void Write(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }
    static Vector3 ReadVector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    static int Count(BinaryReader r, int maximum = 100000)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > maximum)
            throw new InvalidDataException("Invalid far tile count.");
        return n;
    }
    public static FarTile Decode(byte[] bytes, ChunkCoordinate expected)
    {
        if (bytes.Length > MaxBytes)
            throw new InvalidDataException("Far tile exceeds transfer limit.");
        using var zip = new BrotliStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        byte[] buffer = new byte[16384];
        int read;
        while ((read = zip.Read(buffer)) > 0)
        {
            if (decoded.Length + read > MaxDecodedBytes)
                throw new InvalidDataException("Far tile expands beyond limit.");
            decoded.Write(buffer, 0, read);
        }
        decoded.Position = 0;
        using var r = new BinaryReader(decoded);
        if (r.ReadInt32() != 0x33464753)
            throw new InvalidDataException("Unknown far tile format.");
        var coordinate = new ChunkCoordinate(r.ReadInt32(), r.ReadInt32());
        int level = r.ReadInt32();
        var vertices = new (Vector3 P, Vector3 N, Vector4 M)[Count(r)];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = (ReadVector(r), new(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f), new(r.ReadByte() / 255f, r.ReadByte() / 255f, r.ReadByte() / 255f, r.ReadByte() / 255f));
        int count = Count(r);
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var materials = new Vector4[count];
        for (int i = 0; i < count; i++)
        {
            int index = vertices.Length <= 65535 ? r.ReadUInt16() : r.ReadInt32();
            if ((uint)index >= vertices.Length)
                throw new InvalidDataException("Invalid far vertex index.");
            (positions[i], normals[i], materials[i]) = vertices[index];
        }
        var waterVertices = new (Vector3 P, Vector2 F)[Count(r)];
        for (int i = 0; i < waterVertices.Length; i++)
            waterVertices[i] = (ReadVector(r), new(r.ReadSingle(), r.ReadSingle()));
        count = Count(r);
        var waterPositions = new Vector3[count];
        var flow = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int index = r.ReadInt32();
            if ((uint)index >= waterVertices.Length)
                throw new InvalidDataException("Invalid far water index.");
            (waterPositions[i], flow[i]) = waterVertices[index];
        }
        float[] depths = new float[Count(r)]; for(int i=0;i<depths.Length;i++) depths[i]=r.ReadSingle();
        var ecology=new Vector2[Count(r)];for(int i=0;i<ecology.Length;i++)ecology[i]=new(r.ReadSingle(),r.ReadSingle());
        var landscape=new Vector4[Count(r)];for(int i=0;i<landscape.Length;i++)landscape[i]=new(r.ReadSingle(),r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
        int sceneryBytes = Count(r, 1024 * 1024);
        if (decoded.Length - decoded.Position != sceneryBytes)
            throw new InvalidDataException("Invalid far scenery length.");
        var scenery = JsonSerializer.Deserialize<Scenery>(r.ReadBytes(sceneryBytes), Protocol.Json) ?? throw new InvalidDataException("Missing far scenery.");
        var tile = new FarTile(2, coordinate, new(positions, normals, materials, waterPositions)
        {
            WaterFlow = flow, WaterDepth = depths, Ecology=ecology, Landscape=landscape
        }, scenery.Rocks, scenery.Trees) { Level = level };
        var m = tile.Mesh;
        static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) && Math.Abs(p.X) <= 2048 && Math.Abs(p.Y) <= 4096 && Math.Abs(p.Z) <= 2048;
        if ((ecology.Length != 0 && ecology.Length != positions.Length) || (landscape.Length != 0 && landscape.Length != positions.Length) || ecology.Any(e=>!float.IsFinite(e.X+e.Y)) || landscape.Any(e=>!float.IsFinite(e.X+e.Y+e.Z+e.W)) || tile.Version != 2 || !tile.Key.Valid || depths.Length != waterPositions.Length || depths.Any(d => !float.IsFinite(d) || d < 0 || d > 4096) || tile.Coordinate != expected || !expected.Valid || m is null || m.Positions is null || m.Normals is null || m.Materials is null || m.WaterPositions is null || m.WaterFlow is null || tile.Rocks is null || tile.Trees is null ||
            m.Positions.Length > 100000 || m.Positions.Length % 3 != 0 || m.Normals.Length != m.Positions.Length || m.Materials.Length != m.Positions.Length || m.WaterPositions.Length > 100000 || m.WaterPositions.Length % 3 != 0 || m.WaterFlow.Length != m.WaterPositions.Length ||
            tile.Rocks.Length > 512 || tile.Trees.Length > 512 || m.Positions.Any(p => !Finite(p)) || m.Normals.Any(p => !Finite(p)) || m.WaterPositions.Any(p => !Finite(p)))
            throw new InvalidDataException("Invalid far tile.");
        static bool MatrixFinite(Matrix4x4 t) => float.IsFinite(t.GetDeterminant()) && t.M14 == 0 && t.M24 == 0 && t.M34 == 0 && t.M44 == 1 && Finite(t.Translation);
        if (m.Materials.Any(v => !float.IsFinite(v.X + v.Y + v.Z + v.W) || Vector4.Min(v, Vector4.Zero) != Vector4.Zero || Vector4.Max(v, Vector4.One) != Vector4.One) || m.WaterFlow.Any(v => !float.IsFinite(v.X + v.Y)) ||
            tile.Rocks.Any(r => r is null || !RockScenery.Models.Contains(r.Model) || !Finite(r.Min) || !Finite(r.Max) || !MatrixFinite(r.Transform)) ||
            tile.Trees.Any(t => t is null || !TreeScenery.Models.Contains(t.Model) || !Finite(t.Min) || !Finite(t.Max) || !MatrixFinite(t.Transform)))
            throw new InvalidDataException("Invalid far tile material or scenery.");
        return tile;
    }
}
