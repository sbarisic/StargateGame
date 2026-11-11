using System.Text.Json;
using Microsoft.Data.Sqlite;
using StargateGame.Shared;
namespace StargateGame.Persistence;

public sealed record TransitRecord(Guid Id, Guid Connection, Guid Source, Guid Destination, SavedPlayer? Player, PropDefinition? Prop, BodyState? Body);
public sealed partial class WorldRepository
{
    // Autosaves update poses, never ownership. A queued pre-capture snapshot may
    // execute after a transfer transaction; it must not reintroduce its registry.
    public Task SaveCurrentBodiesAsync(Guid location, BodyState[] bodies, PropDefinition[] registry) => Run(db =>
    {
        using var tx = db.BeginTransaction();
        var definition = JsonSerializer.Deserialize<PlanetDefinition>((string)Scalar(db, "SELECT definition FROM locations WHERE id=$id;", ("$id", location.ToString("D")))!, Protocol.Json)!;
        var saved = Read<BodyState>(db, "crates", location).ToDictionary(b => b.Id);
        var submitted = registry.ToDictionary(p => p.PersistentId, p => p.Id);
        var poses = bodies.ToDictionary(b => b.Id);
        foreach (var prop in definition.Props)
            if (submitted.TryGetValue(prop.PersistentId, out var oldId) && oldId == prop.Id && poses.TryGetValue(oldId, out var pose))
                saved[prop.Id] = pose;
        WriteBodies(db, location, definition.Props.Select(p => saved.GetValueOrDefault(p.Id, new(p.Id, p.Position, System.Numerics.Quaternion.Identity, default, default, false))).ToArray(), definition.Props);
        tx.Commit();
        return true;
    });
    public Task DestroyPropAsync(Guid location, Guid persistentId) => Run(db =>
    {
        using var tx = db.BeginTransaction();
        var definition = JsonSerializer.Deserialize<PlanetDefinition>((string)Scalar(db, "SELECT definition FROM locations WHERE id=$id;", ("$id", location.ToString("D")))!, Protocol.Json)!;
        var registry = definition.Props.Where(p => p.PersistentId != persistentId).ToArray();
        var saved = Read<BodyState>(db, "crates", location).ToDictionary(b => b.Id);
        WriteBodies(db, location, registry.Select(p => saved.GetValueOrDefault(p.Id, new(p.Id, p.Position, System.Numerics.Quaternion.Identity, default, default, false))).ToArray(), registry);
        Execute(db, "UPDATE transits SET phase='destroyed' WHERE entity=$entity AND phase='emerging';", ("$entity", persistentId.ToString("D")));
        tx.Commit();
        return true;
    });
    static void WriteBodies(SqliteConnection db, Guid location, BodyState[] bodies, PropDefinition[]? registry)
    {
        string id = location.ToString("D");
        var definition = JsonSerializer.Deserialize<PlanetDefinition>((string)Scalar(db, "SELECT definition FROM locations WHERE id=$id;", ("$id", id))!, Protocol.Json)!;
        registry ??= definition.Props;
        if (bodies.Length > 64 || !bodies.Select(b => b.Id).Order().SequenceEqual(registry.Select(p => p.Id).Order()) || registry.Select(p => p.Id).Distinct().Count() != registry.Length || registry.Any(p => p.Id is < 1 or > 64 || !p.Valid || p.PersistentId == Guid.Empty))
            throw new InvalidDataException("Invalid persistent prop registry.");
        Execute(db, "UPDATE locations SET definition=$json WHERE id=$id;", ("$json", JsonSerializer.Serialize(definition with
        {
            Props = registry
        }, Protocol.Json)), ("$id", id));
        Execute(db, "DELETE FROM crates WHERE location=$location;", ("$location", id));
        foreach (var b in bodies)
            Execute(db, "INSERT INTO crates VALUES($location,$id,$json);", ("$location", id), ("$id", b.Id), ("$json", JsonSerializer.Serialize(b, Protocol.Json)));
    }
    public Task CaptureTransitAsync(TransitRecord record, BodyState[] sourceBodies, PropDefinition[] sourceRegistry) => Run(db =>
    {
        using var tx = db.BeginTransaction();
        Execute(db, "INSERT INTO transits VALUES($id,$entity,'captured',$json);", ("$id", record.Id.ToString("D")), ("$entity", (record.Player?.Id ?? record.Prop!.PersistentId).ToString("D")), ("$json", JsonSerializer.Serialize(record, Protocol.Json)));
        if (record.Prop is { } captured)
        {
            var definition = JsonSerializer.Deserialize<PlanetDefinition>((string)Scalar(db, "SELECT definition FROM locations WHERE id=$id;", ("$id", record.Source.ToString("D")))!, Protocol.Json)!;
            if (!definition.Props.Any(p => p.PersistentId == captured.PersistentId))
                throw new InvalidOperationException("Prop no longer belongs to the capture location.");
            var registry = definition.Props.Where(p => p.PersistentId != captured.PersistentId).ToArray();
            var retained = Read<BodyState>(db, "crates", record.Source).ToDictionary(b => b.Id);
            foreach (var body in sourceBodies)
                retained[body.Id] = body;
            var bodies = registry.Select(p => retained.GetValueOrDefault(p.Id, new(p.Id, p.Position, System.Numerics.Quaternion.Identity, default, default, false))).ToArray();
            WriteBodies(db, record.Source, bodies, registry);
        }
        tx.Commit();
        return true;
    });
    public Task CommitPlayerTransitAsync(Guid id, SavedPlayer player) => Run(db =>
    {
        using var tx = db.BeginTransaction();
        if ((string?)Scalar(db, "SELECT phase FROM transits WHERE id=$id;", ("$id", id.ToString("D"))) != "captured")
            throw new InvalidOperationException("Transfer is not pending.");
        Execute(db, "UPDATE players SET json=$json WHERE id=$id;", ("$json", JsonSerializer.Serialize(player, Protocol.Json)), ("$id", player.Id.ToString("D")));
        Execute(db, "UPDATE transits SET phase='committed' WHERE id=$id;", ("$id", id.ToString("D")));
        tx.Commit();
        return true;
    });
    public Task CommitPropTransitAsync(Guid id, Guid destination, BodyState[] bodies, PropDefinition[] registry, bool emerging = false) => Run(db =>
    {
        using var tx = db.BeginTransaction();
        if ((string?)Scalar(db, "SELECT phase FROM transits WHERE id=$id;", ("$id", id.ToString("D"))) != "captured")
            throw new InvalidOperationException("Transfer is not pending.");
        WriteBodies(db, destination, bodies, registry);
        Execute(db, "UPDATE transits SET phase=$phase WHERE id=$id;", ("$phase", emerging ? "emerging" : "committed"), ("$id", id.ToString("D")));
        tx.Commit();
        return true;
    });
    public Task CompletePropEmergenceAsync(Guid id, Guid destination, PropDefinition prop, BodyState body) => Run(db =>
    {
        using var tx = db.BeginTransaction();
        if ((string?)Scalar(db, "SELECT phase FROM transits WHERE id=$id;", ("$id", id.ToString("D"))) != "emerging")
            return false;
        var definition = JsonSerializer.Deserialize<PlanetDefinition>((string)Scalar(db, "SELECT definition FROM locations WHERE id=$id;", ("$id", destination.ToString("D")))!, Protocol.Json)!;
        if (!definition.Props.Any(p => p.Id == prop.Id && p.PersistentId == prop.PersistentId))
            throw new InvalidOperationException("Emerging prop ownership changed.");
        Execute(db, "UPDATE crates SET json=$json WHERE location=$location AND id=$body;", ("$json", JsonSerializer.Serialize(body, Protocol.Json)), ("$location", destination.ToString("D")), ("$body", prop.Id));
        Execute(db, "UPDATE transits SET phase='committed' WHERE id=$id;", ("$id", id.ToString("D")));
        tx.Commit();
        return true;
    });
    public Task ReturnTransitAsync(Guid id, TransitRecord? recovery = null) => Run(db =>
    {
        using var tx = db.BeginTransaction();
        if (recovery is not null)
        {
            if (recovery.Id != id)
                throw new InvalidOperationException("Recovery identity mismatch.");
            Execute(db, "UPDATE transits SET json=$json WHERE id=$id AND phase='captured';", ("$json", JsonSerializer.Serialize(recovery, Protocol.Json)), ("$id", id.ToString("D")));
        }
        RecoverTransit(db, id);
        tx.Commit();
        return true;
    });
    static void RecoverTransit(SqliteConnection db, Guid id)
    {
        string? json = (string?)Scalar(db, "SELECT json FROM transits WHERE id=$id AND phase='captured';", ("$id", id.ToString("D")));
        if (json is null)
            return;
        var record = JsonSerializer.Deserialize<TransitRecord>(json, Protocol.Json)!;
        if (record.Player is { } player)
            Execute(db, "UPDATE players SET json=$json WHERE id=$id;", ("$json", JsonSerializer.Serialize(player, Protocol.Json)), ("$id", player.Id.ToString("D")));
        if (record.Prop is { } prop && record.Body is { } body)
        {
            var definition = JsonSerializer.Deserialize<PlanetDefinition>((string)Scalar(db, "SELECT definition FROM locations WHERE id=$id;", ("$id", record.Source.ToString("D")))!, Protocol.Json)!;
            var bodies = Read<BodyState>(db, "crates", record.Source).Where(b => b.Id != prop.Id).Append(body).ToArray();
            WriteBodies(db, record.Source, bodies, definition.Props.Where(p => p.Id != prop.Id).Append(prop).ToArray());
        }
        Execute(db, "UPDATE transits SET phase='returned' WHERE id=$id;", ("$id", id.ToString("D")));
    }
    static void RecoverTransits(SqliteConnection db)
    {
        List<Guid> ids = [];
        using (var c = Command(db, "SELECT id FROM transits WHERE phase='captured';"))
        using (var r = c.ExecuteReader())
            while (r.Read())
                ids.Add(Guid.Parse(r.GetString(0)));
        using var tx = db.BeginTransaction();
        foreach (var id in ids)
            RecoverTransit(db, id);
        // Gates restart idle. Never revive an unfinished receiving body unclipped.
        List<TransitRecord> emerging = [];
        using (var c = Command(db, "SELECT json FROM transits WHERE phase='emerging';"))
        using (var r = c.ExecuteReader())
            while (r.Read())
                emerging.Add(JsonSerializer.Deserialize<TransitRecord>(r.GetString(0), Protocol.Json)!);
        foreach (var record in emerging)
        {
            var definition = JsonSerializer.Deserialize<PlanetDefinition>((string)Scalar(db, "SELECT definition FROM locations WHERE id=$id;", ("$id", record.Destination.ToString("D")))!, Protocol.Json)!;
            var registry = definition.Props.Where(p => p.PersistentId != record.Prop!.PersistentId).ToArray();
            var bodies = Read<BodyState>(db, "crates", record.Destination).Where(b => registry.Any(p => p.Id == b.Id)).ToArray();
            WriteBodies(db, record.Destination, bodies, registry);
        }
        Execute(db, "DELETE FROM transits WHERE phase IN ('committed','returned','emerging','destroyed');");
        tx.Commit();
    }
    static PlanetDefinition IdentifyProps(PlanetDefinition d) => d with { Props = d.Props.Select(p => p.PersistentId == Guid.Empty ? p with { PersistentId = GateTravel.PropIdentity(d.LocationId, p.Id) } : p).ToArray() };
}
