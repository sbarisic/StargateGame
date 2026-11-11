using System.Numerics;
using StargateGame.Building;
using StargateGame.Shared;

namespace StargateGame.Server;

public sealed partial class GameServer
{
    sealed class WeaponAccess
    {
        public WeaponInventory? Inventory;
        public Task<WeaponInventory>? Initial;
        public Task<WeaponResult>? Pending;
        public Guid Location, Container;
        public WeaponContainer? Snapshot;
        public WeaponRequest? Command;
        public readonly Dictionary<Guid, WeaponResult> Receipts = [];
    }
    readonly Dictionary<Session, WeaponAccess> weaponAccess = [];
    readonly Dictionary<Guid, WeaponInventory> fixtureWeapons = [];
    readonly Dictionary<Guid, WeaponContainer> fixtureContainers = [];
    bool WeaponEquipped(Session session) => weaponAccess.TryGetValue(session, out var access) && access.Inventory?.Equipped is not null;
    bool CanAccessWeaponContainer(Session s, Guid location, Guid container)
    {
        if (s.Peer.Closed || s.PlayerId == 0 || location != physics.World.Definition.LocationId ||
            physics.Player(s.PlayerId).TransitFrozen || HomeDebugBusy || edit is not null || transition is not null || preparing is not null) return false;
        var piece = CurrentChanges.Pieces.FirstOrDefault(p => p.Id == container && WeaponCatalog.IsContainer(p.Prefab));
        if (piece is null) return false;
        var bounds = BuildingGeometry.Bounds(BuildingGeometry.Mesh(CurrentChanges, piece));
        var eye = Movement.EyePosition(physics.Player(s.PlayerId));
        var point = Vector3.Clamp(eye, bounds.Min, bounds.Max);
        float distance = Vector3.Distance(eye, point);
        return distance <= WeaponCatalog.Reach && (distance < .05f || !physics.InteractionBlocked(eye,
            Vector3.Normalize(point - eye), distance - .03f, ignorePlayer: s.PlayerId));
    }
    bool HandleWeapons(Session s, Message message)
    {
        if (message.Kind != "weapon-command") return false;
        if (message.WeaponCommand is not { } request || !weaponAccess.TryGetValue(s, out var access) || access.Inventory is null) return true;
        void Fail(string reason) => s.Peer.Send(new() { Kind = "weapon-state", Weapons = new(request.Id, false, reason, access.Inventory,
            access.Snapshot?.Piece == access.Container ? access.Snapshot : null) });
        if (access.Pending is not null)
        {
            if (access.Command?.Id != request.Id) Fail("Previous weapon request is still committing.");
            return true;
        }
        if (physics.Player(s.PlayerId).TransitFrozen || HomeDebugBusy) { Fail("Equipment is unavailable during travel or world updates."); return true; }
        if (request.Id == Guid.Empty) { Fail("Invalid request ID."); return true; }
        if (request.Action is "open" or "store" or "take")
        {
            if (request.Action != "open" && (access.Container != request.Container || access.Location != message.LocationId) ||
                !CanAccessWeaponContainer(s, message.LocationId, request.Container))
            { access.Container = Guid.Empty; Fail("Container is out of reach, obstructed or unavailable."); return true; }
            access.Container = request.Container; access.Location = message.LocationId;
        }
        else if (request.Action == "close") access.Container = Guid.Empty;
        // Commands unrelated to storage must not open arbitrary container identities.
        if (request.Action is not ("open" or "store" or "take")) request = request with { Container = Guid.Empty };
        access.Command = request;
        if (repository is not null)
            access.Pending = repository.WeaponsAsync(s.SavedPlayer!.Id, message.LocationId, request);
        else
        {
            WeaponContainer? container = null;
            if (request.Container != Guid.Empty)
            {
                if (!fixtureContainers.TryGetValue(request.Container, out container))
                    fixtureContainers[request.Container] = container = new(message.LocationId, request.Container, 0, new WeaponItem?[WeaponCatalog.ContainerSlots]);
            }
            var result = access.Receipts.GetValueOrDefault(request.Id) ?? WeaponCatalog.Apply(access.Inventory, container, request);
            if (result.Success && result.Container is { } updated) fixtureContainers[updated.Piece] = updated;
            access.Receipts[request.Id] = result;
            access.Pending = Task.FromResult(result);
        }
        return true;
    }
    void TickWeapons()
    {
        foreach (var s in sessions.Where(s => s.PlayerId != 0 && s.SavedPlayer is not null))
        {
            if (!weaponAccess.TryGetValue(s, out var a))
            {
                weaponAccess[s] = a = new();
                a.Initial = repository is not null ? repository.EnsureWeaponsAsync(s.SavedPlayer!.Id, s.SavedPlayer.Appearance?.Equipped ?? true)
                    : Task.FromResult(fixtureWeapons.GetValueOrDefault(s.SavedPlayer!.Id) ?? WeaponCatalog.Starter(s.SavedPlayer.Appearance?.Equipped ?? true));
            }
            if (a.Initial is { IsCompleted: true } initial)
            {
                a.Initial = null;
                if (!initial.IsCompletedSuccessfully) { Reject(s, "Unable to load weapon inventory."); continue; }
                a.Inventory = initial.Result;
                PublishWeapons(s, a, new(Guid.Empty, true, "", a.Inventory));
            }
            if (a.Pending is { IsCompleted: true } pending)
            {
                a.Pending = null;
                var result = pending.IsCompletedSuccessfully ? pending.Result : new WeaponResult(a.Command!.Id, false, "Weapon transaction failed; nothing was acknowledged.", a.Inventory!);
                if (result.Inventory.Revision >= (a.Inventory?.Revision ?? -1)) a.Inventory = result.Inventory;
                if (result.Container is { } snapshot && snapshot.Piece == a.Container &&
                    (a.Snapshot?.Piece != snapshot.Piece || snapshot.Revision >= a.Snapshot.Revision)) a.Snapshot = snapshot;
                PublishWeapons(s, a, result with { Inventory = a.Inventory!, Container = a.Container == a.Snapshot?.Piece ? a.Snapshot : null });
                if (result.Success && a.Command?.Action is "store" or "take" && result.Container is { } changed)
                    foreach (var other in weaponAccess.Where(p => p.Key != s && p.Value.Container == changed.Piece && p.Value.Location == changed.Location && p.Value.Inventory is not null))
                    {
                        other.Value.Snapshot = changed;
                        other.Key.Peer.Send(new() { Kind = "weapon-state", Weapons = new(Guid.Empty, true, "", other.Value.Inventory!, changed) });
                    }
                a.Command = null;
            }
            if (a.Container != Guid.Empty && !CanAccessWeaponContainer(s, a.Location, a.Container))
            {
                a.Container = Guid.Empty;
                if (a.Inventory is not null) s.Peer.Send(new() { Kind = "weapon-state", Weapons = new(Guid.Empty, false, "Container access closed.", a.Inventory) });
            }
        }
        foreach (var pair in weaponAccess.Where(p => p.Key.Peer.Closed && (p.Value.Pending is null || p.Value.Pending.IsCompleted) && (p.Value.Initial is null || p.Value.Initial.IsCompleted)).ToArray()) weaponAccess.Remove(pair.Key);
    }
    void PublishWeapons(Session s, WeaponAccess a, WeaponResult result)
    {
        if (repository is null) fixtureWeapons[s.SavedPlayer!.Id] = a.Inventory!;
        bool equipped = a.Inventory!.Equipped is not null;
        var old = characterStates.GetValueOrDefault(s.PlayerId);
        if (old is null || old.Equipped != equipped)
        {
            var state = new CharacterPresentation(s.PlayerId, (old?.Sequence ?? 0) + 1, equipped, false, equipped ? "draw" : "holster", serverTick);
            characterStates[s.PlayerId] = state;
            Broadcast(new() { Kind = "character-state", Characters = [state], GateTick = serverTick });
        }
        s.Peer.Send(new() { Kind = "weapon-state", Weapons = result });
    }
}
