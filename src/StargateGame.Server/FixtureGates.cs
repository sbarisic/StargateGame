using System.Numerics;
using StargateGame.Physics;
using StargateGame.Shared;

namespace StargateGame.Server;

public sealed partial class GameServer
{
    // Developer fixture endpoints share one production physics world. They never
    // resolve galaxy addresses or write universe/ownership persistence.
    readonly Dictionary<Guid, GateState> fixtureGates = [];
    readonly Dictionary<int, BodyState> fixturePreviousBodies = [], fixtureSafeBodies = [];
    readonly Dictionary<int, PlayerState> fixturePreviousPlayers = [];
    readonly List<FixtureFlight> fixtureFlights = [];
    ConnectionState? fixtureConnection;
    bool fixtureCloseRequested;
    sealed class FixtureFlight
    {
        public Guid Id = Guid.NewGuid();
        public long Started, RetryAt;
        public GateFrame Source, Destination;
        public PropDefinition? Prop;
        public BodyState Body, SafeBody;
        public PlayerState? Player, SafePlayer;
        public Session? Session;
    }
    void InitializeFixtureGates()
    {
        var world = physics.World.Definition;
        if (world.Fixture != "gate-pair")
            return;
        foreach (string suffix in new[] { "a", "b" })
        {
            var id = GraphicsFixture.EntityId(world, "gate-" + suffix);
            fixtureGates[id] = new(world.LocationId, id, GraphicsFixture.EntityId(world, "dhd-" + suffix), 0, GatePhase.Idle, [], 0, 0, Guid.Empty);
        }
        context.Gate = fixtureGates.Values.First();
    }
    StaticEntity FixtureEntity(Guid id) => physics.World.Definition.Entities.Single(e => GraphicsFixture.EntityId(physics.World.Definition, e.Id) == id);
    void PublishFixture(GateState state)
    {
        fixtureGates[state.GateId] = state with
        {
            Revision = fixtureGates[state.GateId].Revision + 1
        };
        context.Gate = fixtureGates.Values.First();
        foreach (var s in sessions.Where(s => s.PlayerId != 0 && !s.Peer.Closed))
            SendFixtureGate(s, fixtureGates[state.GateId]);
    }
    void SendFixtureGate(Session s, GateState state) => s.Peer.Send(new() { Kind = "gate-state", Gate = state, Connection = fixtureConnection, LocationId = state.LocationId, GateTick = serverTick });
    bool HandleFixtureDhd(Session s, DhdCommand command)
    {
        if (s.DhdResults.TryGetValue(command.RequestId, out var prior))
        {
            s.Peer.Send(new()
            {
                Kind = "dhd-result",
                DhdResult = prior
            });
            return true;
        }
        var state = fixtureGates.Values.FirstOrDefault(g => g.DhdId == command.DhdId);
        bool accepted = false;
        string reason = "";
        try
        {
            if (state is null || command.RequestId == Guid.Empty || command.LocationId != state.LocationId || command.Epoch != physics.Epoch)
                throw new InvalidOperationException("Stale DHD location or physics epoch.");
            if (command.Revision != state.Revision)
                throw new InvalidOperationException("Dial changed; retry the input.");
            var dhd = FixtureEntity(state.DhdId);
            var eye = Movement.EyePosition(physics.Player(s.PlayerId));
            var hit = DhdInteraction.Pick(dhd, eye, command.Aim);
            if (hit is null || command.Action == "press" && hit.Value.Key != command.Key || physics.InteractionBlocked(eye, command.Aim, hit.Value.Distance - .015f, ignoreEntity: dhd.Id, ignorePlayer: s.PlayerId))
                throw new InvalidOperationException("Aim at a visible DHD surface within three metres.");
            if (state.Settled && command.Action == "press" && command.Key == 0)
            {
                fixtureCloseRequested = true;
                reason = "Closing after travelers clear";
            }
            else
            {
                if (fixtureConnection is not null)
                    throw new InvalidOperationException("Gate is busy.");
                if (state.Owner != 0 && state.Owner != s.PlayerId)
                    throw new InvalidOperationException("Another explorer owns this dial session.");
                var symbols = state.Symbols.ToList();
                // Six destination glyphs are already complete in Sandbox;
                // an earlier orb press abandons the partial address.
                bool cancelPartial = command.Action == "press" && command.Key == 0 && symbols.Count is > 0 and < 6;
                if (cancelPartial)
                {
                    symbols.Clear();
                    reason = "Dial cancelled";
                }
                else if (command.Action == "reset")
                    symbols.Clear();
                else if (command.Action == "undo")
                {
                    if (symbols.Count > 0)
                        symbols.RemoveAt(symbols.Count - 1);
                }
                else if (command.Action == "press")
                {
                    if (command.Key == 0)
                    {
                        if (symbols.Count is not (6 or 7) || symbols.Count == 7 && symbols[6] != 1)
                            throw new InvalidOperationException("Enter six destination symbols, then press the orb (origin is optional in this lab).");
                        if (symbols.Count == 6)
                            symbols.Add(1);
                        var other = fixtureGates.Values.Single(g => g.GateId != state.GateId);
                        if (other.Phase != GatePhase.Idle)
                            throw new InvalidOperationException("Destination gate is busy.");
                        var id = Guid.NewGuid();
                        fixtureConnection = new(id, state.GateId, other.GateId, serverTick, 0, GatePhase.Opening);
                        PublishFixture(state with
                        {
                            Phase = GatePhase.Opening,
                            PhaseTick = serverTick,
                            ConnectionId = id,
                            Symbols = symbols.ToArray(),
                            Owner = s.PlayerId,
                            PressKey = 0,
                            PressTick = serverTick,
                            Status = "Opening test connection"
                        });
                        PublishFixture(other with
                        {
                            Phase = GatePhase.Opening,
                            PhaseTick = serverTick + GateEffectTimeline.IncomingLeadTicks,
                            ConnectionId = id,
                            Incoming = true,
                            Status = "Incoming test connection"
                        });
                        accepted = true;
                        reason = "Connected to the other test gate";
                    }
                    else
                    {
                        if (!DhdInteraction.Available(command.Key) || symbols.Contains(command.Key) || command.Key == 1 && symbols.Count != 6 || command.Key != 1 && symbols.Count >= 6)
                            throw new InvalidOperationException("Enter six distinct available glyphs; origin follows them.");
                        symbols.Add(command.Key);
                    }
                }
                else
                    throw new InvalidOperationException("Unknown DHD action.");
                if (!accepted)
                    PublishFixture(state with
                    {
                        Phase = symbols.Count == 0 ? GatePhase.Idle : GatePhase.Dialing,
                        Symbols = symbols.ToArray(),
                        Owner = symbols.Count == 0 ? 0 : s.PlayerId,
                        PhaseTick = serverTick,
                        PressKey = command.Action == "press" ? command.Key : -1,
                        PressTick = serverTick,
                        Status = cancelPartial ? reason : "Any six glyphs dial the other test gate"
                    });
            }
            accepted = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            reason = ex.Message;
        }
        var current = state is null ? null : fixtureGates[state.GateId];
        var result = new DhdResult(command.RequestId, accepted, reason, current?.Revision ?? 0);
        s.DhdResults[command.RequestId] = result;
        if (s.DhdResults.Count > 256)
            s.DhdResults.Remove(s.DhdResults.Keys.First());
        s.Peer.Send(new()
        {
            Kind = "dhd-result",
            DhdResult = result,
            Gate = current,
            Connection = fixtureConnection,
            GateTick = serverTick
        });
        return true;
    }
    void TickFixtureGates()
    {
        if (fixtureGates.Count == 0)
            return;
        foreach (var gate in fixtureGates.Values.ToArray())
        {
            if (gate.Phase == GatePhase.Dialing && (!sessions.Any(s => s.PlayerId == gate.Owner && !s.Peer.Closed) || serverTick - gate.PressTick > 1800))
                PublishFixture(gate with
                {
                    Phase = GatePhase.Idle,
                    Symbols = [],
                    Owner = 0
                });
            if (gate.Phase == GatePhase.Opening && serverTick - gate.PhaseTick >= GateEffectTimeline.OpeningTicks)
                PublishFixture(gate with
                {
                    Phase = GatePhase.Open,
                    PhaseTick = serverTick,
                    Status = "Connected"
                });
        }
        foreach (var flight in fixtureFlights.ToArray())
            ReleaseFixtureFlight(flight);
        if (fixtureConnection is not { } connection)
            return;
        bool expired = GateTravel.ConnectionExpired(serverTick, connection.StartTick + GateEffectTimeline.OpeningTicks);
        if (expired || serverTick >= connection.StartTick + GateEffectTimeline.ReceiverSettledTicks + 7200)
            fixtureCloseRequested = true;
        foreach (var clip in physics.CurrentClips().Where(c => c.Mode == PropClipMode.Emergence))
        {
            var body = physics.Bodies().Single(b => b.Id == clip.Body);
            var prop = physics.World.Definition.Props.Single(p => p.Id == clip.Body);
            var extents = GateTravel.Extents(clip.Gate, body, prop);
            if (extents.Min >= .02f)
                physics.FinishPropEmergence(prop.Id);
            else if (extents.Max <= 0 || expired)
            {
                physics.RemoveProp(prop.Id);
                physics.Epoch++;
                runtime!.Definition = physics.World.Definition;
            }
        }
        if (fixtureCloseRequested && fixtureFlights.Count == 0 && !physics.CurrentClips().Any(c => c.Mode == PropClipMode.Emergence) && connection.Phase != GatePhase.Closing)
        {
            // Withdraw incomplete insertions before restoring their full hulls.
            var clips = physics.CurrentClips();
            if (clips.Length > 0)
                physics.Apply(physics.Snapshot() with
                {
                    Bodies = physics.Bodies().Select(b => clips.Any(c => c.Body == b.Id) && fixtureSafeBodies.TryGetValue(b.Id, out var safe) ? safe : b).ToArray(),
                    Clips = []
                });
            fixtureConnection = connection with
            {
                Phase = GatePhase.Closing,
                CloseTick = serverTick
            };
            foreach (var g in fixtureGates.Values.ToArray())
                PublishFixture(g with
                {
                    Phase = GatePhase.Closing,
                    PhaseTick = serverTick
                });
        }
        if (fixtureConnection is { Phase: GatePhase.Closing } closing && serverTick - closing.CloseTick >= GateEffectTimeline.ClosingTicks)
        {
            fixtureConnection = null;
            fixtureCloseRequested = false;
            foreach (var g in fixtureGates.Values.ToArray())
                PublishFixture(g with
                {
                    Phase = GatePhase.Idle,
                    Symbols = [],
                    Owner = 0,
                    ConnectionId = Guid.Empty,
                    Incoming = false,
                    Status = "",
                    PhaseTick = serverTick
                });
        }
    }
    void FixtureNotice(FixtureFlight flight, TransitPhase phase, string? reason = null)
    {
        var location = physics.World.Definition.LocationId;
        flight.Session?.Peer.Send(new()
        {
            Kind = "transit",
            Transit = new(flight.Id, fixtureConnection!.Id, location, location, phase, flight.Started, 0, reason),
            GateTick = serverTick
        });
    }
    void ReleaseFixtureFlight(FixtureFlight f)
    {
        if (serverTick - f.Started < GateTravel.MinimumTransitSeconds * 60 || serverTick < f.RetryAt)
            return;
        if (f.Player is { } player)
        {
            if (f.Session!.Peer.Closed || f.Session.PlayerId == 0)
            {
                fixtureFlights.Remove(f);
                return;
            }
            var exit = GateTravel.Exit(f.Source, f.Destination, player);
            if (!physics.CanPlacePlayer(exit.Position, player.Id))
            {
                if (serverTick - f.Started < 600)
                    return;
                exit = f.SafePlayer!.Value;
                FixtureNotice(f, TransitPhase.Cancelled, "Test gate exit blocked; returned safely.");
            }
            exit = exit with
            {
                LastInput = physics.Player(player.Id).LastInput
            };
            physics.Apply(physics.Snapshot() with
            {
                Players = physics.Players.Select(p => p.Id == player.Id ? exit : p).ToArray()
            });
            physics.Epoch++;
            f.Session.Inputs.Clear();
            fixturePreviousPlayers[player.Id] = exit;
            var location = physics.World.Definition.LocationId;
            f.Session.Peer.Send(new()
            {
                Kind = "fixture-arrival",
                LocationId = location,
                State = physics.Snapshot(),
                Transit = new(f.Id, fixtureConnection!.Id, location, location, TransitPhase.Released, f.Started, 0),
                GateTick = serverTick
            });
            fixtureFlights.Remove(f);
            return;
        }
        var prop = f.Prop!;
        var desired = GateTravel.Exit(f.Source, f.Destination, f.Body, prop);
        var result = desired;
        if (physics.CanBeginPropEmergence(f.Destination, prop, desired))
        {
            physics.InsertProp(prop, result);
            physics.BeginPropEmergence(prop.Id, f.Destination, fixtureConnection!.Id);
            fixturePreviousBodies[prop.Id] = result;
            fixtureFlights.Remove(f);
            physics.Epoch++;
            Console.WriteLine($"FIXTURE item={prop.PersistentId} released at {result.Position} velocity={result.LinearVelocity}");
        }
        else if (serverTick - f.Started > 600)
        {
            // Pristine fixture objects remain recoverable when the exit is blocked.
            if (!physics.CanPlaceProp(prop, f.SafeBody))
                return;
            physics.InsertProp(prop, f.SafeBody);
            fixturePreviousBodies[prop.Id] = f.SafeBody;
            fixtureFlights.Remove(f);
            physics.Epoch++;
            foreach (var s in sessions)
                s.Peer.Send(new()
                {
                    Kind = "item-transit",
                    LocationId = physics.World.Definition.LocationId,
                    Reason = "Item returned: test gate exit blocked for ten seconds."
                });
        }
        else
            f.RetryAt = serverTick + 15;
        runtime!.Definition = physics.World.Definition;
    }
    void ObserveFixtureTravel()
    {
        var bodies = physics.Bodies();
        var connection = fixtureConnection;
        physics.TravelGate = connection is not null && fixtureGates[connection.Source].Settled && !fixtureCloseRequested ? GateFrame.From(FixtureEntity(connection.Source)) : null;
        if (connection is not null)
        {
            var source = GateFrame.From(FixtureEntity(connection.Source));
            var destination = GateFrame.From(FixtureEntity(connection.Destination));
            foreach (var player in physics.Players.ToArray())
            {
                if (!fixturePreviousPlayers.TryGetValue(player.Id, out var before) || player.TransitFrozen || physics.TravelGate is null || !GateTravel.PlayerCrossing(source, before.Position, player.Position, Movement.BodyHeight(player)))
                    continue;
                var session = sessions.FirstOrDefault(s => s.PlayerId == player.Id && !s.Peer.Closed);
                if (session is null)
                    continue;
                var flight = new FixtureFlight { Started = serverTick, Source = source, Destination = destination, Player = player, SafePlayer = before, Session = session };
                physics.FreezePlayer(player.Id, true);
                fixtureFlights.Add(flight);
                FixtureNotice(flight, TransitPhase.Captured);
            }
            foreach (var endpoint in fixtureGates.Values.ToArray())
            {
                var frame = GateFrame.From(FixtureEntity(endpoint.GateId));
                if (endpoint.Phase == GatePhase.Opening)
                {
                    var effect = GateEffectTimeline.Evaluate(GatePhase.Opening, (serverTick - endpoint.PhaseTick) / 60.0);
                    if (effect.Extension > .001f)
                    {
                        var volume = new KawooshVolume(frame, effect);
                        foreach (var body in physics.Bodies())
                        {
                            var prop = physics.World.Definition.Props.Single(p => p.Id == body.Id);
                            if (volume.IntersectsProp(prop, fixturePreviousBodies.GetValueOrDefault(body.Id, body), body))
                            {
                                physics.RemoveProp(body.Id);
                                physics.Epoch++;
                            }
                        }
                    }
                }
                if (!endpoint.Settled || fixtureCloseRequested)
                    continue;
                foreach (var body in physics.Bodies())
                {
                    if (!fixturePreviousBodies.TryGetValue(body.Id, out var before))
                        continue;
                    var prop = physics.World.Definition.Props.Single(p => p.Id == body.Id);
                    var clip = physics.CurrentClips().FirstOrDefault(c => c.Body == body.Id);
                    if (clip?.Mode == PropClipMode.Emergence)
                        continue;
                    if (clip is not null && Vector3.DistanceSquared(clip.Gate.Centre, frame.Centre) > .1f)
                        continue;
                    var local = frame.Local(body.Position);
                    float radius = frame.Radius + prop.HalfExtent.Length();
                    if (local.X * local.X + local.Y * local.Y > radius * radius)
                        continue;
                    var entry = clip?.Gate ?? (frame.Distance(before.Position) >= 0 ? frame : frame with
                    {
                        Rotation = Quaternion.Normalize(frame.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI))
                    });
                    var e = GateTravel.Extents(entry, body, prop);
                    var prior = GateTravel.Extents(entry, before, prop);
                    if (e.Min > .02f)
                    {
                        fixtureSafeBodies[body.Id] = body;
                        continue;
                    }
                    if (clip is null && prior.Max <= GateTravel.PropCompletionDepth || !e.Fits || !GateTravel.SweptFits(entry, before, body, prop))
                        continue;
                    if (clip is null)
                        fixtureSafeBodies[body.Id] = before;
                    physics.BeginPropEntry(body.Id, entry);
                    if (e.Max > GateTravel.PropCompletionDepth)
                        continue;
                    physics.RemoveProp(body.Id);
                    physics.Epoch++;
                    if (endpoint.Incoming || Vector3.Dot(entry.Normal, frame.Normal) < 0)
                    {
                        Console.WriteLine($"FIXTURE wrong-way destruction item={prop.PersistentId}");
                        continue;
                    }
                    fixtureFlights.Add(new()
                    {
                        Started = serverTick,
                        Source = source,
                        Destination = destination,
                        Prop = prop,
                        Body = body,
                        SafeBody = fixtureSafeBodies.GetValueOrDefault(body.Id, before)
                    });
                    Console.WriteLine($"FIXTURE captured item={prop.PersistentId}");
                }
            }
            runtime!.Definition = physics.World.Definition;
        }
        fixturePreviousBodies.Clear();
        foreach (var b in physics.Bodies())
            fixturePreviousBodies[b.Id] = b;
        fixturePreviousPlayers.Clear();
        foreach (var p in physics.Players)
            fixturePreviousPlayers[p.Id] = p;
    }
}
