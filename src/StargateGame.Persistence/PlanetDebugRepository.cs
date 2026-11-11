using System.Numerics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StargateGame.Shared;
using StargateGame.Building;

namespace StargateGame.Persistence;
public sealed partial class WorldRepository
{
    public Task<PlanetDefinition> ApplyHomeEnvironmentAsync(Guid home, PlanetEnvironmentSettings settings) => Run(db =>
    {
        var old = Load(db, home).Definition;
        var environment = settings.Apply(old.Environment ?? PlanetEnvironmentDefinition.Create(home, old.Seed, old.Preset), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var definition = old with { Environment = environment };
        Execute(db, "UPDATE locations SET definition=$json WHERE id=$id;", ("$json", JsonSerializer.Serialize(definition, Protocol.Json)), ("$id", home.ToString("D")));
        return definition;
    });

    // Called only after collision and client preparation succeed. All old-location
    // checkpoint producers are suspended until the new runtime is installed.
    public Task<SavedLocation> ReplaceHomeAsync(PlanetDefinition definition, long expectedRevision, SavedPlayer? explorer) => Run(db =>
    {
        PlanetSettings.Validate(definition);
        var old = Load(db, definition.LocationId);
        if (definition.UniverseId != UniverseId || old.Changes.Revision != expectedRevision)
            throw new InvalidOperationException("Home planet changed during preparation; regenerate again.");
        string backup = DatabasePath + ".before-regeneration", temporary = backup + ".tmp";
        using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
        {
            copy.Open(); db.BackupDatabase(copy);
        }
        File.Move(temporary, backup, true);
        using var tx = db.BeginTransaction();
        string id = definition.LocationId.ToString("D");
        foreach (string table in new[] { "pieces", "structures", "removals", "crates", "receipts" })
            Execute(db, $"DELETE FROM {table} WHERE location=$id;", ("$id", id));
        Execute(db, "UPDATE locations SET definition=$json,generator=$generator,scenery=$scenery,kit=$kit,revision=0 WHERE id=$id;",
            ("$json", JsonSerializer.Serialize(definition, Protocol.Json)), ("$generator", definition.GeneratorVersion),
            ("$scenery", PlanetSettings.SceneryVersion), ("$kit", BuildingGeometry.KitVersion), ("$id", id));
        var changes = WriteGenerated(db, definition);
        var players = new List<SavedPlayer>();
        using (var command = Command(db, "SELECT json FROM players;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) players.Add(JsonSerializer.Deserialize<SavedPlayer>(reader.GetString(0), Protocol.Json)!);
        foreach (var saved in players)
        {
            var player = explorer?.Id == saved.Id ? explorer : saved;
            if (player.Location != definition.LocationId && player.Id != explorer?.Id) continue;
            player = player with { Location = definition.LocationId, State = player.State with {
                Position = definition.Spawns[0], Velocity = Vector3.Zero, Grounded = false,
                SupportId = 0, GroundVelocity = Vector3.Zero, GroundNormal = Vector3.UnitY, TransitFrozen = false } };
            Execute(db, "UPDATE players SET json=$json WHERE id=$id;", ("$json", JsonSerializer.Serialize(player, Protocol.Json)), ("$id", player.Id.ToString("D")));
        }
        tx.Commit();
        return new SavedLocation(definition.LocationId, definition, changes, []);
    });
}
