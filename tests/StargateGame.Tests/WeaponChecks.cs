using System.Numerics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StargateGame.Persistence;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class WeaponChecks
{
    public static async Task Run()
    {
        var starter = WeaponCatalog.Starter(true); var rifle = starter.Slots[0]!;
        var holster = WeaponCatalog.Apply(starter, null, new(Guid.NewGuid(), "holster"));
        Verify.Check(holster.Success && holster.Inventory.Equipped is null && holster.Inventory.Slots[0] == rifle, "holster preserves ownership");
        Verify.Check(!WeaponCatalog.Apply(starter, null, new(Guid.NewGuid(), "equip", Item: Guid.NewGuid())).Success, "cannot equip unowned item");
        Verify.Check(!WeaponCatalog.Apply(holster.Inventory, null, new(Guid.NewGuid(), "equip", Item: rifle.Id)).Success, "stale equipment rejected");
        var full = new WeaponInventory(0, Enumerable.Range(0, 4).Select(_ => new WeaponItem(Guid.NewGuid())).ToArray(), null);
        var chest = new WeaponContainer(Guid.NewGuid(), Guid.NewGuid(), 0, [rifle, ..new WeaponItem?[15]]);
        Verify.Check(!WeaponCatalog.Apply(full, chest, new(Guid.NewGuid(), "take", Item: rifle.Id, Container: chest.Piece)).Success, "full inventory blocks withdrawal");
        var fullChest = chest with { Slots = Enumerable.Range(0, 16).Select(_ => new WeaponItem(Guid.NewGuid())).ToArray() };
        Verify.Check(!WeaponCatalog.Apply(starter, fullChest, new(Guid.NewGuid(), "store", Item: rifle.Id, Container: chest.Piece)).Success, "all sixteen occupied container slots block deposit");
        for (int slot = 0; slot < 4; slot++)
            Verify.Check(WeaponCatalog.Apply(full, null, new(Guid.NewGuid(), "equip", Item: full.Slots[slot]!.Id)).Inventory.Equipped == full.Slots[slot]!.Id, $"personal slot {slot + 1} selects its unique weapon");
        var roundtrip = JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(new Message { Kind = "weapon-state", Weapons = holster }, Protocol.Json), Protocol.Json)!;
        Verify.Check(roundtrip.Weapons!.Inventory.Slots[0]!.Id == rifle.Id, "weapon wire identities preserved");
        string directory = Path.Combine(Path.GetTempPath(), "stargate-weapons-" + Guid.NewGuid().ToString("N"));
        Guid player = Guid.NewGuid(), second = Guid.NewGuid(), itemId;
        SavedLocation location; WorldChanges state; Guid container;
        await using (var db = new WorldRepository(directory))
        {
            using var generation = new GenerationService(7319, "temperate");
            location = await db.ResolveAsync((await generation.Plan).Definition);
            var structure = new StructureRecord(Guid.NewGuid(), new Vector3(32, 0, 32), 0);
            var piece = new BuildingPiece(Guid.NewGuid(), structure.Id, PieceKind.Prefab, default, 0, 0, Prefab: "weapon-crate"); container = piece.Id;
            state = location.Changes with { Revision = 1, Structures = [structure], Pieces = [piece] };
            await db.SaveAsync(state, 0);
            var inv = await db.EnsureWeaponsAsync(player, false); itemId = inv.Slots[0]!.Id;
            Verify.Check(inv.Equipped is null, "migration respects saved holstered preference");
            Verify.Check((await db.EnsureWeaponsAsync(player, true)).Slots[0]!.Id == itemId, "one-time grant cannot repeat");
            var open = await db.WeaponsAsync(player, location.Id, new(Guid.NewGuid(), "open", Container: container));
            Verify.Check(open.Container!.Slots.All(i => i is null), "new container starts empty");
            var equip = await db.WeaponsAsync(player, location.Id, new(Guid.NewGuid(), "equip", inv.Revision, itemId));
            var request = new WeaponRequest(Guid.NewGuid(), "store", equip.Inventory.Revision, itemId, container, 0);
            var deposit = await db.WeaponsAsync(player, location.Id, request);
            Verify.Check(deposit.Success && deposit.Inventory.Equipped is null && deposit.Inventory.Slots.All(i => i is null), "deposit equipped item leaves empty hands");
            var retry = await db.WeaponsAsync(player, location.Id, request);
            Verify.Check(retry.Success && retry.Container!.Revision == deposit.Container!.Revision, "durable request replay does not transfer twice");
            bool conflict = false;
            try { await db.WeaponsAsync(player, location.Id, request with { Action = "take" }); } catch (InvalidOperationException) { conflict = true; }
            Verify.Check(conflict, "request IDs cannot be reused for different actions");
            bool removalBlocked = false;
            try { await db.SaveAsync(state with { Revision = 2, Pieces = [], Structures = [] }, 1); } catch (InvalidOperationException) { removalBlocked = true; }
            Verify.Check(removalBlocked && (await db.LoadAsync(location.Id)).Changes.Pieces.Length == 1, "nonempty container and parent removal rolled back atomically");
            var inv2 = await db.EnsureWeaponsAsync(second, true);
            var attempts = await Task.WhenAll(
                db.WeaponsAsync(player, location.Id, new(Guid.NewGuid(), "take", deposit.Inventory.Revision, itemId, container, 1)),
                db.WeaponsAsync(second, location.Id, new(Guid.NewGuid(), "take", inv2.Revision, itemId, container, 1)));
            Verify.Check(attempts.Count(r => r.Success) == 1, "simultaneous withdrawals have exactly one winner");
            var winner = attempts[0];
            Verify.Check(winner.Success && winner.Inventory.Equipped is null && winner.Inventory.Slots[0]!.Id == itemId, "retrieval uses first free slot without equipping");
            await db.SaveAsync(state with { Revision = 2, Pieces = [], Structures = [] }, 1);
            Verify.Check(!(await db.WeaponsAsync(player, location.Id, new(Guid.NewGuid(), "open", Container: container))).Success, "removed container cannot be recreated by opening it");
        }
        // Copy/migration fixture, never touching a user's save. Simulate a v8 database
        // with an existing saved appearance and verify a backed-up upgrade on reopen.
        string file = Path.Combine(directory, "default", "universe.sqlite");
        await using (var db = new WorldRepository(directory))
        {
            var inv = await db.EnsureWeaponsAsync(player, true);
            Verify.Check(inv.Slots.Count(i => i is not null) == 1 && inv.Slots[0]!.Id == itemId && inv.Equipped is null, "restart preserves inventory and empty hands");
        }
        using (var sql = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            sql.Open(); using var cmd = sql.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM weapon_items;";
            Verify.Check(Convert.ToInt32(cmd.ExecuteScalar()) == 2, "item-count conservation after races, retries and restart");
            cmd.CommandText = "DROP TABLE weapon_receipts; DROP TABLE weapon_items; DROP TABLE weapon_containers; DROP TABLE weapon_owners; DELETE FROM migrations WHERE version=9; PRAGMA user_version=8;"; cmd.ExecuteNonQuery();
        }
        await using (var db = new WorldRepository(directory))
        {
            var inv = await db.EnsureWeaponsAsync(player, false);
            Verify.Check(inv.Slots.Count(i => i is not null) == 1 && inv.Equipped is null, "v8 migration grants a holstered rifle once");
            Verify.Check(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.backup-*").Length > 0, "migration creates backup");
        }
        using (var sql = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            sql.Open(); using var cmd = sql.CreateCommand(); cmd.CommandText = "CREATE TRIGGER fail_weapon_commit BEFORE UPDATE ON weapon_owners BEGIN SELECT RAISE(ABORT,'injected'); END;"; cmd.ExecuteNonQuery();
        }
        await using (var db = new WorldRepository(directory))
        {
            var inv = await db.EnsureWeaponsAsync(player, true); bool failed = false;
            try { await db.WeaponsAsync(player, location.Id, new(Guid.NewGuid(), "equip", inv.Revision, inv.Slots[0]!.Id)); } catch (SqliteException) { failed = true; }
            var after = await db.EnsureWeaponsAsync(player, true);
            Verify.Check(failed && after.Revision == inv.Revision && after.Equipped is null, "interrupted transaction leaves state and revision unchanged");
        }
        Console.WriteLine($"WEAPON CHECKS PASSED ({Verify.Count}); copied-save fixture: {directory}");
    }
}
