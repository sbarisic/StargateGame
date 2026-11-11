using System.Diagnostics;
using System.Net;
using System.Numerics;
using StargateGame.Client;
using StargateGame.Persistence;
using StargateGame.Physics;
using StargateGame.Server;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class WeaponNetworkChecks
{
    public static async Task Run(bool visual = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "stargate-weapon-network-" + Guid.NewGuid().ToString("N"));
        var plan = new PlanetPlan(7319, "temperate", settlements: new("wilderness"));
        var spawn = plan.Definition.Spawns[0];
        var anchor = spawn + new Vector3(0, 0, -1.8f);
        anchor.Y = plan.BuildChunk(ChunkCoordinate.At(anchor.X, anchor.Z)).Sample(anchor.X, anchor.Z);
        var structure = new StructureRecord(Guid.NewGuid(), anchor, 0);
        var chest = new BuildingPiece(Guid.NewGuid(), structure.Id, PieceKind.Prefab, default, 0, 0, Prefab: "weapon-crate");
        Guid locationId;
        await using (var repository = new WorldRepository(directory))
        {
            var saved = await repository.ResolveAsync(plan.Definition);
            locationId = saved.Id;
            var removed = ChunkCoordinate.At(anchor.X, anchor.Z).Around(1).Select(plan.BuildChunk)
                .SelectMany(c => TreeScenery.Resolve(c, "temperate").Select(t => new SceneryRemoval("tree", t.Id, c.Coordinate))
                    .Concat(RockScenery.Resolve(c, "temperate").Select(t => new SceneryRemoval("rock", t.Id, c.Coordinate)))).ToArray();
            await repository.SaveAsync(saved.Changes with { Revision = 1, Structures = [structure], Pieces = [chest], Removed = removed }, 0);
        }
        await using var server = new GameServer(IPAddress.Loopback, 0, 4, 7319, "temperate", true, directory, settlements: new("wilderness"), allowTestIdentities: true); server.Start();
        if (visual)
        {
            Console.WriteLine($"WEAPON VISUAL FIXTURE port={server.Port} save={directory}");
            var placed = new HashSet<int>();
            for (int i = 0; i < 900; i++)
            {
                await server.InspectPhysicsAsync(locationId, world =>
                {
                    if (world.Players.All(p => placed.Contains(p.Id))) return;
                    world.Apply(world.Snapshot() with { Players = world.Players.Select(p => placed.Add(p.Id)
                        ? p with { Position = anchor + new Vector3(p.Id % 2 == 0 ? .6f : -.6f, .05f, 1.6f), Velocity = Vector3.Zero } : p).ToArray() });
                });
                await Task.Delay(100);
            }
            return;
        }
        using var identity = new PlayerIdentity();
        var firstConnection = await GameConnection.ConnectAsync("127.0.0.1", server.Port, "Store", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: identity);
        using var a = new ClientSession(firstConnection);
        using var b = new ClientSession(await NetworkChecks.Connect(server.Port, "Take"));
        async Task Pump(int ms)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < ms)
            {
                a.Poll(); b.Poll();
                if (a.Failure is not null || b.Failure is not null) throw new Exception(a.Failure ?? b.Failure);
                a.Step(0, 0, 0, 0, false, false); b.Step(0, 0, 0, 0, false, false);
                await Task.Delay(5);
            }
        }
        async Task Await(Func<bool> condition)
        { for (int i = 0; i < 200 && !condition(); i++) await Pump(50); Verify.Check(condition(), "network response completed"); }
        await Await(() => a.Inventory is not null && b.Inventory is not null);
        var rifle = a.Inventory!.Slots[0]!;
        a.Weapon("holster"); await Await(() => !a.WeaponPending);
        await Pump(150);
        Verify.Check(a.Inventory.Equipped is null && b.Characters[a.Local.Id].Equipped == false, "holster replicated to observer");
        a.Character(true, true, "fire"); await Pump(150);
        Verify.Check(!b.Characters[a.Local.Id].Equipped, "forged character action cannot equip an unselected weapon");
        a.Weapon("equip", rifle.Id); await Await(() => !a.WeaponPending);
        await server.InspectPhysicsAsync(a.World.Definition.LocationId, world => world.Apply(world.Snapshot() with
        { Players = world.Players.Select(p => p with { Position = anchor + new Vector3(p.Id == a.Local.Id ? -.6f : .6f, .05f, 1.6f), Velocity = Vector3.Zero }).ToArray() }));
        await Pump(400);
        a.Weapon("open", container: chest.Id); b.Weapon("open", container: chest.Id);
        await Await(() => !a.WeaponPending && !b.WeaponPending);
        Verify.Check(a.OpenWeaponContainer?.Piece == chest.Id && b.OpenWeaponContainer?.Piece == chest.Id, "two players share one container with validated reach");
        a.Weapon("holster"); await Await(() => !a.WeaponPending);
        Verify.Check(a.OpenWeaponContainer?.Piece == chest.Id, "personal equipment actions preserve shared container access");
        a.Weapon("equip", rifle.Id); await Await(() => !a.WeaponPending);
        a.Weapon("store", rifle.Id); await Await(() => !a.WeaponPending); await Pump(100);
        Verify.Check(a.Inventory.Equipped is null && b.OpenWeaponContainer!.Slots.Any(i => i?.Id == rifle.Id), "committed deposit replicated to all viewers");
        a.Weapon("take", rifle.Id); b.Weapon("take", rifle.Id);
        await Await(() => !a.WeaponPending && !b.WeaponPending); await Pump(150);
        Verify.Check(a.Inventory.Slots.Concat(b.Inventory!.Slots).Count(i => i?.Id == rifle.Id) == 1, "multiplayer withdrawal race conserves the item");
        await server.InspectPhysicsAsync(a.World.Definition.LocationId, world =>
            world.AddStaticBox(anchor + new Vector3(0, .9f, .8f), new Vector3(1.4f, 1.2f, .1f)));
        await Pump(250);
        Verify.Check(a.OpenWeaponContainer is null && b.OpenWeaponContainer is null, "new line-of-sight obstruction revokes open container access");
        a.Weapon("open", container: chest.Id); await Await(() => !a.WeaponPending);
        Verify.Check(a.OpenWeaponContainer is null && a.WeaponStatus.Contains("obstructed"), "server rejects opening storage through a wall");
        await server.InspectPhysicsAsync(a.World.Definition.LocationId, world => world.Apply(world.Snapshot() with
        { Players = world.Players.Select(p => p with { Position = p.Position + new Vector3(6, 0, 0), Velocity = Vector3.Zero }).ToArray() }));
        await Pump(350);
        Verify.Check(a.OpenWeaponContainer is null && b.OpenWeaponContainer is null, "walking away closes shared access");
        a.Weapon("open", container: chest.Id); await Await(() => !a.WeaponPending);
        Verify.Check(a.OpenWeaponContainer is null, "remote storage access rejected");
        var beforeReconnect = a.Inventory;
        firstConnection.Peer.Close("Weapon reconnect regression");
        for (int i = 0; i < 50; i++) { b.Poll(); b.Step(0, 0, 0, 0, false, false); await Task.Delay(10); }
        using var reconnected = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", server.Port, "Store", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: identity));
        for (int i = 0; i < 400 && reconnected.Inventory is null; i++)
        {
            reconnected.Poll(); b.Poll(); reconnected.Step(0, 0, 0, 0, false, false); b.Step(0, 0, 0, 0, false, false);
            if (reconnected.Failure is not null) throw new Exception(reconnected.Failure);
            await Task.Delay(10);
        }
        Verify.Check(reconnected.Inventory is { } restored && restored.Revision == beforeReconnect.Revision &&
            restored.Equipped == beforeReconnect.Equipped && restored.Slots.Select(i => i?.Id).SequenceEqual(beforeReconnect.Slots.Select(i => i?.Id)),
            "authenticated reconnect preserves exact slots and equipment without a replacement grant");
        Console.WriteLine("WEAPON MULTIPLAYER CHECKS PASSED; save: " + directory);
    }
}
