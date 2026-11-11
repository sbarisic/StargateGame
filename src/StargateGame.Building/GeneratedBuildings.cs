using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using StargateGame.Shared;

namespace StargateGame.Building;

public static class GeneratedBuildings
{
    public static Guid Identity(Guid location, string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"settlements-v1:{location:D}:{name}")).AsSpan(0, 16));
    public static WorldChanges Create(PlanetDefinition planet)
    {
        if (planet.Settlements is not { } layout)
            return WorldChanges.Empty with
            {
                UniverseId = planet.UniverseId,
                LocationId = planet.LocationId
            };
        layout.Validate();
        List<StructureRecord> structures = [];
        List<BuildingPiece> pieces = [];
        var roles = FurnishingLayouts.AssignRoles(layout, planet.Seed);
        foreach (var p in layout.Plots)
        {
            var template = SettlementTemplates.Get(p.Template);
            Guid structure = Identity(planet.LocationId, $"structure:{p.Id}"), root = Identity(planet.LocationId, $"building:{p.Id}");
            structures.Add(new(structure, p.Position, p.Rotation, true));
            pieces.Add(new(root, structure, PieceKind.Prefab, default, 0, 0, Prefab: p.Template));
            for (int i = 0; i < template.Doors.Length; i++)
            {
                var door = template.Doors[i];
                pieces.Add(new(Identity(planet.LocationId, $"door:{p.Id}:{i}"), structure, PieceKind.Prefab, default, door.Rotation, 0, Prefab: door.Model, Offset: new(door.Position[0], door.Position[1], door.Position[2]), Parent: root));
            }
            if (roles.TryGetValue(p.Id, out var role))
            {
                var room = FurnishingLayouts.Create(p, planet.Seed, role);
                for (int i = 0; i < room.Pieces.Length; i++)
                {
                    var furnishing = room.Pieces[i];
                    pieces.Add(new(Identity(planet.LocationId, $"furnishing:{FurnishingLayouts.Revision}:{p.Id}:{i}"), structure,
                        PieceKind.Prefab, default, furnishing.Rotation, 0, Prefab: furnishing.Asset, Offset: furnishing.Position, Parent: root));
                }
            }
        }
        if (pieces.Count > SettlementSettings.MaxRecords)
            throw new InvalidDataException("Generated settlement record cap exceeded.");
        var result = new WorldChanges(planet.UniverseId, planet.LocationId, 0, structures.ToArray(), pieces.ToArray(), []);
        BuildingRules.ValidateState(result);
        return result;
    }
}
