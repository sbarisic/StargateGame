using System.Text.Json;
using Microsoft.Data.Sqlite;
using StargateGame.Shared;

namespace StargateGame.Persistence;

public sealed partial class WorldRepository
{
    static void MigrateWeapons(SqliteConnection db)
    {
        Execute(db, """
            CREATE TABLE weapon_owners(owner TEXT PRIMARY KEY,revision INTEGER NOT NULL,equipped TEXT);
            CREATE TABLE weapon_containers(owner TEXT PRIMARY KEY REFERENCES weapon_owners(owner),location TEXT NOT NULL,piece TEXT NOT NULL,UNIQUE(location,piece));
            CREATE TABLE weapon_items(id TEXT PRIMARY KEY,catalog TEXT NOT NULL CHECK(catalog='rifle'),owner TEXT NOT NULL REFERENCES weapon_owners(owner),slot INTEGER NOT NULL CHECK(slot>=0 AND slot<16),UNIQUE(owner,slot));
            CREATE TABLE weapon_receipts(player TEXT NOT NULL,request TEXT NOT NULL,command TEXT NOT NULL,result TEXT NOT NULL,PRIMARY KEY(player,request));
            INSERT INTO migrations VALUES(9,datetime('now')); PRAGMA user_version=9;
            """);
    }
    static string PersonalOwner(Guid player) => "player/" + player.ToString("D");
    static string ContainerOwner(Guid location, Guid piece) => $"container/{location:D}/{piece:D}";
    static WeaponInventory ReadWeapons(SqliteConnection db, string owner, int capacity)
    {
        long revision; Guid? equipped;
        using (var cmd = Command(db, "SELECT revision,equipped FROM weapon_owners WHERE owner=$owner", ("$owner", owner)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw new InvalidOperationException("Weapon owner is not initialized.");
            revision = r.GetInt64(0); equipped = r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1));
        }
        WeaponItem?[] slots = new WeaponItem?[capacity];
        using (var cmd = Command(db, "SELECT slot,id,catalog FROM weapon_items WHERE owner=$owner ORDER BY slot", ("$owner", owner)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) slots[r.GetInt32(0)] = new(Guid.Parse(r.GetString(1)), r.GetString(2));
        return new(revision, slots, equipped);
    }
    public Task<WeaponInventory> EnsureWeaponsAsync(Guid player, bool equipped) => Run(db =>
    {
        using var tx = db.BeginTransaction();
        string owner = PersonalOwner(player);
        if (Scalar(db, "SELECT owner FROM weapon_owners WHERE owner=$owner", ("$owner", owner)) is null)
        {
            var starter = WeaponCatalog.Starter(equipped);
            Execute(db, "INSERT INTO weapon_owners VALUES($owner,0,$equipped)", ("$owner", owner), ("$equipped", starter.Equipped?.ToString("D") ?? (object)DBNull.Value));
            Execute(db, "INSERT INTO weapon_items VALUES($id,'rifle',$owner,0)", ("$id", starter.Slots[0]!.Id.ToString("D")), ("$owner", owner));
        }
        var result = ReadWeapons(db, owner, WeaponCatalog.PersonalSlots);
        tx.Commit(); return result;
    });
    public Task<WeaponResult> WeaponsAsync(Guid player, Guid location, WeaponRequest request) => Run(db =>
    {
        using var tx = db.BeginTransaction();
        string owner = PersonalOwner(player), command = JsonSerializer.Serialize(new { location, request }, Protocol.Json);
        using (var cmd = Command(db, "SELECT command,result FROM weapon_receipts WHERE player=$p AND request=$r", ("$p", owner), ("$r", request.Id.ToString("D"))))
        using (var r = cmd.ExecuteReader())
            if (r.Read())
            {
                if (r.GetString(0) != command) throw new InvalidOperationException("Request ID reused for a different operation.");
                return JsonSerializer.Deserialize<WeaponResult>(r.GetString(1), Protocol.Json)!;
            }
        var inventory = ReadWeapons(db, owner, WeaponCatalog.PersonalSlots);
        WeaponContainer? container = null;
        string shared = ContainerOwner(location, request.Container);
        if (request.Container != Guid.Empty && request.Action is "open" or "store" or "take" or "snapshot")
        {
            // Validate existence in the same transaction as a transfer, so a queued
            // removal cannot recreate orphaned storage. Server also checks LOS/access.
            string? pieceJson = Scalar(db, "SELECT json FROM pieces WHERE location=$l AND id=$p", ("$l", location.ToString("D")), ("$p", request.Container.ToString("D"))) as string;
            var piece = pieceJson is null ? null : JsonSerializer.Deserialize<BuildingPiece>(pieceJson, Protocol.Json);
            if (piece is null || !WeaponCatalog.IsContainer(piece.Prefab)) return new WeaponResult(request.Id, false, "Container no longer exists.", inventory);
            Execute(db, "INSERT OR IGNORE INTO weapon_owners VALUES($owner,0,NULL)", ("$owner", shared));
            Execute(db, "INSERT OR IGNORE INTO weapon_containers VALUES($owner,$location,$piece)", ("$owner", shared), ("$location", location.ToString("D")), ("$piece", request.Container.ToString("D")));
            var storage = ReadWeapons(db, shared, WeaponCatalog.ContainerSlots);
            container = new(location, request.Container, storage.Revision, storage.Slots);
        }
        var result = WeaponCatalog.Apply(inventory, container, request);
        if (result.Success && request.Action is "equip" or "holster" or "store" or "take")
        {
            if (request.Action is "store" or "take")
            {
                bool deposit = request.Action == "store";
                var target = deposit ? result.Container!.Slots : result.Inventory.Slots;
                int slot = Array.FindIndex(target, i => i?.Id == request.Item);
                Execute(db, "UPDATE weapon_items SET owner=$to,slot=$slot WHERE id=$id AND owner=$from", ("$to", deposit ? shared : owner), ("$slot", slot), ("$id", request.Item!.Value.ToString("D")), ("$from", deposit ? owner : shared));
                Execute(db, "UPDATE weapon_owners SET revision=$r WHERE owner=$owner", ("$r", result.Container!.Revision), ("$owner", shared));
            }
            Execute(db, "UPDATE weapon_owners SET revision=$r,equipped=$e WHERE owner=$owner", ("$r", result.Inventory.Revision), ("$e", result.Inventory.Equipped?.ToString("D") ?? (object)DBNull.Value), ("$owner", owner));
        }
        Execute(db, "INSERT INTO weapon_receipts VALUES($p,$r,$c,$result)", ("$p", owner), ("$r", request.Id.ToString("D")), ("$c", command), ("$result", JsonSerializer.Serialize(result, Protocol.Json)));
        tx.Commit(); return result;
    });
    static void RemoveEmptyWeaponContainers(SqliteConnection db, Guid location, HashSet<Guid> retained)
    {
        var removed = new List<string>();
        using (var cmd = Command(db, "SELECT owner,piece FROM weapon_containers WHERE location=$l", ("$l", location.ToString("D"))))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) if (!retained.Contains(Guid.Parse(r.GetString(1)))) removed.Add(r.GetString(0));
        foreach (string owner in removed)
        {
            if (Convert.ToInt64(Scalar(db, "SELECT count(*) FROM weapon_items WHERE owner=$o", ("$o", owner))) != 0)
                throw new InvalidOperationException("Empty weapon containers before removing them or their structure.");
            Execute(db, "DELETE FROM weapon_containers WHERE owner=$o; DELETE FROM weapon_owners WHERE owner=$o", ("$o", owner));
        }
    }
}
