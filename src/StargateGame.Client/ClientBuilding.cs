using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class ClientSession
{
    WorldStateReceiver? incomingWorld;
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
    public bool BuildPending
    {
        get;
        private set;
    }
    public void Build(BuildCommand command)
    {
        if (BuildPending || TransitionPending || PlanetDebugState?.Busy == true)
        {
            BuildStatus = "World update in progress.";
            return;
        }
        BuildPending = true;
        LastBuildResult = null;
        BuildStatus = "Requesting world change...";
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
            incomingWorld ??= new();
            incomingWorld.Add(message.WorldPart ?? throw new InvalidDataException("Missing world state fragment."));
            return true;
        }
        if (message.Kind == "build-pending")
        {
            BuildStatus = message.Reason ?? "Preparing world change...";
            return true;
        }
        if (message.Kind == "build-result")
        {
            LastBuildResult = message.BuildResult ?? throw new InvalidDataException("Missing build result.");
            BuildStatus = LastBuildResult.Reason;
            BuildPending = false;
            if (!LastBuildResult.Success)
                AudioErrors++;
            return true;
        }
        return false;
    }
}
