using StargateGame.Shared;
using StargateGame.Building;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    sealed record DoorSave(LocationContext Location, Session? Session, BuildCommand Command, bool Previous, Task Save, BuildResult Result);
    readonly List<DoorSave> doorSaves = [];
    bool DoorSavePending => doorSaves.Any(d => d.Location == context);
    void BeginDoorMotion(Session? session, BuildCommand command, BuildEdit validated)
    {
        var door = validated.Piece!;
        bool previous = CurrentChanges.Pieces.Single(p => p.Id == door.Id).Open;
        physics.SetDoorTarget(door.Id, door.Open);
        runtime!.Changes = physics.Changes;
        var result = new BuildResult(command.RequestId, true, "Door target updated.", CurrentChanges.Revision);
        var save = repository?.SaveDoorAsync(runtime.Definition.LocationId, door.Id, door.Open, CurrentChanges.Revision, result) ?? Task.CompletedTask;
        doorSaves.Add(new(context, session, command, previous, save, result));
        PublishBuildSound(new(session, command, validated, BuildSoundPosition(command, validated)));
        PollDoorSaves();
    }
    void PollDoorSaves()
    {
        foreach (var pending in doorSaves.Where(d => d.Location == context && d.Save.IsCompleted).ToArray())
        {
            doorSaves.Remove(pending);
            var result = pending.Result;
            if (!pending.Save.IsCompletedSuccessfully)
            {
                physics.SetDoorTarget(pending.Command.Target, pending.Previous);
                runtime!.Changes = physics.Changes;
                result = result with { Success = false, Reason = "Door save failed: " + pending.Save.Exception?.GetBaseException().Message };
                Console.Error.WriteLine(result.Reason);
            }
            if (pending.Session is { } session) BuildReply(session, result);
        }
    }
}
