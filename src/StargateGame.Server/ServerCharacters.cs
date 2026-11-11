using StargateGame.Shared;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    readonly Dictionary<int, CharacterPresentation> characterStates = [];
    bool HandleCharacter(Session s, Message m)
    {
        if (m.Kind != "character-action")
            return false;
        if (s.PlayerId == 0 || m.CharacterAction is not { } a || a.Sequence <= 0 || a.Action is not ("idle" or "fire" or "reload" or "draw" or "holster"))
            return true;
        var old = characterStates.GetValueOrDefault(s.PlayerId);
        if (old is not null && (a.Sequence <= old.Sequence || serverTick - old.StartTick < (old.Action == "fire" ? 6 : 2)))
            return true;
        bool equipped = WeaponEquipped(s);
        if (a.Equipped != equipped || !equipped && a.Action is "fire" or "reload") return true;
        var next = new CharacterPresentation(s.PlayerId, a.Sequence, equipped, equipped && a.Aiming, equipped ? a.Action : "holster", serverTick);
        characterStates[s.PlayerId] = next;
        Broadcast(new()
        {
            Kind = "character-state",
            Characters = [next],
            GateTick = serverTick
        });
        return true;
    }
    CharacterPresentation[] CharacterBaseline() => physics.Players.Select(p => characterStates.GetValueOrDefault(p.Id) ?? new(p.Id, 0, true, false, "idle", 0)).ToArray();
}
