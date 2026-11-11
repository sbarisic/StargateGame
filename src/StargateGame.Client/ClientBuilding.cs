using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class ClientSession
{
    WorldStateReceiver? incomingWorld;
    long incomingWorldTransfer;
    WorldChanges? preparedChanges;
    public WorldChanges Changes => Physics.Changes;
    public BuildingMetrics? BuildingCounters
    {
        get;
        private set;
    }
    public string BuildStatus
    {
        get;
        private set;
    } = "";
    public BuildResult? LastBuildResult
    {
        get;
        private set;
    }
    double BuildFeedbackUntil;
    public string BuildAction { get; private set; } = "";
    public string Feedback(string action) => action == BuildAction && (BuildPending || Now < BuildFeedbackUntil) ? BuildStatus : "";
    public string InteractionFeedback => Feedback("door");
    public bool BuildPending
    {
        get;
        private set;
    }
    public void Build(BuildCommand command)
    {
        if (BuildPending) return;
        BuildAction = command.Action;
        if (TransitionPending || PlanetDebugState?.Busy == true)
        {
            BuildStatus = "World update in progress.";
            BuildFeedbackUntil = Now + 3;
            return;
        }
        BuildPending = true;
        LastBuildResult = null;
        BuildStatus = command.Action == "remove" ? "Demolishing..." : "Requesting world change...";
        connection.Peer.Send(new()
        {
            Kind = "build",
            Build = command,
            LocationId = World.Definition.LocationId,
            Epoch = Physics.Epoch
        });
    }
    bool HandleBuilding(Message message)
    {
        if (message.Kind == "building-metrics")
        {
            BuildingCounters = message.BuildingMetrics ?? throw new InvalidDataException("Missing building metrics.");
            return true;
        }
        if (message.Kind == "world-state")
        {
            if (message.TransferId <= lastCancelledTransfer)
                return true;
            if (incomingWorld is null) { incomingWorld = new(); incomingWorldTransfer = message.TransferId; }
            else if (incomingWorldTransfer != message.TransferId) throw new InvalidDataException("World state transfer identity mismatch.");
            incomingWorld.Add(message.WorldPart ?? throw new InvalidDataException("Missing world state fragment."));
            return true;
        }
        if (message.Kind == "build-pending")
        {
            BuildStatus = BuildAction == "remove" ? "Demolishing..." : message.Reason ?? "Preparing world change...";
            return true;
        }
        if (message.Kind == "build-result")
        {
            LastBuildResult = message.BuildResult ?? throw new InvalidDataException("Missing build result.");
            BuildStatus = LastBuildResult.Reason;
            BuildFeedbackUntil = LastBuildResult.Success ? 0 : Now + 3;
            BuildPending = false;
            if (!LastBuildResult.Success)
                AudioErrors++;
            return true;
        }
        return false;
    }
}
