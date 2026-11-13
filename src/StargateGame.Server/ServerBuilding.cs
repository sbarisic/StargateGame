using System.Numerics;
using StargateGame.Building;
using StargateGame.Persistence;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Server;

public sealed partial class GameServer
{
    readonly WorldRepository? repository;
    sealed record PendingEdit(Session? Session, BuildCommand Command, BuildEdit Edit, Vector3 SoundPosition, Guid[]? DestroyedProps = null);
    PendingEdit? edit
    {
        get => context.Edit;
        set => context.Edit = value;
    }
    Task? savingEdit
    {
        get => context.SavingEdit;
        set => context.SavingEdit = value;
    }
    Task? crateSave
    {
        get => context.CrateSave;
        set => context.CrateSave = value;
    }
    Task? switchSave
    {
        get => context.SwitchSave;
        set => context.SwitchSave = value;
    }
    bool freezeTick
    {
        get => context.FreezeTick;
        set => context.FreezeTick = value;
    }
    public WorldChanges CurrentChanges => runtime?.Changes ?? WorldChanges.Empty;
    bool Dry(float x, float z)
    {
        if (runtime is null || !runtime.Generation.Store.TryGetChunk(ChunkCoordinate.At(x, z), out var c))
            return false;
        return !WaterSurfaceData.TrySample(runtime.Generation.Store, x, z, out _);
    }
    bool HandleBuild(Session s, Message message)
    {
        if (message.Build is not { } cmd)
        {
            Reject(s, "Missing build command.");
            return true;
        }
        if (s.BuildResults.TryGetValue(cmd.RequestId, out var cached))
        {
            s.Peer.Send(new()
            {
                Kind = "build-result",
                BuildResult = cached
            });
            return true;
        }
        if (edit?.Command.RequestId == cmd.RequestId && edit.Session == s)
            return true;
        if (doorSaves.Any(d => d.Session == s && d.Command.RequestId == cmd.RequestId)) return true;
        if (repository is not null && s.CheckedRequest != cmd.RequestId)
        {
            if (s.LookupReceipt is not null)
            {
                s.Peer.Send(new()
                {
                    Kind = "build-result",
                    BuildResult = new(cmd.RequestId, false, "Checking a previous request.", CurrentChanges.Revision)
                });
                return true;
            }
            s.LookupCommand = cmd;
            s.LookupReceipt = repository.FindReceiptAsync(cmd.RequestId);
            return true;
        }
        s.CheckedRequest = Guid.Empty;
        void Fail(string reason, Guid[]? ids = null) => BuildReply(s, new(cmd.RequestId, false, reason, CurrentChanges.Revision, ids));
        if (Environment.TickCount64 - s.LastBuild < 200)
        {
            Fail("Build requests are limited to five per second.");
            return true;
        }
        s.LastBuild = Environment.TickCount64;
        if (cmd.RequestId == Guid.Empty)
        {
            Fail("Invalid request ID.");
            return true;
        }
        if (runtime is null || repository is null && runtime.Definition.Fixture != "gate-pair")
        {
            Fail("Building requires a persistent generated planet.");
            return true;
        }
        if (DoorSavePending || edit is not null || transition is not null || preparing is not null || HasLocationTransfer || sessions.Any(p => p.JoinName is not null))
        {
            Fail("World update in progress; try again.");
            return true;
        }
        if (cmd.Action == "spawn-prop")
        {
            SpawnSandboxProp(s,cmd);
            return true;
        }
        BuildEdit result;
        if (cmd.Action == "clear")
        {
            if (cmd.Revision != CurrentChanges.Revision || cmd.Category is not ("tree" or "rock") || !cmd.Chunk.Valid || CurrentChanges.Removed.Length >= 65536)
            {
                Fail("Invalid scenery removal.");
                return true;
            }
            var mesh = physics.SceneryMesh(cmd.Category, cmd.SceneryId, cmd.Chunk).ToArray();
            if (mesh.Length == 0)
            {
                Fail("Scenery no longer exists.");
                return true;
            }
            result = new(CurrentChanges with
            {
                Revision = CurrentChanges.Revision + 1,
                Removed = CurrentChanges.Removed.Append(new(cmd.Category, cmd.SceneryId, cmd.Chunk)).ToArray()
            }, null, "", []);
        }
        else
            result = BuildingRules.Edit(CurrentChanges, cmd, physics.World.SampleHeight, Dry);
        if (!result.Valid)
        {
            Fail(result.Error, result.Dependents);
            return true;
        }
        string error = ValidateEdit(s, cmd, result);
        if (error.Length > 0)
        {
            Fail(error);
            return true;
        }
        if (cmd.Action == "door")
        {
            BeginDoorMotion(s, cmd, result);
            return true;
        }
        edit = new(s, cmd, result, BuildSoundPosition(cmd, result));
        var coordinates = PhysicsWorld.IncludeBuildingOwners(result.State, physics.TerrainCoordinates);
        preparing = PrepareCollision(coordinates);
        s.Peer.Send(new()
        {
            Kind = "build-pending",
            Reason = "Preparing world change..."
        });
        return true;
    }
    string ValidateEdit(Session session, BuildCommand cmd, BuildEdit proposed)
    {
        if (runtime is null)
            return "World is not ready.";
        if (session.PlayerId == 0 || session.Peer.Closed)
            return "Builder disconnected.";
        BuildTriangle[] mesh;
        if (cmd.Action == "clear")
            mesh = physics.SceneryMesh(cmd.Category, cmd.SceneryId, cmd.Chunk).ToArray();
        else
        {
            var state = cmd.Action is "remove" or "door" ? physics.PosedChanges : proposed.State;
            var p = cmd.Action is "door" or "remove" ? state.Pieces.Single(p => p.Id == cmd.Target) : proposed.Piece!;
            mesh = BuildingGeometry.Mesh(state, p);
        }
        if (mesh.Length == 0)
            return "Target no longer exists.";
        var b = BuildingGeometry.Bounds(mesh);
        var eye = Movement.EyePosition(physics.Player(session.PlayerId));
        var target = Vector3.Clamp(eye, b.Min, b.Max);
        if (cmd.Action is "door" or "remove")
        {
            if (cmd.HitPoint is not { } hitPoint || !float.IsFinite(hitPoint.X) || !float.IsFinite(hitPoint.Y) || !float.IsFinite(hitPoint.Z))
                return cmd.Action == "door" ? "Aim at the door." : "Aim at the piece to demolish.";
            target = hitPoint;
            float reach = Vector3.Distance(eye,target);
            if (reach > 8) return "Out of reach.";
            if (reach < .001f || BuildingGeometry.Ray(eye,Vector3.Normalize(target-eye),mesh,reach+.1f) is not { } surface || Math.Abs(surface-reach)>.1f)
                return cmd.Action == "door" ? "Aim at the door." : "Aim at the piece to demolish.";
        }
        float distance = Vector3.Distance(eye, target);
        if (distance > 8)
            return "Target is farther than 8 m.";
        if (distance > .1f)
        {
            bool attachment = cmd.Action == "place" && (cmd.Kind == PieceKind.Door || cmd.Kind == PieceKind.Prefab);
            bool blocked = physics.BuildingVisibilityBlocked(eye,Vector3.Normalize(target-eye),distance-.03f,session.PlayerId,
                attachment || cmd.Action is "door" or "remove" ? cmd.Target : Guid.Empty);
            if (blocked)
                return "Target is obstructed.";
        }
        if (b.Min.X < -physics.World.Definition.HalfSize || b.Min.Z < -physics.World.Definition.HalfSize || b.Max.X > physics.World.Definition.HalfSize || b.Max.Z > physics.World.Definition.HalfSize || b.Max.Y > 2048)
            return "Outside world bounds.";
        if (cmd.Action is "remove" or "clear")
            return "";
        if (cmd.Action == "door")
            return ValidateDoorSweep(proposed.Piece!);
        foreach (var p in physics.Players)
            if (b.Intersects(new(p.Position - new Vector3(.32f, 0, .32f), p.Position + new Vector3(.32f, 1.8f, .32f))))
                return "A player occupies this space.";
        foreach (var prop in physics.Bodies())
            if (b.Intersects(new(prop.Position - new Vector3(.87f), prop.Position + new Vector3(.87f))))
                return "A crate occupies this space.";
        foreach (var c in BuildingGeometry.Chunks(b))
        {
            if (!runtime.Generation.Store.TryGetChunk(c, out var data))
                return "Nearby terrain is still loading.";
            foreach (var r in RockScenery.Resolve(data, runtime.Definition.Preset))
                if (!CurrentChanges.IsRemoved("rock", r.Id) && b.Intersects(new(r.Min, r.Max)))
                    return "Clear the rock first.";
            foreach (var t in TreeScenery.Resolve(data, runtime.Definition.Preset, runtime.Definition.Fixture))
                if (!CurrentChanges.IsRemoved("tree", t.Id) && b.Intersects(new(t.Min, t.Max)))
                    return "Clear the tree first.";
        }
        return "";
    }
    void PollBuildLookups()
    {
        foreach (var s in sessions.Where(s => s.LookupReceipt?.IsCompleted == true).ToArray())
        {
            var lookup = s.LookupReceipt!;
            var command = s.LookupCommand!;
            s.LookupReceipt = null;
            s.LookupCommand = null;
            if (s.PlayerId == 0 || s.Peer.Closed)
                continue;
            if (lookup.IsFaulted)
            {
                BuildReply(s, new(command.RequestId, false, "Unable to read save: " + lookup.Exception?.GetBaseException().Message, CurrentChanges.Revision));
                continue;
            }
            if (lookup.Result is { } receipt)
            {
                BuildReply(s, receipt);
                continue;
            }
            s.CheckedRequest = command.RequestId;
            HandleBuild(s, new()
            {
                Build = command
            });
        }
    }
    void BuildReply(Session s, BuildResult result)
    {
        s.BuildResults[result.RequestId] = result;
        if (s.BuildResults.Count > 256)
            s.BuildResults.Remove(s.BuildResults.Keys.First());
        s.Peer.Send(new()
        {
            Kind = "build-result",
            BuildResult = result
        });
    }
    void CancelEdit(string reason)
    {
        if (edit is not { } pending)
            return;
        if (transition is { } t)
        {
            lastCancelledTransfer = t.Id;
            Broadcast(new()
            {
                Kind = "terrain-cancel",
                TransferId = t.Id,
                Reason = reason
            });
        }
        physics.CancelChanges();
        if (pending.Session is { } builder)
            BuildReply(builder, new(pending.Command.RequestId, false, reason, CurrentChanges.Revision));
        else
            Console.Error.WriteLine("KAWOOSH change retry: " + reason);
        edit = null;
        savingEdit = null;
        transition = null;
        freezeTick = false;
    }
    bool CommitEditReady()
    {
        if (edit is null)
            return true;
        if (savingEdit is null)
        {
            string error = edit.Session is { } builder ? ValidateEdit(builder, edit.Command, edit.Edit) : "";
            if (error.Length > 0)
            {
                CancelEdit(error);
                return false;
            }
            savingEdit = repository is null ? Task.CompletedTask : edit.Session is null ? repository!.SaveDestructionAsync(edit.Edit.State, CurrentChanges.Revision, edit.DestroyedProps ?? []) : repository!.SaveAsync(edit.Edit.State, CurrentChanges.Revision, new(edit.Command.RequestId, true, "World change saved.", edit.Edit.State.Revision));
        }
        if (!savingEdit.IsCompleted)
        {
            freezeTick = true;
            return false;
        }
        if (!savingEdit.IsCompletedSuccessfully)
        {
            CancelEdit("Saving failed: " + savingEdit.Exception?.GetBaseException().Message);
            return false;
        }
        runtime!.Changes = edit.Edit.State;
        if (edit.DestroyedProps is { } destroyed)
        {
            foreach (var prop in physics.World.Definition.Props.Where(p => destroyed.Contains(p.PersistentId)).ToArray())
                physics.RemoveProp(prop.Id);
            runtime.Definition = physics.World.Definition;
        }
        return true;
    }
    void CheckpointCrates()
    {
        if (homeRegeneration?.Save is not null || repository is null || runtime is null || TravelBusy(context))
            return;
        if (crateSave?.IsFaulted == true)
        {
            Console.Error.WriteLine("Crate save failed: " + crateSave.Exception?.GetBaseException().Message);
            crateSave = null;
        }
        if (physics.Tick % 300 == 0 && (crateSave is null || crateSave.IsCompleted))
            crateSave = repository.SaveCurrentBodiesAsync(runtime.Definition.LocationId, physics.Bodies(), physics.World.Definition.Props);
    }
}
