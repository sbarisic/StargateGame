using StargateGame.Shared;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    readonly Dictionary<int, CharacterPresentation> characterStates = [];
    long nextShot;
    bool Reloading(Session s) => characterStates.GetValueOrDefault(s.PlayerId) is { Action: "reload", ReloadUntil: > 0 };
    void UpdateAmmo(Session s, WeaponAccess access, WeaponItem item)
    {
        var inventory = access.Inventory!;
        access.Inventory = inventory with { Revision = inventory.Revision+1, Slots = inventory.Slots.Select(i => i?.Id == item.Id ? item : i).ToArray() };
        if (repository is not null) access.AmmoSave = repository.SaveAmmoAsync(s.SavedPlayer!.Id, access.Inventory, item);
        else fixtureWeapons[s.SavedPlayer!.Id] = access.Inventory;
        s.Peer.Send(new() { Kind = "weapon-state", Weapons = new(Guid.Empty, true, "", access.Inventory, access.Snapshot) });
    }
    bool HandleCharacter(Session s, Message m)
    {
        if (m.Kind != "character-action") return false;
        if (s.PlayerId == 0 || m.CharacterAction is not { } a || a.Sequence <= 0 || !float.IsFinite(a.Yaw+a.Pitch) || Math.Abs(a.Yaw)>10000 || Math.Abs(a.Pitch)>MathF.PI/2 || a.Action is not ("idle" or "fire" or "reload" or "draw" or "holster")) return true;
        var old = characterStates.GetValueOrDefault(s.PlayerId) ?? new(s.PlayerId, 0, WeaponEquipped(s), false, "idle", 0);
        if (a.Sequence <= old.Sequence) return true;
        void RejectAction()
        {
            var state = old with { Sequence = a.Sequence };
            characterStates[s.PlayerId] = state;
            s.Peer.Send(new() { Kind = "character-state", Characters = [state], GateTick = serverTick });
        }
        bool equipped = WeaponEquipped(s);
        if (Reloading(s) || physics.Player(s.PlayerId).TransitFrozen || a.Equipped != equipped ||
            !weaponAccess.TryGetValue(s, out var access) || access.Inventory is null || access.Pending is not null ||
            access.AmmoSave is { IsCompletedSuccessfully: false }) { RejectAction(); return true; }
        var rifle = access.Inventory.Slots.FirstOrDefault(i => i?.Id == access.Inventory.Equipped);
        RifleShot? shot = null;
        long reloadUntil = 0;
        if (a.Action is "fire" or "reload")
        {
            if (!equipped || rifle is null || a.Action == "fire" && serverTick-access.LastFire < Rifle.FireTicks) { RejectAction(); return true; }
            if (a.Action == "reload")
            {
                if (!Rifle.CanReload(rifle)) { RejectAction(); return true; }
                reloadUntil = serverTick+Rifle.ReloadTicks;
                EmitSound(WorldSoundKind.RifleReload, Movement.EyePosition(physics.Player(s.PlayerId)), sourcePlayer: s.PlayerId);
            }
            else
            {
                access.LastFire = serverTick;
                if (!rifle.Chambered)
                {
                    EmitSound(WorldSoundKind.RifleDry, Movement.EyePosition(physics.Player(s.PlayerId)), sourcePlayer: s.PlayerId);
                    RejectAction(); return true;
                }
                UpdateAmmo(s, access, Rifle.Fire(rifle));
                var hit = physics.ShootRifle(s.PlayerId,a.Yaw,a.Pitch);
                var origin = Movement.EyePosition(physics.Player(s.PlayerId));
                shot = new(++nextShot, s.PlayerId, serverTick, origin, hit.End, hit.Hit, hit.Body);
                EmitSound(WorldSoundKind.RifleFire, origin, sourcePlayer: s.PlayerId);
                if (hit.Hit) EmitSound(WorldSoundKind.RifleImpact, hit.End, .6f);
                HearNpcs(origin, true, s.PlayerId);
            }
        }
        var next = new CharacterPresentation(s.PlayerId, a.Sequence, equipped, equipped && a.Aiming && reloadUntil == 0,
            equipped ? a.Action : "holster", serverTick, reloadUntil);
        characterStates[s.PlayerId] = next;
        Broadcast(new() { Kind = "character-state", Characters = [next], GateTick = serverTick, Shot = shot });
        return true;
    }
    CharacterPresentation[] CharacterBaseline() => physics.Players.Select(p => characterStates.GetValueOrDefault(p.Id) ?? new(p.Id, 0, true, false, "idle", 0)).ToArray();
}
