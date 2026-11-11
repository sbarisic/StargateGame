using System.Diagnostics;
using System.Numerics;
using StargateGame.Shared;
using StargateGame.WorldGen;
using StargateGame.Building;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    public sealed class DebugTile
    {
        public readonly List<DebugLine> Lines = [];
        public IEnumerator<DebugLine>? Pending;
        public TerrainChunkData? Chunk;
        public Vector3 Min = new(float.PositiveInfinity), Max = new(float.NegativeInfinity);
    }
    readonly Dictionary<(int X, int Z), DebugTile> debugTiles = [];
    WorldChanges? debugChanges;
    public bool DebugGeometryIncomplete
    {
        get;
        private set;
    }
    public double DebugPreparationMilliseconds
    {
        get;
        private set;
    }
    IEnumerable<DebugLine> CachedDebug(Vector3 eye, Matrix4x4? viewProjection)
    {
        foreach (var tile in DebugBatches(eye, viewProjection))
            foreach (var line in tile.Lines)
                if (Vector3.DistanceSquared(line.A, eye) < 400)
                    yield return line;
    }
    public IEnumerable<DebugTile> DebugBatches(Vector3 eye, Matrix4x4? viewProjection = null)
    {
        if (!ReferenceEquals(debugChanges, Changes))
        {
            foreach (var tile in debugTiles.Values)
                tile.Pending?.Dispose();
            debugTiles.Clear();
            debugChanges = Changes;
        }
        int cx = (int)MathF.Floor(eye.X / 8), cz = (int)MathF.Floor(eye.Z / 8);
        foreach (var key in debugTiles.Keys.Where(k => Math.Abs(k.X - cx) > 4 || Math.Abs(k.Z - cz) > 4).ToArray())
        {
            debugTiles[key].Pending?.Dispose();
            debugTiles.Remove(key);
        }
        var watch = Stopwatch.StartNew();
        DebugGeometryIncomplete = false;
        for (int z = cz - 3; z <= cz + 3; z++)
            for (int x = cx - 3; x <= cx + 3; x++)
            {
                var nearest = new Vector3(Math.Clamp(eye.X, x * 8, x * 8 + 8), eye.Y, Math.Clamp(eye.Z, z * 8, z * 8 + 8));
                if (Vector3.DistanceSquared(nearest, eye) > 400)
                    continue;
                var coord = ChunkCoordinate.At(x * 8 + .1f, z * 8 + .1f);
                TerrainChunkData? chunk = null;
                if (terrainSource is not null && (!terrainBodies.ContainsKey(coord) || !terrainSource.TryGetChunk(coord, out chunk)))
                {
                    if (debugTiles.Remove((x, z), out var old))
                        old.Pending?.Dispose();
                    continue;
                }
                var key = (x, z);
                if (!debugTiles.TryGetValue(key, out var tile) || !ReferenceEquals(tile.Chunk, chunk))
                {
                    tile?.Pending?.Dispose();
                    tile = new()
                    {
                        Chunk = chunk,
                        Pending = BuildDebugTile(x, z, chunk).GetEnumerator()
                    };
                    debugTiles[key] = tile;
                }
                while (tile.Pending is not null && watch.Elapsed.TotalMilliseconds < .5)
                {
                    if (!tile.Pending.MoveNext())
                    {
                        tile.Pending.Dispose();
                        tile.Pending = null;
                        break;
                    }
                    var l = tile.Pending.Current;
                    tile.Lines.Add(l);
                    tile.Min = Vector3.Min(tile.Min, Vector3.Min(l.A, l.B));
                    tile.Max = Vector3.Max(tile.Max, Vector3.Max(l.A, l.B));
                }
                DebugGeometryIncomplete |= tile.Pending is not null;
            }
        DebugPreparationMilliseconds = watch.Elapsed.TotalMilliseconds;
        foreach (var tile in debugTiles.Values)
        {
            if (tile.Lines.Count == 0 || Vector3.DistanceSquared(Vector3.Clamp(eye, tile.Min, tile.Max), eye) > 400)
                continue;
            if (viewProjection is { } vp && !DebugVisible(tile.Min, tile.Max, vp))
                continue;
            yield return tile;
        }
    }
    static bool DebugVisible(Vector3 min, Vector3 max, Matrix4x4 vp)
    {
        int common = 63;
        for (int i = 0; i < 8; i++)
        {
            var p = Vector4.Transform(new Vector4((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z, 1), vp);
            int code = (p.X < -p.W ? 1 : 0) | (p.X > p.W ? 2 : 0) | (p.Y < -p.W ? 4 : 0) | (p.Y > p.W ? 8 : 0) | (p.Z < -p.W ? 16 : 0) | (p.Z > p.W ? 32 : 0);
            common &= code;
        }
        return common == 0;
    }
    IEnumerable<DebugLine> BuildDebugTile(int x, int z, TerrainChunkData? chunk)
    {
        bool OwnPoint(Vector3 p) => (int)MathF.Floor(p.X / 8) == x && (int)MathF.Floor(p.Z / 8) == z;
        foreach (var line in staticLines)
            if (OwnPoint(line.A))
                yield return line;
        foreach (var piece in Changes.Pieces)
        {
            if (!OwnPoint(BuildingGeometry.WorldPoint(Changes.Structures.Single(s => s.Id == piece.StructureId), piece.Point, piece.Level)))
                continue;
            foreach (var t in BuildingGeometry.Mesh(Changes, piece))
            {
                yield return new(t.A, t.B, new(.7f, .7f, 1));
                yield return new(t.B, t.C, new(.7f, .7f, 1));
            }
        }
        if (chunk is null)
            yield break;
        int ox = x * 8 - chunk.Coordinate.X * 64, oz = z * 8 - chunk.Coordinate.Z * 64;
        for (int iz = oz; iz < oz + 8; iz++)
            for (int ix = ox; ix < ox + 8; ix++)
            {
                var p = chunk.Point(ix, iz);
                yield return new(p, chunk.Point(ix + 1, iz), new(.3f, .8f, .8f));
                yield return new(p, chunk.Point(ix, iz + 1), new(.3f, .8f, .8f));
                yield return new(chunk.Point(ix + 1, iz), chunk.Point(ix, iz + 1), new(.2f, .5f, .5f));
            }
        foreach (var tree in TreeScenery.Resolve(chunk, World.Definition.Preset, World.Definition.Fixture))
        {
            if (!OwnPoint(tree.Transform.Translation) || Changes.IsRemoved("tree", tree.Id))
                continue;
            foreach (var t in treeMeshes.Value[tree.Model])
                yield return new(Vector3.Transform(t.A, tree.Transform), Vector3.Transform(t.B, tree.Transform), new(.2f, .9f, .3f));
        }
        foreach (var rock in RockScenery.Resolve(chunk, World.Definition.Preset))
        {
            if (!rock.Collidable || !OwnPoint(rock.Transform.Translation) || Changes.IsRemoved("rock", rock.Id))
                continue;
            foreach (var t in rockMeshes.Value[rock.Model])
                yield return new(Vector3.Transform(t.A, rock.Transform), Vector3.Transform(t.B, rock.Transform), new(.9f, .5f, .2f));
        }
    }
}
