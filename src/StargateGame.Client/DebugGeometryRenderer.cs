using System.Diagnostics;
using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Physics;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public PhysicsWorld? DebugWorld
    {
        get;
        set;
    }
    PhysicsWorld? debugOwner;
    sealed record CachedLines(Mesh3D Mesh, int Count);
    readonly Dictionary<PhysicsWorld.DebugTile, CachedLines> debugMeshes = [];
    Mesh3D? markerMesh;
    Vector3 markerEye = new(float.PositiveInfinity);
    int markerChunks = -1, markerCount;
    long markerRevision = -1;
    void ClearDebugMeshes()
    {
        foreach (var cached in debugMeshes.Values)
            cached.Mesh.Dispose();
        debugMeshes.Clear();
        markerMesh?.Dispose();
        markerMesh = null;
        markerChunks = -1;
        markerEye = new(float.PositiveInfinity);
    }
    void UploadDebug(Mesh3D mesh, IEnumerable<DebugLine> lines, int limit, out int count)
    {
        count = 0;
        foreach (var line in lines)
        {
            if (count >= limit * 2)
                break;
            var color = new Color((byte)(line.Color.X * 255), (byte)(line.Color.Y * 255), (byte)(line.Color.Z * 255));
            debugVertices[count++] = new(line.A, color);
            debugVertices[count++] = new(line.B, color);
        }
        mesh.SetVertices(debugVertices, count, hasUvs: false);
        count /= 2;
    }
    void DrawCachedPhysics(RenderPass pass, Camera camera)
    {
        var watch = Stopwatch.StartNew();
        if (debugOwner != DebugWorld)
        {
            ClearDebugMeshes();
            debugOwner = DebugWorld;
        }
        // Animated colliders have their own small streaming buffer.
        DrawPhysics(pass, DebugWorld!.DynamicDebugLines(camera.Position));
        int remaining = 30000 - dynamicDebugLineCount;
        var visible = new HashSet<PhysicsWorld.DebugTile>();
        foreach (var tile in DebugWorld.DebugBatches(camera.Position, camera.View * camera.Projection))
        {
            if (tile.Lines.Count == 0 || remaining == 0)
                continue;
            visible.Add(tile);
            int wanted = Math.Min(tile.Lines.Count, remaining);
            if (!debugMeshes.TryGetValue(tile, out var cached))
            {
                var mesh = graphics.CreateMesh3D();
                mesh.PrimitiveType = PrimitiveType.Lines;
                cached = new(mesh, -1);
            }
            if (cached.Count != wanted)
            {
                UploadDebug(cached.Mesh, tile.Lines, wanted, out int count);
                cached = cached with
                {
                    Count = count
                };
            }
            debugMeshes[tile] = cached;
            pass.DrawMesh(cached.Mesh, white);
            remaining -= cached.Count;
        }
        foreach (var tile in debugMeshes.Keys.Where(t => !visible.Contains(t)).ToArray())
        {
            debugMeshes[tile].Mesh.Dispose();
            debugMeshes.Remove(tile);
        }
        if (markerMesh is null || Vector3.DistanceSquared(markerEye, camera.Position) > 16 || markerChunks != (terrainStore?.Count ?? 0) || markerRevision != DebugWorld.Changes.Revision)
        {
            markerMesh ??= graphics.CreateMesh3D();
            markerMesh.PrimitiveType = PrimitiveType.Lines;
            UploadDebug(markerMesh, PlacementDebug(camera.Position), Math.Min(remaining, 10000), out markerCount);
            markerEye = camera.Position;
            markerChunks = terrainStore?.Count ?? 0;
            markerRevision = DebugWorld.Changes.Revision;
        }
        if (markerCount > 0 && remaining >= markerCount)
            pass.DrawMesh(markerMesh, white);
        DebugLineCpuMilliseconds = watch.Elapsed.TotalMilliseconds;
    }
}
