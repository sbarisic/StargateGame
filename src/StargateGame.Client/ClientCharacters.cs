using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class ClientSession
{
    public readonly Dictionary<int, CharacterPresentation> Characters = [];
    long characterSequence;
    public CharacterPresentation LocalCharacter => Characters.GetValueOrDefault(localId) ?? new(localId, 0, true, false, "idle", 0);
    public void Character(bool equipped, bool aim, string action)
    {
        if (PlanetDebugState?.Busy == true) return;
        characterSequence = Math.Max(characterSequence, LocalCharacter.Sequence);
        var cmd = new CharacterAction(++characterSequence, equipped, aim, action);
        Characters[localId] = new(localId, cmd.Sequence, equipped, aim, action, (long)GateTime);
        connection.Peer.Send(new()
        {
            Kind = "character-action",
            LocationId = World.Definition.LocationId,
            CharacterAction = cmd
        });
    }
    bool HandleCharacters(Message m)
    {
        if (m.Characters is { } states)
        {
            if (states.Length > 8)
                throw new InvalidDataException("Too many characters.");
            foreach (var s in states)
                if (s.PlayerId != localId || s.Sequence >= LocalCharacter.Sequence)
                    Characters[s.PlayerId] = s;
        }
        return m.Kind == "character-state";
    }
}
