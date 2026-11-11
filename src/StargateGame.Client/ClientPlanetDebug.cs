using StargateGame.Shared;
namespace StargateGame.Client;
sealed partial class ClientSession
{
    long regenerationStarted;
    public bool PlanetVisualPending { get; private set; }
    public void PlanetVisualsReady() { PlanetVisualPending = false; Console.WriteLine($"PLANET DEBUG controllable frame ready {System.Diagnostics.Stopwatch.GetElapsedTime(regenerationStarted).TotalSeconds:F2}s"); }
    public PlanetDefinition? DebugHome { get; private set; }
    public PlanetDebugStatus? PlanetDebugState { get; private set; }
    public void ReadPlanetDebug() => connection.Peer.Send(new() { Kind = "planet-debug-read" });
    public void ApplyPlanetDebug(PlanetDebugSettings settings, bool regenerate)
    {
        settings.Validate();
        if (!Developer || PlanetDebugState?.Busy == true) return;
        if (regenerate) regenerationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var id = Guid.NewGuid();
        PlanetDebugState = new(id, "Requested", "Waiting for server", 0, true, false, DebugHome);
        connection.Peer.Send(new() { Kind = regenerate ? "planet-debug-regenerate" : "planet-debug-environment", PlanetDebug = new(id, settings) });
    }
    public void CancelPlanetDebug()
    {
        if (PlanetDebugState is { CanCancel: true } status && DebugHome is not null)
            connection.Peer.Send(new() { Kind = "planet-debug-cancel", PlanetDebug = new(status.RequestId, PlanetDebugSettings.Read(DebugHome)) });
    }
    bool HandlePlanetDebug(Message message)
    {
        if (message.Kind == "planet-debug-status" && message.PlanetDebugState is { } state)
        {
            if (state.RequestId == Guid.Empty && PlanetDebugState?.Busy == true) { DebugHome = state.Home; return true; }
            PlanetDebugState = state;
            DebugHome = state.Home;
            TerrainStatus = state.Phase + ": " + state.Detail;
            Console.WriteLine($"PLANET DEBUG {state.Phase} {state.ElapsedSeconds:F2}s: {state.Detail}");
            return true;
        }
        if (message.Kind == "planet-environment" && message.LocationId == World.Definition.LocationId && message.PlanetEnvironment is { } environment)
        {
            environment.Validate(); World.SetEnvironment(environment); return true;
        }
        return false;
    }
}
