using System.Text.Json;
using StargateGame.Shared;
namespace StargateGame.Persistence;

public sealed record SavedPlayer(Guid Id, string Key, Guid Location, PlayerState State, CharacterPresentation? Appearance);
public sealed partial class WorldRepository
{
    public Task<SavedPlayer> AuthenticatePlayerAsync(string key, Guid home, string name) => Run(db =>
    {
        if (Scalar(db, "SELECT device FROM revoked_devices WHERE device=$key;", ("$key", key)) is not null)
            throw new InvalidOperationException("Device key revoked; contact the server administrator.");
        using (var cmd = Command(db, "SELECT json,revoked FROM players WHERE device=$key;", ("$key", key)))
        using (var r = cmd.ExecuteReader())
            if (r.Read())
            {
                if (r.GetInt32(1) != 0)
                    throw new InvalidOperationException("This device key has been revoked by the server administrator.");
                return JsonSerializer.Deserialize<SavedPlayer>(r.GetString(0), Protocol.Json)!;
            }
        var saved = new SavedPlayer(Guid.NewGuid(), key, home, new(0, name, Load(db, home).Definition.Spawns[0], default, 0, 0, false, 0), null);
        Execute(db, "INSERT INTO players(id,device,revoked,json) VALUES($id,$key,0,$json);", ("$id", saved.Id.ToString("D")), ("$key", key), ("$json", JsonSerializer.Serialize(saved, Protocol.Json)));
        return saved;
    });
    public Task SavePlayerAsync(SavedPlayer player) => Run(db =>
    {
        Execute(db, "UPDATE players SET json=$json WHERE id=$id AND device=$key AND revoked=0;", ("$id", player.Id.ToString("D")), ("$key", player.Key), ("$json", JsonSerializer.Serialize(player, Protocol.Json)));
        return true;
    });
    public Task RebindPlayerAsync(Guid playerId, string newKey) => Run(db =>
    {
        if (newKey.Length != 64 || !newKey.All(Uri.IsHexDigit))
            throw new ArgumentException("Expected SHA-256 device public-key fingerprint.");
        newKey = newKey.ToUpperInvariant();
        using var tx = db.BeginTransaction();
        string json = (string?)Scalar(db, "SELECT json FROM players WHERE id=$id AND revoked=0;", ("$id", playerId.ToString("D"))) ?? throw new InvalidOperationException("Unknown player.");
        if (Scalar(db, "SELECT id FROM players WHERE device=$key;", ("$key", newKey)) is not null || Scalar(db, "SELECT device FROM revoked_devices WHERE device=$key;", ("$key", newKey)) is not null)
            throw new InvalidOperationException("Replacement key is already registered; use a new key.");
        var player = JsonSerializer.Deserialize<SavedPlayer>(json, Protocol.Json)!;
        Execute(db, "INSERT INTO revoked_devices(device) VALUES($key);", ("$key", player.Key));
        player = player with
        {
            Key = newKey
        };
        Execute(db, "UPDATE players SET device=$key,json=$json WHERE id=$id;", ("$key", newKey), ("$json", JsonSerializer.Serialize(player, Protocol.Json)), ("$id", playerId.ToString("D")));
        tx.Commit();
        return true;
    });
    public Task<bool> DeviceRevokedAsync(string key) => Run(db => Scalar(db, "SELECT device FROM revoked_devices WHERE device=$key;", ("$key", key)) is not null);
    public Task<GateAssociation?> AssociationForLocationAsync(Guid location) => Run(db =>
    {
        using var cmd = Command(db, "SELECT address,gate,dhd,powered,available,ordinary FROM galaxy_associations WHERE location=$location;", ("$location", location.ToString("D")));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new GateAssociation((ulong)r.GetInt64(0), location, Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetInt32(3) != 0, r.GetInt32(4) != 0, r.GetInt32(5) != 0) : null;
    });
}
