using System.Collections.Concurrent;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    sealed record RebindRequest(Guid Player, string Key, TaskCompletionSource Completion);
    readonly ConcurrentQueue<RebindRequest> adminRequests = new();
    RebindRequest? adminCurrent;
    Task? adminWrite;
    public Task RebindPlayerAsync(Guid player, string replacementFingerprint)
    {
        if (repository is null)
            throw new InvalidOperationException("Player recovery requires a persistent universe.");
        if (adminRequests.Count >= 8)
            throw new InvalidOperationException("Administration queue is full.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adminRequests.Enqueue(new(player, replacementFingerprint, completion));
        return completion.Task;
    }
    internal int DiagnosticDestinationDelayMilliseconds
    {
        get;
        set;
    }
    readonly ConcurrentQueue<(Guid Location, Action<StargateGame.Physics.PhysicsWorld> Edit, TaskCompletionSource Done)> diagnostics = new();
    internal Task InspectPhysicsAsync(Guid location, Action<StargateGame.Physics.PhysicsWorld> edit)
    {
        if (!allowTestIdentities || !devPlanets)
            throw new InvalidOperationException("Physics inspection requires explicit server testing flags.");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        diagnostics.Enqueue((location, edit, done));
        return done.Task;
    }
    internal Task InspectConnectionAgeAsync(Guid location, long openAgeTicks)
    {
        if (openAgeTicks < 0 || openAgeTicks > StargateGame.Shared.GateTravel.MaximumConnectionTicks)
            throw new ArgumentOutOfRangeException(nameof(openAgeTicks));
        return InspectPhysicsAsync(location, _ =>
        {
            long start = serverTick - openAgeTicks - StargateGame.Shared.GateEffectTimeline.OpeningTicks;
            foreach (var link in links.Values.Where(l => l.Source.Physics.World.Definition.LocationId == location))
                link.Opening = start;
            if (fixtureConnection is { } connection)
                fixtureConnection = connection with
                {
                    StartTick = start
                };
        });
    }
    void TickAdministration()
    {
        testCommands.Drain(ExecuteTestCommand);
        PumpTestPreparation();
        while (diagnostics.TryDequeue(out var diagnostic))
        {
            try
            {
                var c = contexts.Single(c => c.Physics.World.Definition.LocationId == diagnostic.Location);
                diagnostic.Edit(c.Physics);
                c.Physics.Epoch++;
                diagnostic.Done.TrySetResult();
            }
            catch (Exception ex)
            {
                diagnostic.Done.TrySetException(ex);
            }
        }
        if (adminCurrent is null && !adminRequests.TryDequeue(out adminCurrent))
            return;
        var request = adminCurrent!;
        if (adminWrite is null)
        {
            foreach (var c in contexts)
                foreach (var s in c.Sessions.Where(s => s.SavedPlayer?.Id == request.Player && !s.Peer.Closed))
                {
                    var previous = context;
                    context = c;
                    SavePlayer(s);
                    context = previous;
                    s.Peer.Close("Administrator replaced the player identity key.");
                }
            if (travels.Values.Any(t => t.Record.Player?.Id == request.Player))
                return;
            adminWrite = repository!.RebindPlayerAsync(request.Player, request.Key);
        }
        if (!adminWrite.IsCompleted)
            return;
        if (adminWrite.IsCompletedSuccessfully)
            request.Completion.TrySetResult();
        else
            request.Completion.TrySetException(adminWrite.Exception!.GetBaseException());
        adminWrite = null;
        adminCurrent = null;
    }
}
