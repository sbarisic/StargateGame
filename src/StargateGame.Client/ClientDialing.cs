using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class ClientSession
{
    public GateState? Gate
    {
        get;
        private set;
    }
    public Dictionary<Guid, GateState> Gates
    {
        get;
    } = [];
    Guid selectedGate;
    public void SelectDhd(string entityId)
    {
        if (World.Definition.Fixture != "gate-pair")
            return;
        var id = GraphicsFixture.EntityId(World.Definition, entityId);
        var state = Gates.Values.FirstOrDefault(g => g.DhdId == id);
        if (state is not null)
        {
            selectedGate = state.GateId;
            Gate = state;
        }
    }
    public ConnectionState? GateConnection
    {
        get;
        private set;
    }
    public string DialStatus
    {
        get;
        private set;
    } = "";
    public DhdResult? LastDhdResult
    {
        get;
        private set;
    }
    public string CurrentWorldName
    {
        get;
        private set;
    } = "";
    public int[] ReturnAddress
    {
        get;
        private set;
    } = [];
    public DeveloperDestination[] Destinations
    {
        get;
        private set;
    } = [];
    public bool DialPending => dialRequest is not null || retryDial is not null || dialQueue.Count > 0;
    public Func<GateState, bool>? PrepareGateVisuals
    {
        get;
        set;
    }
    readonly Queue<(string Action, int Key, Vector3 Aim, Guid Dhd)> dialQueue = [];
    DhdCommand? dialRequest;
    (string Action, int Key, Vector3 Aim, Guid Dhd)? retryDial;
    double retryAt;
    int dialRetries;
    double gateAnchor, gateAnchorAt;
    public double GateTime => gateAnchor + (Now - gateAnchorAt) * 60;
    void AnchorGate(long tick)
    {
        if (tick <= 0)
            return;
        gateAnchor = tick + rttSeconds * 30;
        gateAnchorAt = Now;
    }
    bool HandleDialing(Message m)
    {
        AnchorGate(m.GateTick);
        if (m.Kind == "galaxy-info")
        {
            if (m.LocationId != Guid.Empty && m.LocationId != World.Definition.LocationId)
                return true;
            ReturnAddress = m.Address is { Length: 6 } address && address.All(n => n is >= 2 and <= 39) && address.Distinct().Count() == 6 ? address : [];
            CurrentWorldName = m.Name ?? World.Definition.Name;
            Destinations = m.Destinations ?? [];
            Console.WriteLine("Return address: " + string.Join('-', ReturnAddress) + " + 01 + orb");
            foreach (var destination in Destinations)
                Console.WriteLine($"{destination.Name}: {string.Join('-', destination.Symbols)} {(destination.GameAuthored ? "(game-authored completion)" : "")} {(destination.DhdCompatible ? "" : "(unsupported on standard DHD)")}");
            return true;
        }
        if (m.Kind is not ("gate-state" or "dhd-result"))
            return false;
        if (m.DhdResult is not null)
            LastDhdResult = m.DhdResult;
        if (m.Gate is { } gate && gate.LocationId == World.Definition.LocationId && (!Gates.TryGetValue(gate.GateId, out var previous) || gate.Revision >= previous.Revision))
        {
            if (previous is null || gate.Revision > previous.Revision)
            {
                if (AudioGates.Count == 128)
                {
                    AudioGates.Dequeue();
                    AudioDropped++;
                }
                AudioGates.Enqueue((gate, previous is null));
            }
            Gates[gate.GateId] = gate;
            if (Gate is null || selectedGate == Guid.Empty || gate.GateId == selectedGate)
            {
                selectedGate = gate.GateId;
                Gate = gate;
                DialStatus = gate.Status;
            }
            if (m.Kind == "gate-state" || gate.ConnectionId == Guid.Empty)
                GateConnection = m.Connection;
            if (gate.Phase == GatePhase.Preparing && (PrepareGateVisuals?.Invoke(gate) ?? true))
                connection.Peer.Send(new()
                {
                    Kind = "gate-ready",
                    Gate = gate,
                    LocationId = gate.LocationId
                });
        }
        if (m.DhdResult is { } result && result.RequestId == dialRequest?.RequestId)
        {
            DialStatus = result.Reason;
            Console.WriteLine($"DHD {dialRequest.Action} {dialRequest.Key}: {result.Accepted} {result.Reason}");
            if (!result.Accepted && dialRetries < 3 && result.Reason is "Stale DHD location or physics epoch." or "Dial changed; retry the input.")
            {
                retryDial = (dialRequest.Action, dialRequest.Key, dialRequest.Aim, dialRequest.DhdId);
                retryAt = Now + .2;
                dialRetries++;
            }
            else
                dialRetries = 0;
            dialRequest = null;
            SendNextDial();
        }
        return true;
    }
    public void Dial(string action, int key, Vector3 aim)
    {
        if (Gate is null || dialQueue.Count >= 16 || PlanetDebugState?.Busy == true)
            return;
        if (action == "press" && AudioPresses.Count < 32)
            AudioPresses.Enqueue(key);
        dialQueue.Enqueue((action, key, aim, Gate.DhdId));
        SendNextDial();
    }
    void SendNextDial()
    {
        if (dialRequest is not null || Gate is null || Now < retryAt || PlanetDebugState?.Busy == true)
            return;
        (string Action, int Key, Vector3 Aim, Guid Dhd) input;
        if (retryDial is { } retry)
        {
            input = retry;
            retryDial = null;
        }
        else if (!dialQueue.TryDequeue(out input))
            return;
        var target = Gates.Values.FirstOrDefault(g => g.DhdId == input.Dhd);
        if (target is null)
            return;
        dialRequest = new(Guid.NewGuid(), World.Definition.LocationId, target.DhdId, Physics.Epoch, target.Revision, input.Action, input.Key, input.Aim);
        connection.Peer.Send(new()
        {
            Kind = "dhd",
            LocationId = World.Definition.LocationId,
            Dhd = dialRequest
        });
    }
    void ClearDial()
    {
        ClearAudioEvents();
        Gates.Clear();
        selectedGate = Guid.Empty;
        Gate = null;
        GateConnection = null;
        dialQueue.Clear();
        dialRequest = null;
        retryDial = null;
        retryAt = 0;
        dialRetries = 0;
        DialStatus = "";
        ReturnAddress = [];
        CurrentWorldName = "";
        BuildPending = false;
        BuildStatus = "";
        LastBuildResult = null;
    }
    public void InspectDestination(int[] address)
    {
        if (!Developer || TransitionPending)
            return;
        connection.Peer.Send(new()
        {
            Kind = "galaxy-move",
            Address = address,
            LocationId = World.Definition.LocationId
        });
    }
    public void InspectDestination(string text)
    {
        var parts = text.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var address = new int[parts.Length];
        if (parts.Length != 6)
        {
            TerrainStatus = "Enter six destination glyph IDs separated by hyphens.";
            return;
        }
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out address[i]) || address[i] is < 2 or > 39)
            {
                TerrainStatus = "Destination glyph IDs must be 02 through 39.";
                return;
            }
        InspectDestination(address);
    }
}
