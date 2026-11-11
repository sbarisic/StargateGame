using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class ClientSession
{
    readonly HashSet<Guid> completedTransits = [];
    public TransitNotice? Transit
    {
        get;
        private set;
    }
    double fadeStarted, fadeBackStarted, confirmedAt;
    bool TransitReady => Transit is not null && TransitWhite >= 1 && Now - confirmedAt >= GateTravel.MinimumTransitSeconds;
    bool predictingTransit, awaitingArrivalFrame;
    public bool AwaitingArrivalFrame => awaitingArrivalFrame;
    public bool TransitLocked => predictingTransit || Transit is { Phase: not TransitPhase.Cancelled } || awaitingArrivalFrame;
    public float TransitWhite
    {
        get
        {
            if (predictingTransit && Now - fadeStarted > 2 && Transit is null)
            {
                predictingTransit = false;
                fadeBackStarted = Now;
            }
            if (fadeBackStarted > 0)
                return (float)Math.Clamp(1 - (Now - fadeBackStarted) / .15, 0, 1);
            return TransitLocked ? (float)Math.Clamp((Now - fadeStarted) / .15, 0, 1) : 0;
        }
    }
    public (float Yaw, float Pitch)? ArrivalView
    {
        get;
        private set;
    }
    public void ConsumeArrivalView() => ArrivalView = null;
    public string TransitDiagnostic => Transit is { } t ? $"{t.Phase} / {Now - fadeStarted:F1}s / {t.Reason ?? ""}" : predictingTransit ? "Predicted entry" : "Idle";
    bool HandleTransit(Message message)
    {
        if (message.Kind != "transit")
            return false;
        if (message.Transit is not { } notice || notice.Id == Guid.Empty || notice.Source != World.Definition.LocationId || completedTransits.Contains(notice.Id))
            return true;
        if (Transit is { } current && current.Id != notice.Id)
            return true;
        if (Transit is null)
        {
            confirmedAt = Now;
            fadeStarted = Now;
            fadeBackStarted = 0;
            Console.WriteLine($"CLIENT TRANSIT {notice.Id}: captured, preload={Destination?.Stage}, nativeReady={Destination?.PhysicsReady}");
        }
        predictingTransit = false;
        Transit = notice;
        if (notice.Phase == TransitPhase.Cancelled)
        {
            completedTransits.Add(notice.Id);
            Transit = null;
            fadeBackStarted = Now;
            awaitingArrivalFrame = false;
        }
        return true;
    }
    void PredictTransit(PlayerState before, PlayerState after)
    {
        if (!TransitLocked && Physics.TravelGate is { } gate && GateTravel.PlayerCrossing(gate, before.Position, after.Position, Movement.BodyHeight(after)))
        {
            predictingTransit = true;
            fadeStarted = Now;
            fadeBackStarted = 0;
        }
    }
    void Arrived(Message message)
    {
        if (message.Transit is not { } notice)
            return;
        Transit = notice;
        predictingTransit = false;
        awaitingArrivalFrame = true;
        sequence = 0;
        pendingJump = false;
        Console.WriteLine($"CLIENT TRANSIT {notice.Id}: committed after {Now - confirmedAt:F3}s, physics promotions={PreloadPhysicsPromotions}");
        ArrivalView = (Local.Yaw, Local.Pitch);
    }
    public void DestinationFrameRendered(bool complete)
    {
        if (!awaitingArrivalFrame || !complete || Transit is not { Phase: TransitPhase.Released })
            return;
        Console.WriteLine($"CLIENT TRANSIT {Transit.Id}: landing frame ready after {Now - confirmedAt:F3}s");
        completedTransits.Add(Transit.Id);
        if (completedTransits.Count > 64)
            completedTransits.Remove(completedTransits.First());
        awaitingArrivalFrame = false;
        Transit = null;
        fadeBackStarted = Now;
        connection.Peer.Send(new()
        {
            Kind = "transit-applied",
            LocationId = World.Definition.LocationId
        });
    }
}
