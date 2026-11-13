using System.Numerics;
using StargateGame.Galaxy;
using StargateGame.Persistence;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.Building;

namespace StargateGame.Server;

public sealed partial class GameServer
{
    Catalogue? catalogue;
    UniverseGalaxy? galaxy;
    readonly Dictionary<Guid, Task<PlanetRuntime>> locationLoads = [];
    readonly Dictionary<Guid, long> loadStarts = [];
    readonly Dictionary<Guid, Link> links = [];
    readonly Dictionary<Session, Relocation> relocations = [];
    public int LoadedLocationCount => contexts.Count;
    public int ActiveConnectionCount => links.Count + (fixtureConnection is null ? 0 : 1);
    bool HasLocationTransfer => TravelBusy(context) || relocations.Any(p => p.Value.Transfer != 0 && (sessions.Contains(p.Key) || p.Value.Target == context.Association?.LocationId));
    sealed class Link(Guid id, LocationContext source, GateAssociation target, long tick)
    {
        public Guid Id = id;
        public LocationContext Source = source;
        public GateAssociation Target = target;
        public LocationContext? Destination;
        public long Started = tick, Opening, Closing;
        public bool CloseRequested;
        public HashSet<int> Ready = [];
    }
    sealed class Relocation(Guid target, long started)
    {
        public Guid Target = target;
        public long Transfer, Started = started;
        public bool Ready;
    }
    void InitializeGalaxy(string path)
    {
        if (repository is null || runtime is null)
            throw new InvalidOperationException("Galaxy requires a persistent world.");
        catalogue = new(path);
        galaxy = repository.AdoptGalaxyAsync(catalogue, runtime.Definition.LocationId).GetAwaiter().GetResult();
        // The retained home wins over a later developer preset/seed selection.
        if (galaxy.HomeLocation != runtime.Definition.LocationId)
        {
            var saved = repository.LoadAsync(galaxy.HomeLocation).GetAwaiter().GetResult();
            physics.Dispose();
            runtime.Dispose();
            runtime = PlanetRuntime.CreateAsync(saved.Definition.Seed, saved.Definition.Preset, materialize: _ => Task.FromResult(saved), settlements: saved.Definition.SettlementGeneration, generationSettings: saved.Definition.Generation, version: saved.Definition.GeneratorVersion).GetAwaiter().GetResult();
            physics = new(runtime.Definition, true, runtime.Generation.Store, runtime.InitialCollision, runtime.Changes);
            if (runtime.SavedBodies.Length > 0)
                physics.Apply(physics.Snapshot() with
                {
                    Bodies = runtime.SavedBodies
                });
        }
        SetAssociation(context, repository.ResolveGalaxyAsync(galaxy, catalogue, galaxy.HomeKey).GetAwaiter().GetResult().Association);
        Console.WriteLine($"Galaxy {Address.Version}: {catalogue.Count:N0} worlds; home {string.Join('-', Address.Symbols(galaxy.HomeKey))} + 01 + orb");
    }
    void SetAssociation(LocationContext c, GateAssociation association)
    {
        c.Association = association;
        c.Gate = new(association.LocationId, association.GateId, association.DhdId, 0, GatePhase.Idle, [], 0, serverTick, Guid.Empty);
    }
    void SendGateBaseline(Session s)
    {
        if (fixtureGates.Count > 0)
        {
            foreach (var state in fixtureGates.Values)
                SendFixtureGate(s, state);
            s.Peer.Send(new()
            {
                Kind = "galaxy-info",
                LocationId = physics.World.Definition.LocationId,
                Name = "Gate-pair lab",
                Address = [2, 3, 4, 5, 6, 7]
            });
            return;
        }
        if (context.Gate is not { } gate)
            return;
        var connection = links.TryGetValue(gate.ConnectionId, out var link) ? new ConnectionState(link.Id, link.Source.Association!.GateId, link.Target.GateId, link.Opening, link.Closing, gate.Phase) : null;
        s.Peer.Send(new()
        {
            Kind = "gate-state",
            Gate = gate,
            Connection = connection,
            LocationId = gate.LocationId,
            GateTick = serverTick
        });
        var association = context.Association!;
        string name = association.LocationId == galaxy!.HomeLocation ? "Home Base" : Address.Data.Routes.FirstOrDefault(r => !r.Intergalactic && r.Key == association.Address)?.Name ?? ("World " + association.LocationId.ToString("N")[..8].ToUpperInvariant());
        s.Peer.Send(new()
        {
            Kind = "galaxy-info",
            LocationId = gate.LocationId,
            Name = name,
            Address = Address.Symbols(association.Address),
            Destinations = devPlanets && s.Loopback ? Address.Data.Routes.Select(r => new DeveloperDestination(r.Name, r.GameSequence, r.DhdCompatible, r.GameAuthored)).ToArray() : null
        });
    }
    void Publish(LocationContext c, GateState gate)
    {
        c.Gate = gate with
        {
            Revision = (c.Gate?.Revision ?? -1) + 1
        };
        UpdatePrefetch(c);
        ConnectionState? connection = links.TryGetValue(gate.ConnectionId, out var link) ? new(link.Id, link.Source.Association!.GateId, link.Target.GateId, link.Opening, link.Closing, gate.Phase) : null;
        foreach (var s in c.Sessions.Where(s => s.PlayerId != 0))
            s.Peer.Send(new()
            {
                Kind = "gate-state",
                Gate = c.Gate,
                Connection = connection,
                LocationId = c.Gate.LocationId,
                GateTick = serverTick
            });
        Console.WriteLine($"Gate {gate.GateId}: {gate.Phase}, revision {c.Gate.Revision}, connection {gate.ConnectionId}; {gate.Status}");
    }
    void Idle(LocationContext c, string reason = "") => Publish(c, c.Gate! with { Phase = GatePhase.Idle, Symbols = [], Owner = 0, ConnectionId = Guid.Empty, PhaseTick = serverTick, Incoming = false, Status = reason, PressKey = -1 });
    void Fail(Link link, string reason)
    {
        foreach (var travel in travels.Values.Where(t => t.Link == link))
            travel.Failure = reason;
        if (HasEmergences(link) || npcFlights.Values.Any(t => t.Link == link) || travels.Values.Any(t => t.Link == link) || !RecoverPartialProps(link.Source) || link.Destination is { } failedReceiver && !RecoverPartialProps(failedReceiver))
        {
            link.CloseRequested = true;
            return;
        }
        links.Remove(link.Id);
        link.Source.LastDialTick = serverTick;
        if (link.Destination is { } d && d.Gate?.ConnectionId == link.Id)
            Idle(d);
        if (link.Source.Gate?.ConnectionId == link.Id)
            Idle(link.Source, reason);
    }
    void BeginLoad(GateAssociation association, bool gateRegionOnly = true)
    {
        if (contexts.Any(c => c.Association?.LocationId == association.LocationId) || locationLoads.ContainsKey(association.LocationId))
            return;
        if (locationLoads.Count >= 2)
            throw new InvalidOperationException("Two destination generation jobs are already running. Retry shortly.");
        loadAssociations[association.LocationId] = association;
        loadStarts[association.LocationId] = System.Diagnostics.Stopwatch.GetTimestamp();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        loadCancellation.Add(association.LocationId, cancellation);
        int seed = Address.ContentSeed(galaxy!.Seed, association.LocationId);
        var named = Address.Data.Routes.FirstOrDefault(r => !r.Intergalactic && r.Key == association.Address);
        string preset = named?.Name == "Earth" ? "temperate" : named?.Name == "Abydos" ? "desert" : new[] { "temperate", "desert", "icy" }[(int)((uint)seed % 3)];
        locationLoads.Add(association.LocationId, Task.Run(async () =>
        {
            if (allowTestIdentities && DiagnosticDestinationDelayMilliseconds > 0)
                await Task.Delay(DiagnosticDestinationDelayMilliseconds, cancellation.Token);
            var retained = await repository!.TryLoadAsync(association.LocationId);
            cancellation.Token.ThrowIfCancellationRequested();
            return await PlanetRuntime.CreateAsync(retained?.Definition.Seed ?? seed, retained?.Definition.Preset ?? preset, cancellation.Token,
                materialize: d => retained is not null ? Task.FromResult(retained) : repository.MaterializeGalaxyAsync(galaxy, association, d with { Name = named?.Name ?? $"World {association.Address:x9}" }), gateCollisionRadius: gateRegionOnly ? 1 : 3, settlements: retained?.Definition.SettlementGeneration ?? settlementSettings, generationSettings: retained?.Definition.Generation, version: retained?.Definition.GeneratorVersion ?? PlanetSettings.Version);
        }, cancellation.Token));
    }
    bool HandleGateMessage(Session s, Message m)
    {
        if (m.Kind == "planet-cancel" && catalogue is not null)
        {
            if (devPlanets && s.Loopback)
            {
                s.MoveLookup = null;
                CancelRelocation(s, "Developer relocation cancelled.");
            }
            return true;
        }
        if (m.Kind == "gate-ready")
        {
            if (m.Gate is { } ready && links.TryGetValue(ready.ConnectionId, out var link) && ready.LocationId == context.Association?.LocationId && context.Gate?.ConnectionId == link.Id && ready.Revision == context.Gate.Revision)
                link.Ready.Add(s.PlayerId);
            return true;
        }
        if (m.Kind == "terrain-ready" && relocations.TryGetValue(s, out var relocation) && m.TransferId == relocation.Transfer)
        {
            relocation.Ready = true;
            return true;
        }
        if (m.Kind == "galaxy-move")
        {
            if (testSession.Length > 0) Console.WriteLine($"TEST relocate player={s.PlayerId} from={m.LocationId} context={context.Association?.LocationId}");
            if (m.LocationId != context.Association?.LocationId)
                return true;
            if (!devPlanets || !s.Loopback || catalogue is null || galaxy is null)
            {
                s.Peer.Send(new()
                {
                    Kind = "terrain-status",
                    Reason = "Developer location inspection is unavailable."
                });
                return true;
            }
            try
            {
                if (transition is not null || preparing is not null || edit is not null || relocations.ContainsKey(s))
                    throw new InvalidOperationException("A world preparation is already running.");
                if (s.MoveLookup is not null)
                    throw new InvalidOperationException("Destination resolution is already running.");
                s.MoveLookup = repository!.ResolveGalaxyAsync(galaxy, catalogue, Address.Resolve(m.Address ?? []).Key);
                s.Peer.Send(new()
                {
                    Kind = "terrain-status",
                    Reason = "Preparing developer destination; other explorers remain here."
                });
            }
            catch (Exception ex)
            {
                s.Peer.Send(new()
                {
                    Kind = "terrain-status",
                    Reason = ex.Message
                });
            }
            return true;
        }
        if (m.Kind != "dhd")
            return false;
        if (m.Dhd is not { } command)
            return true;
        if (fixtureGates.Count > 0)
            return HandleFixtureDhd(s, command);
        if (s.DhdResults.TryGetValue(command.RequestId, out var prior))
        {
            s.Peer.Send(new()
            {
                Kind = "dhd-result",
                DhdResult = prior
            });
            return true;
        }
        if (s.GalaxyLookupCommand?.RequestId == command.RequestId)
            return true;
        void Reply(bool accepted, string reason)
        {
            var result = new DhdResult(command.RequestId, accepted, reason, context.Gate?.Revision ?? 0);
            s.DhdResults[command.RequestId] = result;
            if (s.DhdResults.Count > 256)
                s.DhdResults.Remove(s.DhdResults.Keys.First());
            s.Peer.Send(new()
            {
                Kind = "dhd-result",
                DhdResult = result,
                Gate = context.Gate,
                GateTick = serverTick
            });
        }
        bool activating = false;
        try
        {
            var gate = context.Gate ?? throw new InvalidOperationException("This testing location has no galaxy gate.");
            if (command.RequestId == Guid.Empty || command.LocationId != gate.LocationId || command.DhdId != gate.DhdId || command.Epoch != physics.Epoch)
                throw new InvalidOperationException("Stale DHD location or physics epoch.");
            if (command.Revision != gate.Revision)
                throw new InvalidOperationException("Dial changed; retry the input.");
            if (!CanPress(s, command, out var selected) || command.Action == "press" && selected != command.Key)
                throw new InvalidOperationException("Aim at a visible DHD surface within three metres.");
            if (gate.Phase == GatePhase.Preparing && command.Action == "reset" && gate.Owner == s.PlayerId)
            {
                if (links.TryGetValue(gate.ConnectionId, out var preparingLink))
                    Fail(preparingLink, "Dial cancelled.");
                Idle(context);
                Reply(true, "Dial reset");
                return true;
            }
            if (gate.Phase == GatePhase.Open && command.Action == "press" && command.Key == 0)
            {
                var link = links[gate.ConnectionId];
                link.CloseRequested = true;
                Reply(true, "Closing");
                return true;
            }
            if (gate.Phase is GatePhase.Opening or GatePhase.Open or GatePhase.Closing or GatePhase.Preparing)
                throw new InvalidOperationException("Gate is busy.");
            if (gate.Owner != 0 && gate.Owner != s.PlayerId)
                throw new InvalidOperationException("Another explorer owns this dial session.");
            context.LastDialTick = serverTick;
            var symbols = gate.Symbols.ToList();
            if (command.Action == "reset")
            {
                Idle(context);
                Reply(true, "Dial reset");
                return true;
            }
            if (command.Action == "undo")
            {
                if (symbols.Count > 0)
                    symbols.RemoveAt(symbols.Count - 1);
                Publish(context, gate with
                {
                    Symbols = symbols.ToArray(),
                    Phase = symbols.Count == 0 ? GatePhase.Idle : GatePhase.Dialing,
                    Owner = symbols.Count == 0 ? 0 : s.PlayerId,
                    Status = "",
                    PressKey = -1
                });
            }
            else if (command.Action == "press")
            {
                int key = command.Key;
                if (key == 0)
                {
                    if (symbols.Count is > 0 and < 7)
                    {
                        Idle(context, "Dial cancelled");
                        Reply(true, "Dial cancelled");
                        return true;
                    }
                    activating = true;
                    if (symbols.Count != 7 || symbols[6] != 1)
                        throw new InvalidOperationException("Enter six destination symbols and the origin key before pressing the orb.");
                    if (!context.Association!.Powered || !context.Association.Available)
                        throw new InvalidOperationException("Departure gate is unavailable or not powered.");
                    if (!context.Association.SupportsOrdinary)
                        throw new InvalidOperationException("Departure gate does not support ordinary seven-chevron connections.");
                    if (links.Count >= 8)
                        throw new InvalidOperationException("All eight connection slots are in use.");
                    ulong address = Address.Resolve(symbols.Take(6).ToArray()).Key;
                    if (s.ResolvedRequest != command.RequestId)
                    {
                        if (s.GalaxyLookup is not null)
                            throw new InvalidOperationException("Address resolution is already running.");
                        s.GalaxyLookupCommand = command;
                        s.GalaxyLookup = repository!.ResolveGalaxyAsync(galaxy!, catalogue!, address);
                        return true;
                    }
                    var target = s.ResolvedTarget!.Association;
                    if (target.LocationId == gate.LocationId)
                        throw new InvalidOperationException("Cannot connect a gate to itself.");
                    if (!target.Available || !target.Powered)
                        throw new InvalidOperationException("Destination receiver is unavailable.");
                    if (!target.SupportsOrdinary)
                        throw new InvalidOperationException("Destination receiver does not support ordinary seven-chevron connections.");
                    if (links.Values.Any(l => l.Target.GateId == target.GateId || l.Source.Association!.GateId == target.GateId) || contexts.Any(c => c.Association?.GateId == target.GateId && c.Gate?.Phase is not GatePhase.Idle))
                        throw new InvalidOperationException("Destination gate is busy.");
                    if (context.Prefetch?.Target?.LocationId == target.LocationId && (locationLoads.ContainsKey(target.LocationId) || contexts.Any(c => c.Association?.LocationId == target.LocationId)))
                    {
                        PrefetchLoadsReused++;
                        Console.WriteLine($"Prefetch {target.LocationId}: reused at orb activation.");
                    }
                    BeginLoad(target);
                    var link = new Link(Guid.NewGuid(), context, target, serverTick);
                    links.Add(link.Id, link);
                    var receiver = contexts.FirstOrDefault(c => c.Association?.GateId == target.GateId);
                    if (receiver is not null)
                    {
                        link.Destination = receiver;
                        Publish(receiver, receiver.Gate! with
                        {
                            Phase = GatePhase.Preparing,
                            ConnectionId = link.Id,
                            Incoming = true,
                            PhaseTick = serverTick,
                            Status = "Incoming connection"
                        });
                    }
                    Publish(context, gate with
                    {
                        Phase = GatePhase.Preparing,
                        ConnectionId = link.Id,
                        PhaseTick = serverTick,
                        Status = "Preparing destination",
                        PressKey = 0,
                        PressTick = serverTick
                    });
                }
                else
                {
                    if (!DhdInteraction.Available(key))
                        throw new InvalidOperationException("This glyph is unavailable on the standard DHD.");
                    if (symbols.Contains(key))
                        throw new InvalidOperationException("That glyph is already accepted.");
                    if (key == 1 && symbols.Count != 6)
                        throw new InvalidOperationException("Origin follows six destination symbols.");
                    if (key != 1 && symbols.Count >= 6)
                        throw new InvalidOperationException("Press origin, then the orb.");
                    symbols.Add(key);
                    Publish(context, gate with
                    {
                        Symbols = symbols.ToArray(),
                        Owner = s.PlayerId,
                        Phase = GatePhase.Dialing,
                        Status = key == 1 ? "Origin locked; press orb" : "",
                        PressKey = key,
                        PressTick = serverTick
                    });
                }
            }
            else
                throw new InvalidOperationException("Unknown DHD action.");
            context.LastDialTick = serverTick;
            context.OwnerAwayTick = 0;
            Reply(true, context.Gate!.Status);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            if (activating)
                Idle(context, ex.Message);
            Reply(false, ex.Message);
        }
        return true;
    }
    void PollGalaxyLookups()
    {
        foreach (var s in sessions.ToArray())
        {
            if (s.GalaxyLookup?.IsCompleted == true)
            {
                var task = s.GalaxyLookup;
                var command = s.GalaxyLookupCommand!;
                s.GalaxyLookup = null;
                s.GalaxyLookupCommand = null;
                if (s.PlayerId != 0 && !s.Peer.Closed)
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        s.ResolvedRequest = command.RequestId;
                        s.ResolvedTarget = task.Result;
                        HandleGateMessage(s, new()
                        {
                            Kind = "dhd",
                            Dhd = command
                        });
                        s.ResolvedTarget = null;
                        s.ResolvedRequest = Guid.Empty;
                    }
                    else
                    {
                        string reason = task.Exception?.GetBaseException().Message ?? "Address resolution cancelled.";
                        if (context.Gate is { } gate && gate.Owner == s.PlayerId && gate.Revision == command.Revision)
                            Idle(context, reason);
                        var result = new DhdResult(command.RequestId, false, reason, context.Gate?.Revision ?? 0);
                        s.DhdResults[command.RequestId] = result;
                        s.Peer.Send(new()
                        {
                            Kind = "dhd-result",
                            DhdResult = result,
                            Gate = context.Gate,
                            GateTick = serverTick
                        });
                    }
                }
            }
            if (s.MoveLookup?.IsCompleted == true)
            {
                if (testSession.Length > 0) Console.WriteLine($"TEST relocation resolved player={s.PlayerId} status={s.MoveLookup.Status}");
                var task = s.MoveLookup;
                s.MoveLookup = null;
                if (s.PlayerId == 0 || s.Peer.Closed)
                    continue;
                try
                {
                    var target = task.GetAwaiter().GetResult().Association;
                    if (target.LocationId == context.Association?.LocationId)
                        throw new InvalidOperationException("Already at that location.");
                    if (transition is not null || preparing is not null || edit is not null || relocations.ContainsKey(s))
                        throw new InvalidOperationException("A world preparation is already running.");
                    BeginLoad(target);
                    relocations.Add(s, new(target.LocationId, serverTick));
                }
                catch (Exception ex)
                {
                    s.Peer.Send(new()
                    {
                        Kind = "terrain-status",
                        Reason = ex.Message
                    });
                }
            }
        }
    }
    bool CanPress(Session s, DhdCommand command, out int selected)
    {
        selected = -1;
        var dhd = physics.World.Definition.Entities.FirstOrDefault(e => e.Model == "dhd");
        if (dhd is null)
            return false;
        var eye = Movement.EyePosition(physics.Player(s.PlayerId));
        if (DhdInteraction.Pick(dhd, eye, command.Aim) is not { } hit)
            return false;
        if (physics.InteractionBlocked(eye, command.Aim, hit.Distance - .015f, ignoreEntity: dhd.Id, ignorePlayer: s.PlayerId))
            return false;
        selected = hit.Key;
        return true;
    }
    void TickGalaxy()
    {
        if (galaxy is null)
            return;
        PollPrefetch();
        foreach (var (id, task) in locationLoads.ToArray())
            if (task.IsCompleted)
            {
                locationLoads.Remove(id);
                try
                {
                    var loaded = task.GetAwaiter().GetResult();
                    if (loadCancellation[id].IsCancellationRequested || !LoadReferenced(id))
                    {
                        loaded.Dispose();
                        continue;
                    }
                    var world = new PhysicsWorld(loaded.Definition, true, loaded.Generation.Store, [], loaded.Changes) { Epoch = ++nextLocationEpoch };
                    var c = new LocationContext { Runtime = loaded, Physics = world, UnusedTick = serverTick, CollisionReady = false, LoadStarted = loadStarts[id] };
                    loadStarts.Remove(id);
                    var association = links.Values.FirstOrDefault(l => l.Target.LocationId == id)?.Target;
                    if (association is null)
                    {
                        // IDs are retained in the pending relocation, whose key is supplied before loading.
                        association = loadAssociations[id];
                    }
                    SetAssociation(c, association);
                    contexts.Add(c);
                    loadAssociations.Remove(id);
                }
                catch (Exception ex)
                {
                    foreach (var c in contexts.Where(c => c.Prefetch?.Target?.LocationId == id))
                        c.Prefetch!.Failed = true;
                    loadAssociations.Remove(id);
                    loadStarts.Remove(id);
                    foreach (var link in links.Values.Where(l => l.Target.LocationId == id).ToArray())
                        Fail(link, "Destination preparation failed: " + ex.Message);
                    foreach (var (s, r) in relocations.Where(p => p.Value.Target == id).ToArray())
                    {
                        s.Peer.Send(new()
                        {
                            Kind = "terrain-status",
                            Reason = ex.Message
                        });
                        relocations.Remove(s);
                    }
                }
                finally
                {
                    loadStarts.Remove(id);
                    loadAssociations.Remove(id);
                    loadAbandonedAt.Remove(id);
                    loadCancellation.Remove(id, out var cancellation);
                    cancellation?.Dispose();
                }
            }
        foreach (var link in links.Values.ToArray())
        {
            // A reserved route opens independently of destination generation and visual readiness.
            if (link.Opening == 0)
                link.Opening = serverTick + 12;
            if (link.Destination is null)
            {
                var d = contexts.FirstOrDefault(c => c.Association?.LocationId == link.Target.LocationId);
                if (d is not null)
                {
                    if (d.Gate!.Phase != GatePhase.Idle)
                    {
                        Fail(link, "Destination reservation was lost.");
                        continue;
                    }
                    link.Destination = d;
                    var phase = serverTick >= link.Opening + GateEffectTimeline.ReceiverSettledTicks ? GatePhase.Open : serverTick >= link.Opening ? GatePhase.Opening : GatePhase.Preparing;
                    Publish(d, d.Gate with
                    {
                        Phase = phase,
                        ConnectionId = link.Id,
                        Incoming = true,
                        PhaseTick = phase == GatePhase.Open ? link.Opening + GateEffectTimeline.ReceiverSettledTicks : link.Opening + GateEffectTimeline.IncomingLeadTicks,
                        Status = "Incoming"
                    });
                }
            }
            bool hardLimit = ConnectionExpired(link);
            if (hardLimit)
                link.CloseRequested = true;
            if (link.CloseRequested && link.Closing == 0 && !HasEmergences(link) && (hardLimit || !travels.Values.Any(t => t.Link == link) && !npcFlights.Values.Any(t => t.Link == link)))
            {
                if (!RecoverPartialProps(link.Source, hardLimit) || link.Destination is { } closingReceiver && !RecoverPartialProps(closingReceiver, hardLimit))
                    continue;
                link.Closing = serverTick;
                foreach (var endpoint in new[] { link.Source, link.Destination }.OfType<LocationContext>())
                    Publish(endpoint, endpoint.Gate! with
                    {
                        Phase = GatePhase.Closing,
                        PhaseTick = serverTick,
                        Status = "Closing"
                    });
            }
            if (link.Closing != 0)
            {
                if (serverTick - link.Closing >= GateEffectTimeline.ClosingTicks)
                {
                    Idle(link.Source);
                    if (link.Destination is { } d)
                        Idle(d);
                    links.Remove(link.Id);
                }
                continue;
            }
            if (serverTick >= link.Opening && link.Source.Gate!.Phase == GatePhase.Preparing)
                Publish(link.Source, link.Source.Gate with
                {
                    Phase = GatePhase.Opening,
                    PhaseTick = link.Opening,
                    Status = "Opening"
                });
            if (link.Destination is { } receiver && serverTick >= link.Opening && receiver.Gate!.Phase == GatePhase.Preparing)
                Publish(receiver, receiver.Gate with
                {
                    Phase = GatePhase.Opening,
                    PhaseTick = link.Opening + GateEffectTimeline.IncomingLeadTicks,
                    Status = "Incoming",
                    Incoming = true
                });
            // Each endpoint settles on its own animation schedule. The source does not
            // wait for the receiver's additional seven-lock sequence.
            foreach (var endpoint in new[] { link.Source, link.Destination }.OfType<LocationContext>())
            {
                long settled = link.Opening + (endpoint == link.Source ? GateEffectTimeline.OpeningTicks : GateEffectTimeline.ReceiverSettledTicks);
                if (serverTick >= settled && endpoint.Gate!.Phase == GatePhase.Opening)
                    Publish(endpoint, endpoint.Gate with
                    {
                        Phase = GatePhase.Open,
                        PhaseTick = settled,
                        Status = "Connected"
                    });
            }
            if (serverTick >= link.Opening + GateEffectTimeline.ReceiverSettledTicks + 7200)
                link.CloseRequested = true;
        }
        foreach (var c in contexts.ToArray())
        {
            if (!c.CollisionReady && c.Physics.PrepareTerrainBatch(c.Runtime!.InitialCollision, 1))
            {
                c.Physics.SetTerrain(c.Runtime.InitialCollision);
                c.CollisionReady = true;
                if (c.Runtime.SavedBodies.Length > 0)
                    c.Physics.Apply(c.Physics.Snapshot() with
                    {
                        Bodies = c.Runtime.SavedBodies
                    });
                c.Runtime.BodiesDeferred = false;
                Console.WriteLine($"Location {c.Association!.LocationId} ready in {System.Diagnostics.Stopwatch.GetElapsedTime(c.LoadStarted).TotalMilliseconds:F1} ms; {TickTiming()}");
            }
            bool referenced = c == homeContext || TravelBusy(c) || c.Sessions.Count != 0 || UnattendedPhysicsActive(c) || LoadReferenced(c.Association?.LocationId ?? Guid.Empty);
            if (referenced)
            {
                c.UnusedTick = serverTick;
                c.UnloadSave = null;
            }
            else if (serverTick - c.UnusedTick > 1800 && c.Transition is null && c.Preparing is null && c.Edit is null)
            {
                c.UnloadSave ??= repository!.SaveCurrentBodiesAsync(c.Runtime!.Definition.LocationId, c.Runtime.BodiesDeferred ? c.Runtime.SavedBodies : c.Physics.Bodies(), c.Physics.World.Definition.Props);
                if (c.UnloadSave.IsCompletedSuccessfully)
                {
                    foreach (var key in farTiles.Keys.Where(k => k.Item1 == c.Runtime!.Definition.LocationId).ToArray())
                        farTiles.Remove(key);
                    ClearTravelHistory(c.Runtime!.Definition.LocationId);
                    contexts.Remove(c);
                    hazards.Remove(c);
                    c.Physics.Dispose();
                    c.Runtime!.Dispose();
                    continue;
                }
                if (c.UnloadSave.IsFaulted)
                {
                    Console.Error.WriteLine("Location unload save failed: " + c.UnloadSave.Exception!.GetBaseException().Message);
                    c.UnloadSave = null;
                    c.UnusedTick = serverTick;
                }
            }
            if (c.Gate is not { } g)
                continue;
            if (g.Owner != 0 && g.Phase is GatePhase.Dialing or GatePhase.Failed)
            {
                var owner = c.Sessions.FirstOrDefault(s => s.PlayerId == g.Owner && !s.Peer.Closed);
                var dhd = c.Physics.World.Definition.Entities.First(e => e.Model == "dhd");
                var eye = owner is null ? Vector3.Zero : Movement.EyePosition(c.Physics.Player(g.Owner));
                bool away = owner is null || !DhdInteraction.WithinReach(dhd, eye);
                if (away && c.OwnerAwayTick == 0)
                    c.OwnerAwayTick = serverTick;
                if (!away)
                    c.OwnerAwayTick = 0;
                if (owner is null || c.OwnerAwayTick != 0 && serverTick - c.OwnerAwayTick >= 120 || serverTick - c.LastDialTick >= 1800)
                    Idle(c);
            }
        }
        TickClientPreloads();
        TickRelocations();
    }
    readonly Dictionary<Guid, GateAssociation> loadAssociations = [];
    void CancelRelocation(Session s, string reason)
    {
        if (!relocations.Remove(s, out var r) || r.Transfer == 0)
        {
            s.Peer.Send(new()
            {
                Kind = "terrain-status",
                Reason = reason
            });
            return;
        }
        s.Peer.Send(new()
        {
            Kind = "terrain-cancel",
            TransferId = r.Transfer,
            Reason = reason
        });
        s.CancelledRelocation = Math.Max(s.CancelledRelocation, r.Transfer);
        s.PreparedRuntime = null;
        s.PreparedCoordinates.Clear();
        var queued = s.WorldQueue.Where(m => m.TransferId != r.Transfer).ToArray();
        s.WorldQueue.Clear();
        foreach (var message in queued)
            s.WorldQueue.Enqueue(message);
        var retained = s.TerrainQueue.Where(q => q.Transfer != r.Transfer).ToArray();
        s.TerrainQueue.Clear();
        foreach (var item in retained)
            s.TerrainQueue.Enqueue(item);
        if (s.PayloadTransfer == r.Transfer)
            s.Payload = null;
    }
    void TickRelocations()
    {
        foreach (var (s, r) in relocations.ToArray())
        {
            if (s.Peer.Closed)
            {
                relocations.Remove(s);
                continue;
            }
            if (serverTick - r.Started > 1800)
            {
                CancelRelocation(s, "Developer destination preparation timed out.");
                continue;
            }
            var target = contexts.FirstOrDefault(c => c.Association?.LocationId == r.Target && c.CollisionReady);
            if (target is null)
                continue;
            if (target.Runtime is { BodiesDeferred: true } deferred)
            {
                deferred.ArrivalChunks ??= PhysicsWorld.IncludeBuildingOwners(deferred.Changes, deferred.InitialCollision.Concat(deferred.SavedBodies.SelectMany(b => ChunkCoordinate.At(b.Position.X, b.Position.Z).Around(1))));
                deferred.ArrivalData ??= Task.WhenAll(deferred.ArrivalChunks.Select(c => deferred.Generation.RequestAsync(c, stop.Token)));
                if (!deferred.ArrivalData.IsCompleted)
                    continue;
                if (!deferred.ArrivalData.IsCompletedSuccessfully)
                {
                    _ = deferred.ArrivalData.Exception;
                    CancelRelocation(s, "Saved body region preparation failed.");
                    continue;
                }
                if (!target.Physics.PrepareTerrainBatch(deferred.ArrivalChunks, 1))
                    continue;
                target.Physics.SetTerrain(deferred.ArrivalChunks);
                target.Physics.Apply(target.Physics.Snapshot() with
                {
                    Bodies = deferred.SavedBodies
                });
                deferred.BodiesDeferred = false;
            }
            var source = contexts.Single(c => c.Sessions.Contains(s));
            if (source.Transition is not null || source.Preparing is not null || source.Edit is not null || target.Transition is not null || target.Preparing is not null || target.Edit is not null)
                continue;
            context = source;
            if (r.Transfer == 0)
            {
                r.Transfer = ++nextTransfer;
                SendPreparation(s, r.Transfer, target.Runtime!, target.Physics.TerrainCoordinates.ToArray(), true);
                continue;
            }
            if (!r.Ready)
                continue;
            var player = source.Physics.Player(s.PlayerId);
            source.Physics.RemovePlayer(s.PlayerId);
            source.Physics.Epoch++;
            source.Sessions.Remove(s);
            target.Physics.AddPlayer(player with
            {
                Position = target.Physics.World.Spawn(target.Sessions.Count),
                Velocity = Vector3.Zero,
                LastInput = 0,
                ArrivalMomentum = default,
                ArrivalMomentumActive = false,
                TransitFrozen = false
            });
            target.Physics.Epoch = Math.Max(target.Physics.Epoch, source.Physics.Epoch) + 1;
            target.Sessions.Add(s);
            s.Inputs.Clear();
            s.LastSequence = 0;
            s.LastTarget = target.Physics.Tick;
            s.PreparedRuntime = null;
            s.PreparedCoordinates.Clear();
            s.VisualPending.Clear();
            s.TerrainQueue.Clear();
            s.Payload = null;
            s.LookupReceipt = null;
            s.LookupCommand = null;
            s.CheckedRequest = Guid.Empty;
            s.BuildResults.Clear();
            s.GalaxyLookup = null;
            s.GalaxyLookupCommand = null;
            s.ResolvedTarget = null;
            s.ResolvedRequest = Guid.Empty;
            s.WorldQueue.Clear();
            s.Peer.Send(new()
            {
                Kind = "location-commit",
                TransferId = r.Transfer,
                LocationId = r.Target,
                State = target.Physics.Snapshot(),
                Tick = target.Physics.Tick
            });
            context = target;
            SendGateBaseline(s);
            relocations.Remove(s);
        }
        context = homeContext;
    }
}
