namespace StargateGame.Shared;

public sealed record CharacterAction(long Sequence, bool Equipped, bool Aiming, string Action, float Yaw = 0, float Pitch = 0);
public sealed record CharacterPresentation(int PlayerId, long Sequence, bool Equipped, bool Aiming, string Action, long StartTick, long ReloadUntil = 0);
