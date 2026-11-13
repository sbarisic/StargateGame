using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class ClientSession
{
    public readonly Dictionary<int, CharacterPresentation> Characters = [];
    long characterSequence;
    public readonly Queue<RifleShot> RifleShots = [];
    long lastRifleShot;
    long pendingReload;
    public bool Reloading => pendingReload > 0 || LocalCharacter.Action == "reload" && LocalCharacter.ReloadUntil > 0;
    public WeaponItem? EquippedRifle => Inventory?.Slots.FirstOrDefault(i => i?.Id == Inventory.Equipped);
    public CharacterPresentation LocalCharacter => Characters.GetValueOrDefault(localId) ?? new(localId, 0, true, false, "idle", 0);
    public void Character(bool equipped, bool aim, string action)
    {
        if (PlanetDebugState?.Busy == true || Reloading) return;
        characterSequence = Math.Max(characterSequence, LocalCharacter.Sequence);
        var cmd = new CharacterAction(++characterSequence, equipped, aim, action, Local.Yaw, Local.Pitch);
        if(action=="reload")pendingReload=cmd.Sequence;
        connection.Peer.Send(new()
        {
            Kind = "character-action",
            LocationId = World.Definition.LocationId,
            CharacterAction = cmd
        });
    }
    bool HandleCharacters(Message m)
    {
        if (m.Shot is { } shot && shot.Id > lastRifleShot)
        {
            lastRifleShot = shot.Id;
            if (RifleShots.Count < 32) RifleShots.Enqueue(shot);
        }
        if (m.Characters is { } states)
        {
            if (states.Length > 8)
                throw new InvalidDataException("Too many characters.");
            foreach (var s in states)
            {
                if(s.PlayerId==localId && s.Sequence>=pendingReload)pendingReload=0;
                if (s.PlayerId != localId || s.Sequence >= LocalCharacter.Sequence)
                    Characters[s.PlayerId] = s;
            }
        }
        return m.Kind == "character-state";
    }
}
