using System.Diagnostics;
using System.Numerics;
using StargateGame.Persistence;
using StargateGame.Physics;
using StargateGame.Shared;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    sealed class PendingTravel
    {
        public Guid Id = Guid.NewGuid();
        public Link Link = null!;
        public Session? Session;
        public LocationContext Source = null!;
        public LocationContext? Destination;
        public PlayerState? Player, ExitPlayer;
        public PropDefinition? Prop, ExitProp;
        public BodyState? Body, ExitBody;
        public TransitRecord Record = null!;
        public Task Journal = Task.CompletedTask;
        public Task? Commit, Return;
        public long CapturedAt = Stopwatch.GetTimestamp();
        public long Started, Transfer, BlockedSince, Revision;
        public bool Removed, Ready;
        public ChunkCoordinate[]? Chunks;
        public Task? Data;
        public long LastExitProbeTick, NextExitProbe;
        public string? Failure;
    }
    readonly List<(LocationContext Location, long Tick, Vector3 Position)> arrivals = [];
    readonly Dictionary<Guid, PendingTravel> travels = [];
    readonly Dictionary<(Guid, int), BodyState> travelBodies = [];
    readonly Dictionary<(Guid, int), PlayerState> travelPlayers = [];
    readonly Dictionary<(Guid, int), BodyState> propSafe = [];
    sealed record PropDestruction(LocationContext Context, PropDefinition Prop, Task Save);
    readonly List<PropDestruction> propDestructions = [];
    sealed class EmergingProp(Guid id, Link link, LocationContext location, PropDefinition prop)
    {
        public Guid Id = id;
        public Link Link = link;
        public LocationContext Location = location;
        public PropDefinition Prop = prop;
        public Task? Completion;
        public BodyState CompletedPose;
    }
    readonly Dictionary<Guid, EmergingProp> emergingProps = [];
    bool HasEmergences(Link link) => emergingProps.Values.Any(e => e.Link == link);
    bool ConnectionExpired(Link link) => GateTravel.ConnectionExpired(serverTick, link.Opening + GateEffectTimeline.OpeningTicks);
    void TickEmergences()
    {
        foreach (var e in emergingProps.Values.ToArray())
        {
            if (e.Completion is not null)
            {
                if (e.Completion.IsCompletedSuccessfully)
                {
                    emergingProps.Remove(e.Id);
                    continue;
                }
                if (e.Completion.IsFaulted)
                {
                    Console.Error.WriteLine("Emergence completion save failed; retrying: " + e.Completion.Exception);
                    e.Completion = repository!.CompletePropEmergenceAsync(e.Id, e.Location.Physics.World.Definition.LocationId, e.Prop, e.CompletedPose);
                }
                continue;
            }
            if (propDestructions.Any(d => d.Context == e.Location && d.Prop.PersistentId == e.Prop.PersistentId))
                continue;
            var pose = e.Location.Physics.Bodies().Single(b => b.Id == e.Prop.Id);
            var extents = GateTravel.Extents(Frame(e.Location), pose, e.Prop);
            if (extents.Min >= .02f)
            {
                e.Location.Physics.FinishPropEmergence(e.Prop.Id);
                e.CompletedPose = pose;
                e.Completion = repository!.CompletePropEmergenceAsync(e.Id, e.Location.Physics.World.Definition.LocationId, e.Prop, pose);
                Console.WriteLine($"EMERGENCE item={e.Prop.PersistentId} phase=cleared");
            }
            else if (extents.Max <= 0 || ConnectionExpired(e.Link))
            {
                e.Location.Physics.HoldProp(e.Prop.Id);
                propDestructions.Add(new(e.Location, e.Prop, repository!.DestroyPropAsync(e.Location.Physics.World.Definition.LocationId, e.Prop.PersistentId)));
                Console.WriteLine($"EMERGENCE item={e.Prop.PersistentId} phase=destroyed reason={(extents.Max <= 0 ? "reverse-entry" : "38-minute-limit")}");
            }
        }
    }
    double exitSearchMilliseconds;
    bool TravelBusy(LocationContext c) => npcFlights.Values.Any(f => f.Source == c || f.Destination == c) || c.Gate?.Phase == GatePhase.Opening || hazards.TryGetValue(c, out var hazard) && hazard.Pending || travels.Values.Any(t => t.Source == c || t.Destination == c) || propDestructions.Any(d => d.Context == c) || emergingProps.Values.Any(e => e.Location == c);
    bool UnattendedPhysicsActive(LocationContext c) => c.Physics.HasMovingDoors || fixtureConnection is not null || links.Values.Any(l => l.Source == c || l.Destination == c) || c.Physics.NeedsPropSimulation || c.Physics.NpcMotions.Any(n=>c.Physics.HasTerrain(n.Position,2)&&(!n.Grounded||n.Velocity.LengthSquared()>.01f));
    void Notice(PendingTravel t, TransitPhase phase, string? reason = null)
    {
        Console.WriteLine($"TRANSIT id={t.Id} item={t.Prop?.PersistentId} connection={t.Link.Id} source={t.Record.Source} destination={t.Record.Destination} phase={phase} sourceEpoch={t.Source.Physics.Epoch} reason={reason}");
        if (t.Session is { } player)
            player.Peer.Send(new()
            {
                Kind = "transit",
                Transit = new(t.Id, t.Link.Id, t.Record.Source, t.Record.Destination, phase, t.Started, t.Transfer, reason),
                LocationId = t.Record.Source,
                GateTick = serverTick
            });
        else if (reason is not null)
            foreach (var observer in t.Source.Sessions.Where(s => !s.Peer.Closed && s.PlayerId != 0))
            {
                if (Vector3.DistanceSquared(t.Source.Physics.Player(observer.PlayerId).Position, Frame(t.Source).Centre) > 900)
                    continue;
                observer.Peer.Send(new()
                {
                    Kind = "item-transit",
                    Reason = phase is TransitPhase.Returning or TransitPhase.Cancelled ? "Item returned: " + reason : reason,
                    LocationId = t.Record.Source
                });
            }
    }
    GateFrame Frame(LocationContext c) => GateFrame.From(c.Physics.World.Definition.Entities.Single(e => e.Model == "stargate"));
    readonly Dictionary<Guid, long> travelHistoryEpochs = [];

    void ClearTravelHistory(Guid location)
    {
        foreach (var key in travelBodies.Keys.Where(key => key.Item1 == location).ToArray())
            travelBodies.Remove(key);
        foreach (var key in travelPlayers.Keys.Where(key => key.Item1 == location).ToArray())
            travelPlayers.Remove(key);
        foreach (var key in propSafe.Keys.Where(key => key.Item1 == location).ToArray())
            propSafe.Remove(key);
        travelHistoryEpochs.Remove(location);
    }

    void ObserveTravel()
    {
        var location = physics.World.Definition.LocationId;
        if (!travelHistoryEpochs.TryGetValue(location, out var previousEpoch) || previousEpoch != physics.Epoch)
        {
            ClearTravelHistory(location);
            travelHistoryEpochs[location] = physics.Epoch;
        }
        var activePlayers = physics.Players.Select(player => player.Id).ToHashSet();
        foreach (var key in travelPlayers.Keys.Where(key => key.Item1 == location && !activePlayers.Contains(key.Item2)).ToArray())
            travelPlayers.Remove(key);
        var link = links.Values.FirstOrDefault(l => l.Source == context && l.Source.Gate?.Settled == true && l.Closing == 0 && !l.CloseRequested);
        bool available = link is not null && context.Transition?.Data.Switch != true && context.Edit is null;
        GateFrame? gate = link is null ? null : Frame(context);
        physics.TravelGate = gate;
        ObserveNpcTravel(available ? gate : null, link?.Destination is { } npcTarget ? Frame(npcTarget) : null, link);
        var endpoint = links.Values.FirstOrDefault(l => (l.Source == context || l.Destination == context) && context.Gate?.Settled == true && l.Closing == 0 && !l.CloseRequested);
        bool propsAvailable = endpoint is not null && context.Transition?.Data.Switch != true && context.Edit is null;
        foreach (var player in physics.Players.ToArray())
        {
            var key = (location, player.Id);
            var prior = travelPlayers.GetValueOrDefault(key, player);
            travelPlayers[key] = player;
            if (!available || player.TransitFrozen || !GateTravel.PlayerCrossing(gate!.Value, prior.Position, player.Position, Movement.BodyHeight(player)))
                continue;
            var session = sessions.FirstOrDefault(s => s.PlayerId == player.Id && !s.Peer.Closed);
            if (session is null || session.SavedPlayer is null || travels.Values.Any(t => t.Session == session))
                continue;
            var safe = prior with
            {
                Velocity = default,
                ArrivalMomentum = default,
                TransitFrozen = false
            };
            if (!physics.CanPlacePlayer(safe.Position, player.Id))
                safe = physics.ResolveCharacterExit(gate!.Value, safe, out _, player.Id);
            CancelTravelSourcePreparation();
            SavePlayer(session);
            var t = new PendingTravel { Link = link!, Session = session, Source = context, Destination = link!.Destination!, Player = player, Started = serverTick };
            t.Record = new(t.Id, link.Id, location, link.Target.LocationId, session.SavedPlayer with
            {
                State = safe
            }, null, null);
            t.Journal = repository!.CaptureTransitAsync(t.Record, physics.Bodies(), physics.World.Definition.Props);
            physics.FreezePlayer(player.Id, true);
            travels.Add(t.Id, t);
            Notice(t, TransitPhase.Captured);
        }
        var current = physics.Bodies();
        foreach (var body in current)
        {
            var key = (location, body.Id);
            bool observed = travelBodies.TryGetValue(key, out var prior);
            travelBodies[key] = body;
            if (emergingProps.Values.Any(e => e.Location == context && e.Prop.Id == body.Id) || !propsAvailable || !observed || travels.Values.Any(t => t.Source == context && t.Prop?.Id == body.Id) || propDestructions.Any(d => d.Context == context && d.Prop.Id == body.Id))
                continue;
            var definition = physics.World.Definition.Props.Single(p => p.Id == body.Id);
            var front = Frame(context);
            var activeClip = physics.CurrentClips().FirstOrDefault(c => c.Body == body.Id);
            // Lock the approach side while intersecting. Rear entry clips the
            // opposite half-space; receiving-side entry never becomes travel.
            var safe = propSafe.GetValueOrDefault(key, prior);
            var entry = activeClip?.Gate ?? (front.Distance(prior.Position) >= 0 ? front : front with
            {
                Rotation = Quaternion.Normalize(front.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI))
            });
            var e = GateTravel.Extents(entry, body, definition);
            if (e.Min > .02f)
            {
                propSafe[key] = body;
                continue;
            }
            if (!GateTravel.SweptFits(entry, prior, body, definition))
            {
                if (physics.CurrentClips().Any(c => c.Body == body.Id) && TryPropRecovery(context, definition, safe with
                {
                    LinearVelocity = default,
                    AngularVelocity = default
                }, body.Id, out var recovery))
                {
                    Console.WriteLine($"PROP RECOVERY item={definition.PersistentId} location={location} reason=aperture-sweep prior={prior.Position} current={body.Position} extents={e} epoch={physics.Epoch}");
                    physics.Apply(physics.Snapshot() with
                    {
                        Bodies = physics.Bodies().Select(b => b.Id == body.Id ? recovery : b).ToArray(),
                        Clips = physics.CurrentClips().Where(c => c.Body != body.Id).ToArray()
                    });
                }
                propSafe.Remove(key);
                continue;
            }
            // A newly loaded body already intersecting a settled sheet is clipped
            // quietly, but a body wholly behind it is not retroactively captured.
            if (!propSafe.ContainsKey(key) && activeClip is null && e.Max <= GateTravel.PropCompletionDepth)
                continue;
            if (e.Min < 0)
                physics.BeginPropEntry(body.Id, entry);
            if (e.Max > GateTravel.PropCompletionDepth || !e.Fits)
                continue;
            if (travels.Values.Any(t => t.Destination == context && t.Commit is not null))
                continue;
            CancelTravelSourcePreparation();
            if (endpoint!.Source != context || Vector3.Dot(entry.Normal, front.Normal) < 0)
            {
                physics.HoldProp(body.Id);
                propDestructions.Add(new(context, definition, repository!.DestroyPropAsync(location, definition.PersistentId)));
                Console.WriteLine($"Gate absorbed wrong-way prop {definition.PersistentId} at {location}");
                continue;
            }
            var t = new PendingTravel { Link = link!, Source = context, Destination = link!.Destination, Prop = definition, Body = body, Started = serverTick };
            t.Record = new(t.Id, link.Id, location, link.Target.LocationId, null, definition, safe with
            {
                LinearVelocity = default,
                AngularVelocity = default
            });
            t.Journal = repository!.CaptureTransitAsync(t.Record, current.Where(b => b.Id != body.Id).ToArray(), physics.World.Definition.Props.Where(p => p.Id != body.Id).ToArray());
            physics.HoldProp(body.Id);
            travels.Add(t.Id, t);
            propSafe.Remove(key);
            Notice(t, TransitPhase.Captured);
            Console.WriteLine($"Transit {t.Id}: captured prop {definition.PersistentId}, velocity {body.LinearVelocity}");
        }
        foreach (var key in travelBodies.Keys.Where(k => k.Item1 == location && !current.Any(b => b.Id == k.Item2)).ToArray())
        {
            travelBodies.Remove(key);
            propSafe.Remove(key);
        }
    }
    void CancelTravelSourcePreparation()
    {
        // Canonical coverage can be retried after travel. Do not leave its client transfer
        // overlapping the destination preparation, or deadlock its activation behind transit.
        if (context.Preparing is { } pending)
        {
            context.DestinationStop?.Cancel();
            context.Preparing = null;
            _ = pending.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result.Switch)
                    t.Result.Runtime.Dispose();
                else
                    _ = t.Exception;
            });
        }
        if (context.Transition is { } change)
        {
            context.Transition = null;
            lastCancelledTransfer = Math.Max(lastCancelledTransfer, change.Id);
            Broadcast(new()
            {
                Kind = "terrain-cancel",
                TransferId = change.Id
            });
            physics.CancelChanges();
        }
    }
    bool HandleTravel(Session s, Message m)
    {
        if (m.Kind == "terrain-ready")
        {
            var t = travels.Values.FirstOrDefault(t => t.Session == s && t.Transfer == m.TransferId && t.Transfer != 0);
            return t is not null;
        }
        if (m.Kind == "transit-ready")
        {
            if (m.Transit is { } notice && travels.TryGetValue(notice.Id, out var t) && t.Session == s && t.Link.Id == notice.Connection && t.Transfer != 0 && t.Transfer == notice.Transfer && notice.Source == t.Record.Source && notice.Destination == t.Record.Destination)
            {
                t.Ready = true;
                Notice(t, TransitPhase.Ready);
            }
            return true;
        }
        if (m.Kind == "transit-applied")
            return true;
        return false;
    }
    void TickTravel()
    {
        TickNpcTravel();
        TickEmergences();
        foreach (var destruction in propDestructions.Where(d => d.Save.IsCompleted).ToArray())
        {
            propDestructions.Remove(destruction);
            if (!destruction.Save.IsCompletedSuccessfully)
            {
                Console.Error.WriteLine("Prop destruction save failed; retrying: " + destruction.Save.Exception?.GetBaseException().Message);
                propDestructions.Add(destruction with
                {
                    Save = repository!.DestroyPropAsync(destruction.Context.Physics.World.Definition.LocationId, destruction.Prop.PersistentId)
                });
                continue;
            }
            foreach (var e in emergingProps.Values.Where(e => e.Location == destruction.Context && e.Prop.PersistentId == destruction.Prop.PersistentId).ToArray())
                emergingProps.Remove(e.Id);
            var c = destruction.Context;
            c.Physics.RemoveProp(destruction.Prop.Id);
            c.Physics.Epoch++;
            c.Runtime!.Definition = c.Physics.World.Definition;
            var key = (c.Physics.World.Definition.LocationId, destruction.Prop.Id);
            propSafe.Remove(key);
            travelBodies.Remove(key);
        }
        foreach (var arrival in arrivals.Where(a => serverTick >= a.Tick).ToArray())
        {
            var previous = context;
            context = arrival.Location;
            EmitSound(WorldSoundKind.GateArrival, arrival.Position);
            context = previous;
            arrivals.Remove(arrival);
        }
        exitSearchMilliseconds = 0;
        // Oldest probe first so one blocked arrival cannot monopolize the shared
        // query budget. Keep journal/ownership processing running for every item.
        foreach (var t in travels.Values.OrderBy(t => t.LastExitProbeTick).ToArray())
        {
            if (!t.Journal.IsCompleted)
                continue;
            if (!t.Journal.IsCompletedSuccessfully)
                t.Failure = "Transit journal failed.";
            if (t.Commit is { IsCompleted: false } || t.Return is { IsCompleted: false })
                continue;
            if (t.Commit is { IsCompletedSuccessfully: true })
            {
                ReleaseTravel(t);
                continue;
            }
            if (t.Commit?.IsFaulted == true)
            {
                _ = t.Commit.Exception;
                t.Failure = "Transfer commit failed.";
            }
            if (ConnectionExpired(t.Link))
                t.Failure = "Connection reached its 38-minute limit.";
            if (t.Session?.Peer.Closed == true)
                t.Failure = "Traveler disconnected.";
            if (Stopwatch.GetElapsedTime(t.CapturedAt).TotalSeconds > 30)
                t.Failure = "Destination readiness timed out.";
            if (t.Failure is not null)
            {
                ReturnTravel(t);
                continue;
            }
            if (!t.Removed && t.Prop is not null)
            {
                t.Source.Physics.RemoveProp(t.Prop.Id);
                t.Source.Physics.Epoch++;
                t.Source.Runtime!.Definition = t.Source.Physics.World.Definition;
                t.Removed = true;
            }
            t.Destination ??= t.Link.Destination;
            if (t.Destination is not { CollisionReady: true } target)
                continue;
            if (t.Player is { } captured && t.ExitPlayer is null)
                t.ExitPlayer = GateTravel.Exit(Frame(t.Source), Frame(target), captured);
            if (t.Prop is { } prop && t.ExitProp is null)
            {
                var used = target.Physics.World.Definition.Props.Select(p => p.Id).Concat(travels.Values.Where(o => o.Destination == target).Select(o => o.ExitProp?.Id ?? 0)).Concat(travels.Values.Where(o => o.Source == target).Select(o => o.Prop?.Id ?? 0)).ToHashSet();
                int id = Enumerable.Range(1, 64).FirstOrDefault(i => !used.Contains(i));
                if (prop.Kind == PropKind.Lantern && LanternCount(target.Physics) >= LanternAsset.LocationLimit)
                {
                    t.Failure = "Destination lantern limit reached (8).";
                    continue;
                }
                if (id == 0)
                {
                    t.Failure = "Destination prop registry is full.";
                    continue;
                }
                var exit = GateTravel.Exit(Frame(t.Source), Frame(target), t.Body!.Value, prop) with
                {
                    Id = id
                };
                t.ExitBody = exit;
                t.ExitProp = prop with
                {
                    Id = id,
                    Position = exit.Position
                };
            }
            var runtime = target.Runtime!;
            var position = t.ExitPlayer?.Position ?? t.ExitBody!.Value.Position;
            // The traveler needs the landing region, not every other explorer's
            // collision coverage. Keep those regions installed on the server.
            t.Chunks ??= PhysicsWorld.IncludeBuildingOwners(runtime.Changes, ChunkCoordinate.At(position.X, position.Z).Around(1));
            t.Data ??= Task.WhenAll(t.Chunks.Select(c => runtime.Generation.RequestAsync(c, stop.Token)));
            if (!t.Data.IsCompleted)
                continue;
            if (!t.Data.IsCompletedSuccessfully)
            {
                t.Failure = "Destination terrain failed.";
                continue;
            }
            if (target.Transition is not null || target.Preparing is not null || target.Edit is not null)
                continue;
            if (!target.Physics.PrepareTerrainBatch(t.Chunks, 1))
                continue;
            if (!t.Chunks.All(target.Physics.TerrainCoordinates.Contains))
                target.Physics.SetTerrain(target.Physics.TerrainCoordinates.Concat(t.Chunks).Distinct());
            if (t.Session is { } session && t.Transfer == 0)
            {
                t.Transfer = ++nextTransfer;
                t.Revision = runtime.Changes.Revision;
                runtime.Definition = target.Physics.World.Definition;
                Notice(t, TransitPhase.Preparing);
                SendPreparation(session, t.Transfer, runtime, t.Chunks, true);
                continue;
            }
            if (Stopwatch.GetElapsedTime(t.CapturedAt).TotalSeconds < GateTravel.MinimumTransitSeconds || t.Session is not null && !t.Ready)
                continue;
            if (t.Session is not null && runtime.Changes.Revision != t.Revision)
            {
                t.Failure = "Destination buildings changed during preparation.";
                continue;
            }
            if (t.BlockedSince != 0 && Stopwatch.GetElapsedTime(t.BlockedSince).TotalSeconds >= 10)
            {
                t.Failure = "Receiving gate exit is obstructed.";
                continue;
            }
            bool reserved = npcFlights.Values.Any(f => f.Destination == target && f.Commit is not null) || propDestructions.Any(d => d.Context == target) || travels.Values.Any(o => o != t && (o.Destination == target && o.Commit is not null || o.Source == target && o.Prop is not null && !o.Removed));
            bool clear = false;
            if (!reserved)
            {
                if (t.Player is not null)
                {
                    t.ExitPlayer = target.Physics.ResolveCharacterExit(Frame(target), t.ExitPlayer!.Value, out bool launched);
                    Console.WriteLine($"TRANSIT character={t.Player.Value.Id} exit={(launched ? "centre-launch" : "clear")} position={t.ExitPlayer.Value.Position}");
                    clear = true;
                }
                else
                {
                    if (exitSearchMilliseconds >= 1 || Stopwatch.GetTimestamp() < t.NextExitProbe)
                        continue;
                    long queryStart = Stopwatch.GetTimestamp();
                    t.LastExitProbeTick = serverTick;
                    clear = target.Physics.CanBeginPropEmergence(Frame(target), t.ExitProp!, t.ExitBody!.Value);
                    exitSearchMilliseconds += Stopwatch.GetElapsedTime(queryStart).TotalMilliseconds;
                    if (!clear)
                        t.NextExitProbe = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
                }
            }
            if (!clear)
            {
                if (t.BlockedSince == 0)
                {
                    t.BlockedSince = Stopwatch.GetTimestamp();
                    Notice(t, TransitPhase.Ready, t.Prop is null ? "Waiting for occupied exit" : "Item waiting for a clear exit");
                }
                continue;
            }
            CommitTravel(t, target);
        }
    }

    // Commit ownership before releasing simulation. The exit reservation survives
    // the asynchronous save, so another arrival cannot occupy the same space.
    void CommitTravel(PendingTravel travel, LocationContext destination)
    {
        Notice(travel, TransitPhase.Committing);
        Guid location = destination.Runtime!.Definition.LocationId;
        if (travel.Session is { } traveler)
        {
            var saved = traveler.SavedPlayer! with
            {
                Location = location,
                State = travel.ExitPlayer!.Value
            };
            travel.Commit = repository!.CommitPlayerTransitAsync(travel.Id, saved);
            return;
        }

        var physics = destination.Physics;
        physics.ReservePropEmergence(travel.Id, Frame(destination), travel.ExitProp!, travel.ExitBody!.Value);
        var bodies = physics.Bodies().Append(travel.ExitBody!.Value).ToArray();
        var props = physics.World.Definition.Props.Append(travel.ExitProp!).ToArray();
        travel.Commit = repository!.CommitPropTransitAsync(travel.Id, location, bodies, props, emerging: true);
    }
    void ReleaseTravel(PendingTravel t)
    {
        Console.WriteLine($"Transit {t.Id}: released after {Stopwatch.GetElapsedTime(t.CapturedAt).TotalSeconds:F3}s to {t.Record.Destination}");
        if (t.Session is { } s)
        {
            var state = t.ExitPlayer!.Value with
            {
                LastInput = 0
            };
            t.Source.Physics.RemovePlayer(s.PlayerId);
            t.Source.Physics.Epoch++;
            t.Source.Sessions.Remove(s);
            t.Destination!.Physics.AddPlayer(state);
            t.Destination!.Physics.Epoch = Math.Max(t.Source.Physics.Epoch, t.Destination!.Physics.Epoch) + 1;
            t.Destination!.Sessions.Add(s);
            s.Inputs.Clear();
            s.LastSequence = 0;
            s.LastTarget = t.Destination!.Physics.Tick;
            s.PreparedRuntime = null;
            s.PreparedCoordinates.Clear();
            s.VisualPending.Clear();
            s.TerrainQueue.Clear();
            s.Payload = null;
            s.WorldQueue.Clear();
            s.SavedPlayer = s.SavedPlayer! with
            {
                Location = t.Destination!.Physics.World.Definition.LocationId,
                State = state
            };
            s.Peer.Send(new()
            {
                Kind = "location-commit",
                Transit = new(t.Id, t.Link.Id, t.Record.Source, t.Record.Destination, TransitPhase.Released, t.Started, t.Transfer),
                TransferId = t.Transfer,
                LocationId = t.Record.Destination,
                State = t.Destination!.Physics.Snapshot(),
                Tick = t.Destination!.Physics.Tick
            });
            var prior = context;
            context = t.Destination;
            SendGateBaseline(s);
            context = prior;
            travelPlayers[(t.Record.Destination, s.PlayerId)] = state;
        }
        else
        {
            t.Destination!.Physics.ReleasePropExit(t.Id);
            t.Destination.Physics.InsertProp(t.ExitProp!, t.ExitBody!.Value);
            t.Destination.Physics.BeginPropEmergence(t.ExitProp!.Id, Frame(t.Destination), t.Link.Id);
            emergingProps.Add(t.Id, new(t.Id, t.Link, t.Destination, t.ExitProp));
            t.Destination!.Physics.Epoch++;
            t.Destination!.Runtime!.Definition = t.Destination!.Physics.World.Definition;
            travelBodies[(t.Record.Destination, t.ExitProp!.Id)] = t.ExitBody!.Value;
        }
        arrivals.Add((t.Destination!, serverTick + (t.Prop is null ? 9 : 0), t.ExitPlayer?.Position ?? t.ExitBody!.Value.Position));
        Console.WriteLine($"TRANSIT id={t.Id} item={t.Prop?.PersistentId} phase=released destinationEpoch={t.Destination!.Physics.Epoch}");
        travels.Remove(t.Id);
    }
    void ReturnTravel(PendingTravel t)
    {
        t.Destination?.Physics.ReleasePropExit(t.Id);
        if (t.Return is null)
            Console.WriteLine($"Transit {t.Id}: returning: {t.Failure}");
        if (t.Return is null)
        {
            if (t.Record.Player is { } player)
            {
                var gate = Frame(t.Source);
                var desired = player.State.Position + gate.Normal * Math.Max(0, Movement.Radius + .35f - gate.Distance(player.State.Position));
                if (!TryPlayerRecovery(t.Source, desired, t.Session!.PlayerId, out var position))
                    return;
                t.Record = t.Record with
                {
                    Player = player with
                    {
                        State = player.State with
                        {
                            Position = position
                        }
                    }
                };
            }
            else
            {
                var safe = t.Record.Body!.Value with
                {
                    LinearVelocity = default,
                    AngularVelocity = default
                };
                if (!TryPropRecovery(t.Source, t.Prop!, safe, t.Removed ? 0 : t.Prop!.Id, out safe))
                    return;
                t.Record = t.Record with
                {
                    Body = safe
                };
            }
            t.Return = t.Journal.IsCompletedSuccessfully ? repository!.ReturnTransitAsync(t.Id, t.Record) : Task.CompletedTask;
            Notice(t, TransitPhase.Returning, t.Failure);
            return;
        }
        if (!t.Return.IsCompletedSuccessfully)
        {
            Console.Error.WriteLine("Transit recovery save failed: " + t.Return.Exception?.GetBaseException().Message);
            t.Return = null;
            return;
        }
        if (t.Session is { } s)
        {
            var saved = t.Record.Player!;
            var state = saved.State with
            {
                Id = s.PlayerId,
                LastInput = s.LastSequence
            };
            t.Source.Physics.RemovePlayer(s.PlayerId);
            if (!s.Peer.Closed)
                t.Source.Physics.AddPlayer(state);
            t.Source.Physics.Epoch++;
            s.SavedPlayer = saved with
            {
                State = state
            };
            if (t.Transfer != 0)
            {
                s.CancelledRelocation = Math.Max(s.CancelledRelocation, t.Transfer);
                s.Peer.Send(new()
                {
                    Kind = "terrain-cancel",
                    TransferId = t.Transfer
                });
                s.WorldQueue.Clear();
                s.TerrainQueue.Clear();
                s.Payload = null;
                s.PreparedRuntime = null;
            }
            if (s.Peer.Closed)
            {
                t.Source.Sessions.Remove(s);
                s.PlayerId = 0;
            }
            Notice(t, TransitPhase.Cancelled, t.Failure);
        }
        else
        {
            if (!t.Removed)
                t.Source.Physics.RemoveProp(t.Prop!.Id);
            var state = t.Record.Body!.Value;
            t.Source.Physics.InsertProp(t.Prop!, state);
            t.Source.Physics.Epoch++;
            t.Source.Runtime!.Definition = t.Source.Physics.World.Definition;
        }
        travels.Remove(t.Id);
    }
    bool TryPropRecovery(LocationContext source, PropDefinition prop, BodyState desired, int ignore, out BodyState result)
    {
        var gate = Frame(source);
        foreach (float forward in new[] { 0f, 1f, 2f, 4f })
            foreach (float sideways in new[] { 0f, 1.5f, -1.5f, 3f, -3f })
            {
                var candidate = desired with
                {
                    Position = desired.Position + gate.Normal * forward + Vector3.Transform(Vector3.UnitX, gate.Rotation) * sideways
                };
                if (source.Physics.CanPlaceProp(prop, candidate, ignore))
                {
                    result = candidate;
                    return true;
                }
            }
        foreach (var spawn in source.Physics.World.Definition.Spawns)
        {
            var candidate = desired with
            {
                Position = spawn + Vector3.UnitY * (prop.HalfExtent.Length() + .1f)
            };
            if (source.Physics.CanPlaceProp(prop, candidate, ignore))
            {
                Console.Error.WriteLine("Prop recovery used the source spawn.");
                result = candidate;
                return true;
            }
        }
        result = desired;
        return false;
    }
    bool RecoverPartialProps(LocationContext source, bool hardLimit = false)
    {
        foreach (var clip in source.Physics.CurrentClips())
        {
            if (clip.Mode == PropClipMode.Emergence)
                return false;
            if (propDestructions.Any(d => d.Context == source && d.Prop.Id == clip.Body))
                return false;
            if (travels.Values.Any(t => t.Source == source && t.Prop?.Id == clip.Body))
                continue;
            var definition = source.Physics.World.Definition.Props.Single(p => p.Id == clip.Body);
            var current = source.Physics.Bodies().Single(b => b.Id == clip.Body);
            var safe = propSafe.GetValueOrDefault((source.Physics.World.Definition.LocationId, clip.Body), current);
            safe = safe with
            {
                Position = safe.Position + clip.Gate.Normal * Math.Max(0, .04f - GateTravel.Extents(clip.Gate, safe, definition).Min),
                LinearVelocity = default,
                AngularVelocity = default
            };
            if (!TryPropRecovery(source, definition, safe, clip.Body, out safe))
            {
                // At the hard limit an obstructed insertion cannot keep the
                // connection alive forever. Persist removal before publishing it.
                if (hardLimit)
                {
                    source.Physics.HoldProp(clip.Body);
                    propDestructions.Add(new(source, definition, repository!.DestroyPropAsync(source.Physics.World.Definition.LocationId, definition.PersistentId)));
                }
                return false;
            }
            source.Physics.Apply(source.Physics.Snapshot() with
            {
                Bodies = source.Physics.Bodies().Select(b => b.Id == clip.Body ? safe : b).ToArray(),
                Clips = source.Physics.CurrentClips().Where(c => c.Body != clip.Body).ToArray()
            });
        }
        return true;
    }
    bool TryPlayerRecovery(LocationContext c, Vector3 desired, int ignore, out Vector3 position)
    {
        foreach (var origin in new[] { desired }.Concat(c.Physics.World.Definition.Spawns))
            foreach (float distance in new[] { 0f, 1f, 2f, 4f, 8f })
                foreach (var direction in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ, Vector3.UnitY })
                {
                    var p = origin + distance * direction;
                    if (c.Physics.CanPlacePlayer(p, ignore))
                    {
                        if (origin != desired)
                            Console.Error.WriteLine("Travel recovery used the source spawn region.");
                        position = p;
                        return true;
                    }
                }
        position = desired;
        return false;
    }
}
