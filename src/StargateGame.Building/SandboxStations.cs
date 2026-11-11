using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Building;

public static class SandboxStations
{
    public static WorldChanges Create(PlanetDefinition world)
    {
        // Reuse production building assemblies, including their functional doors and furnishings.
        var generated = GeneratedBuildings.Create(world);
        var structures = generated.Structures.ToList(); var pieces = generated.Pieces.ToList();
        void Piece(string name, Vector3 origin, PieceKind kind, string prefab = "")
        {
            var id = GeneratedBuildings.Identity(world.LocationId, "sandbox-" + name);
            structures.Add(new(id, origin, 0));
            pieces.Add(new(GeneratedBuildings.Identity(world.LocationId, "sandbox-piece-" + name), id, kind, default, 0, 0, Prefab: prefab, FoundationBottom: name == "stairs" ? .3f : 0));
        }
        Piece("equipment", new(22, 0, 10), PieceKind.Prefab, "weapon-crate");
        Piece("stairs", new(-24, .3f, 8), PieceKind.Stairs);
        Piece("stair-incline", new(-28, 0, 8), PieceKind.Prefab, "sandbox-stair-incline");
        pieces.Add(new(GeneratedBuildings.Identity(world.LocationId, "sandbox-stairs-base"), GeneratedBuildings.Identity(world.LocationId, "sandbox-stairs"), PieceKind.SquareFoundation, default, 0, 0, FoundationBottom: -.15f));
        Piece("platform", new(-24, .3f, 13), PieceKind.SquareFoundation);
        var result = generated with { Structures = structures.ToArray(), Pieces = pieces.ToArray() };
        BuildingRules.ValidateState(result);
        if (BuildingRules.Unsupported(result).Length != 0) throw new InvalidDataException("Sandbox station lacks structural support.");
        return result;
    }
}
