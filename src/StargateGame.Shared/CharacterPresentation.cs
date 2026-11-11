namespace StargateGame.Shared;

public sealed record CharacterAction(long Sequence, bool Equipped, bool Aiming, string Action);
public sealed record CharacterPresentation(int PlayerId, long Sequence, bool Equipped, bool Aiming, string Action, long StartTick);
