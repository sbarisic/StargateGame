using System.Numerics;

namespace StargateGame.Shared;

public enum GatePhase
{
    Idle, Dialing, Preparing, Opening, Open, Closing, Failed
}
public sealed record DhdCommand(Guid RequestId, Guid LocationId, Guid DhdId, long Epoch, long Revision, string Action, int Key, Vector3 Aim);
public sealed record DhdResult(Guid RequestId, bool Accepted, string Reason, long Revision);
public sealed record GateState(Guid LocationId, Guid GateId, Guid DhdId, long Revision, GatePhase Phase, int[] Symbols, int Owner, long PhaseTick, Guid ConnectionId, bool Incoming = false, string Status = "", int PressKey = -1, long PressTick = 0)
{
    public bool Settled => Phase == GatePhase.Open;
    public bool Surface => Phase is GatePhase.Opening or GatePhase.Open or GatePhase.Closing;
    public int AcceptedCount => Math.Min(7, Symbols.Length);
}
public sealed record ConnectionState(Guid Id, Guid Source, Guid Destination, long StartTick, long CloseTick, GatePhase Phase);
public sealed record DeveloperDestination(string Name, int[] Symbols, bool DhdCompatible, bool GameAuthored);
