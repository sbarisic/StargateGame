using System.Numerics;
using StargateGame.Shared;
using StargateGame.Building;
using StargateGame.WorldGen;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    public sealed record HazardCandidate(Guid Piece, SceneryRemoval? Scenery, BuildTriangle[] Mesh);
    public HazardCandidate[] KawooshCandidates(GateFrame gate)
    {
        float range = gate.Radius * 3;
        var bounds = new BuildBounds(gate.Centre - new Vector3(range), gate.Centre + new Vector3(range));
        List<HazardCandidate> result = [];
        foreach (var coordinate in terrainBodies.Keys)
        {
            if (terrainSource is null || !terrainSource.TryGetChunk(coordinate, out var chunk))
                continue;
            // Include neighbouring chunk owners of large trees whose crowns overlap.
            foreach (var tree in TreeScenery.Resolve(chunk, World.Definition.Preset, World.Definition.Fixture))
                if (!Changes.IsRemoved("tree", tree.Id) && bounds.Intersects(new(tree.Min, tree.Max)))
                    result.Add(new(Guid.Empty, new("tree", tree.Id, coordinate), SceneryMesh("tree", tree.Id, coordinate).ToArray()));
            foreach (var rock in RockScenery.Resolve(chunk, World.Definition.Preset))
                if (rock.Collidable && !Changes.IsRemoved("rock", rock.Id) && bounds.Intersects(new(rock.Min, rock.Max)))
                    result.Add(new(Guid.Empty, new("rock", rock.Id, coordinate), SceneryMesh("rock", rock.Id, coordinate).ToArray()));
        }
        foreach (var piece in Changes.Pieces)
        {
            var mesh = BuildingGeometry.Mesh(Changes, piece);
            if (bounds.Intersects(BuildingGeometry.Bounds(mesh)))
                result.Add(new(piece.Id, null, mesh));
        }
        return result.ToArray();
    }
}
