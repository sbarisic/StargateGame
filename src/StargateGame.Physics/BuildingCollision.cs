using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Building;
using StargateGame.Shared;

namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    public WorldChanges Changes
    {
        get;
        private set;
    } = WorldChanges.Empty;
    WorldChanges? stagedChanges;
    readonly HashSet<ChunkCoordinate> dirtyTerrain = [];
    readonly Dictionary<ChunkCoordinate, (BodyID Body, Shape Shape)> buildingBodies = [];
    readonly Dictionary<ChunkCoordinate, Shape> preparedBuildings = [];
    readonly HashSet<ChunkCoordinate> dirtyBuildings = [];
    public int RebuiltBatches
    {
        get;
        private set;
    }
    public void StageChanges(WorldChanges state)
    {
        lock (NativeGate)
        {
            BuildingRules.ValidateState(state);
            CancelChanges();
            stagedChanges = state;
            foreach (var r in state.Removed.Except(Changes.Removed).Concat(Changes.Removed.Except(state.Removed)))
                dirtyTerrain.Add(r.Chunk);
            foreach (var c in dirtyTerrain)
                if (retainedTerrain.Remove(c, out var retained))
                {
                    shapes.Remove(retained.Shape);
                    retained.Shape.Dispose();
                }
            foreach (var p in state.Pieces.Except(Changes.Pieces))
                dirtyBuildings.Add(Owner(state, p));
            foreach (var p in Changes.Pieces.Except(state.Pieces))
                dirtyBuildings.Add(Owner(Changes, p));
            var oldCorners = BuildingCorners.Pieces(Changes);
            var newCorners = BuildingCorners.Pieces(state);
            foreach (var trim in oldCorners.Except(newCorners))
                dirtyBuildings.Add(Owner(Changes, Changes.Pieces.Single(p => p.Id == trim.Parent)));
            foreach (var trim in newCorners.Except(oldCorners))
                dirtyBuildings.Add(Owner(state, state.Pieces.Single(p => p.Id == trim.Parent)));
        }
    }
    static ChunkCoordinate Owner(WorldChanges state, BuildingPiece p) => BuildingGeometry.Chunks(BuildingGeometry.Bounds(BuildingGeometry.Mesh(state, p))).Order().First();
    public static ChunkCoordinate[] IncludeBuildingOwners(WorldChanges state, IEnumerable<ChunkCoordinate> chunks)
    {
        var result = chunks.ToHashSet();
        foreach (var p in state.Pieces)
        {
            var touched = BuildingGeometry.Chunks(BuildingGeometry.Bounds(BuildingGeometry.Mesh(state, p))).ToArray();
            if (touched.Any(result.Contains))
                result.Add(touched.Order().First());
        }
        return result.Order().ToArray();
    }
    public void CancelChanges()
    {
        lock (NativeGate)
        {
            foreach (var c in preparedTerrain.Keys.ToArray())
                if (preparedTerrain.Remove(c, out var shape))
                {
                    shapes.Remove(shape);
                    shape.Dispose();
                }
            foreach (var shape in preparedBuildings.Values)
            {
                shapes.Remove(shape);
                shape.Dispose();
            }
            preparedBuildings.Clear();
            dirtyTerrain.Clear();
            dirtyBuildings.Clear();
            stagedChanges = null;
        }
    }
    bool PrepareBuildings(IEnumerable<ChunkCoordinate> coordinates, int limit, bool wait = false)
    {
        var state = stagedChanges ?? Changes;
        var wanted = coordinates.ToHashSet();
        var groups = state.Pieces.GroupBy(p => Owner(state, p)).Where(g => wanted.Contains(g.Key) && (!buildingBodies.ContainsKey(g.Key) || dirtyBuildings.Contains(g.Key)) && !preparedBuildings.ContainsKey(g.Key)).OrderBy(g => g.Key).ToArray();
        int cooked = 0;
        foreach (var group in groups)
        {
            var input = BuildingInput(state, group.Key, group);
            if (cooked >= limit || !wait && !input.IsCompleted)
                continue;
            var ready = input.GetAwaiter().GetResult();
            lock (NativeGate)
                preparedBuildings[group.Key] = Cook(ready);
            cooked++;
        }
        return groups.All(g => preparedBuildings.ContainsKey(g.Key));
    }

    void CommitBuildings(HashSet<ChunkCoordinate> wanted)
    {
        PrepareBuildings(wanted, 256);
        foreach (var c in buildingBodies.Keys.Where(c => !wanted.Contains(c) || dirtyBuildings.Contains(c)).Order().ToArray())
        {
            var old = buildingBodies[c];
            system.BodyInterface.RemoveBody(old.Body);
            system.BodyInterface.DestroyBody(old.Body);
            ownedBodies.Remove(old.Body);
            shapes.Remove(old.Shape);
            old.Shape.Dispose();
            buildingBodies.Remove(c);
            topology++;
        }
        foreach (var c in preparedBuildings.Keys.Order().ToArray())
        {
            var shape = preparedBuildings[c];
            preparedBuildings.Remove(c);
            if (wanted.Contains(c))
            {
                buildingBodies.Add(c, (AddBody(shape, Vector3.Zero, Quaternion.Identity, false), shape));
                RebuiltBatches++;
            }
            else
            {
                shapes.Remove(shape);
                shape.Dispose();
            }
        }
        Changes = stagedChanges ?? Changes;
        stagedChanges = null;
        dirtyTerrain.Clear();
        dirtyBuildings.Clear();
    }
    public IEnumerable<BuildTriangle> SceneryMesh(string category, ulong id, ChunkCoordinate coordinate)
    {
        if (terrainSource is null || !terrainSource.TryGetChunk(coordinate, out var chunk) || Changes.IsRemoved(category, id))
            yield break;
        if (category == "tree")
            foreach (var tree in StargateGame.WorldGen.TreeScenery.Resolve(chunk, World.Definition.Preset, World.Definition.Fixture).Where(t => t.Id == id))
                foreach (var t in treeMeshes.Value[tree.Model])
                    yield return new(Vector3.Transform(t.A, tree.Transform), Vector3.Transform(t.B, tree.Transform), Vector3.Transform(t.C, tree.Transform));
        if (category == "rock")
            foreach (var rock in StargateGame.WorldGen.RockScenery.Resolve(chunk, World.Definition.Preset).Where(t => t.Id == id))
                foreach (var t in rockMeshes.Value[rock.Model])
                    yield return new(Vector3.Transform(t.A, rock.Transform), Vector3.Transform(t.B, rock.Transform), Vector3.Transform(t.C, rock.Transform));
    }
    public IEnumerable<BuildTriangle> ObstacleTriangles(Vector3 near, float radius = 12, Guid ignoreBuilding = default, string? ignoreEntity = null, bool includeTrees = true)
    {
        if (terrainSource is not null)
            foreach (var c in ChunkCoordinate.At(near.X, near.Z).Around(1))
                if (terrainSource.TryGetChunk(c, out var data))
                {
                    foreach (var t in data.Triangles())
                        yield return new(t.A, t.B, t.C);
                    foreach (var rock in StargateGame.WorldGen.RockScenery.Resolve(data, World.Definition.Preset).Where(r => !Changes.IsRemoved("rock", r.Id) && Vector3.DistanceSquared(r.Transform.Translation, near) < radius * radius))
                        foreach (var t in SceneryMesh("rock", rock.Id, c))
                            yield return t;
                    foreach (var tree in StargateGame.WorldGen.TreeScenery.Resolve(data, World.Definition.Preset, World.Definition.Fixture).Where(r => includeTrees && !Changes.IsRemoved("tree", r.Id) && Vector3.DistanceSquared(r.Transform.Translation, near) < radius * radius))
                        foreach (var t in SceneryMesh("tree", tree.Id, c))
                            yield return t;
                }
        foreach (var e in World.Definition.Entities.Where(e => e.Id != ignoreEntity))
            foreach (var t in ReadCollision(e.Model))
                yield return new(Vector3.Transform(t.A, e.Transform), Vector3.Transform(t.B, e.Transform), Vector3.Transform(t.C, e.Transform));
        foreach (var p in Changes.Pieces.Where(p => p.Id != ignoreBuilding))
            foreach (var t in BuildingGeometry.Mesh(Changes, p))
                yield return t;
    }
}
