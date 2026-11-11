using System.Numerics;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Shared;
using StargateGame.Building;

namespace StargateGame.Client;

sealed partial class GameApp
{
    int smokeDialKey, smokeTravelStep;
    Guid smokeTravelSource;
    bool smokeTravelComplete;
    double smokeDialNext, smokeDialOpened;
    bool smokeDialClosed, smokePreloadRelocated;
    readonly HashSet<string> dialCaptures = [];
    void SmokeDial(double now, ref float strafe, ref float forward)
    {
        if (!args.Contains("--dial-check") || session?.Gate is not { } gate)
            return;
        if (args.Contains("--travel-check") && (gate.Phase == GatePhase.Open || smokeTravelSource != Guid.Empty))
        {
            if (smokeTravelSource == Guid.Empty)
                smokeTravelSource = session.World.Definition.LocationId;
            if (session.World.Definition.LocationId != smokeTravelSource)
            {
                if (!smokeTravelComplete)
                    Console.WriteLine("TRAVEL VISUAL arrival=" + session.World.Definition.LocationId);
                smokeTravelComplete = true;
                strafe = forward = 0;
                return;
            }
            var frame = GateFrame.From(session.World.Definition.Entities.First(e => e.Model == "stargate"));
            var point = frame.Centre + frame.Normal * (smokeTravelStep == 0 ? 3 : -3);
            var motion = point - session.Local.Position;
            if (smokeTravelStep == 0 && new Vector2(motion.X, motion.Z).Length() < .2f)
                smokeTravelStep = 1;
            yaw = 0;
            pitch = 0;
            strafe = Math.Clamp(motion.X * 2, -1, 1);
            forward = Math.Clamp(-motion.Z * 2, -1, 1);
            return;
        }
        if (args.Contains("--preload-relocate"))
        {
            if (smokePreloadRelocated)
                return;
            if (smokeDialClosed && gate.Phase == GatePhase.Idle && session.Destination is { Stage: PreloadStage.Ready } cache)
            {
                Console.WriteLine($"PRELOAD READY tiles={cache.Store.Count} cpu_MiB={cache.Bytes / 1048576d:F2} prepare_ms={cache.Milliseconds:F2} gpu_tiles={scene?.PreloadMeshes} gpu_MiB={scene?.PreloadGpuBytes / 1048576d:F2}");
                session.InspectDestination(Arguments.Get(args, "--dial-address", "28-26-5-36-11-29").Split('-').Select(int.Parse).ToArray());
                smokePreloadRelocated = true;
                return;
            }
        }
        if (gate.Phase == GatePhase.Idle && gate.Status.Length > 0 && smokeDialKey > 0)
        {
            smokeDialClosed = true;
            return;
        }
        var dhd = session.World.Definition.Entities.First(e => e.Model == "dhd");
        var delta = dhd.Position + new Vector3(0, 0, 1.1f) - session.Local.Position;
        yaw = 0;
        pitch = 0;
        strafe = Math.Clamp(delta.X * 2, -1, 1);
        forward = Math.Clamp(-delta.Z * 2, -1, 1);
        if (new Vector2(delta.X, delta.Z).Length() > .25f || session.DialPending || now < smokeDialNext)
            return;
        strafe = forward = 0;
        int[] sequence = Arguments.Get(args, "--dial-address", "28-26-5-36-11-29").Split('-').Select(int.Parse).Concat(new[] { 1, 0 }).ToArray();
        int key;
        if (gate.Phase is GatePhase.Idle or GatePhase.Dialing or GatePhase.Failed && !smokeDialClosed)
        {
            key = sequence[Math.Min(gate.Symbols.Length, sequence.Length - 1)];
            smokeDialKey++;
        }
        else if (gate.Phase == GatePhase.Open)
        {
            if (smokeDialOpened == 0)
                smokeDialOpened = now;
            if (now - smokeDialOpened < 5 || smokeDialClosed)
                return;
            key = 0;
            smokeDialClosed = true;
        }
        else
            return;
        var eye = Movement.EyePosition(session.Local);
        foreach (var face in DhdInteraction.Surfaces.Where(f => f.Key == key).OrderByDescending(f => Math.Abs(Vector3.Cross(f.B - f.A, f.C - f.A).Y)))
        {
            var aim = Vector3.Normalize((face.A + face.B + face.C) / 3 * dhd.Scale + dhd.Position - eye);
            if (DhdInteraction.Pick(dhd, eye, aim)?.Key != key)
                continue;
            session.Dial("press", key, aim);
            scene?.PredictDhdPress(key);
            smokeDialNext = now + .7;
            return;
        }
        throw new InvalidOperationException("Dial inspection could not pick key " + key);
    }
    int dhdHover = -1;
    double backspaceAt;
    bool backspaceReset;
    void DialingInput(InputManager input, Vector3 eye, Vector3 aim, BodyState[] renderedBodies)
    {
        dhdHover = -1;
        if (scene is not null)
            scene.DhdHover = -1;
        if (session is null || buildMode || paused)
        {
            backspaceAt = 0;
            return;
        }
        var target = session.World.Definition.Entities.Where(e => e.Model == "dhd").Select(e => (Entity: e, Hit: DhdInteraction.Pick(e, eye, aim))).Where(p => p.Hit is not null).OrderBy(p => p.Hit!.Value.Distance).FirstOrDefault();
        var dhd = target.Entity;
        if (dhd is not null && target.Hit is { } picked && !session.Physics.InteractionBlocked(eye, aim, picked.Distance - .015f, ignoreEntity: dhd.Id, ignorePlayer: session.Local.Id))
        {
            session.SelectDhd(dhd.Id);
            dhdHover = picked.Key;
        }
        if (scene is not null)
            scene.DhdHover = dhdHover;
        if (session.Gate is null)
            return;
        if (Smoke && args.Contains("--dhd-hint-check"))
            return;
        if (dhdHover < 0)
        {
            backspaceAt = 0;
            return;
        }
        if (input.WasMouseButtonPressed(MouseButton.Left))
        {
            session.Dial("press", dhdHover, aim);
            scene?.PredictDhdPress(dhdHover);
        }
        if (input.WasKeyPressed(Key.Backspace))
        {
            backspaceAt = timer.Elapsed.TotalSeconds;
            backspaceReset = false;
            session.Dial("undo", dhdHover, aim);
        }
        if (input.IsKeyDown(Key.Backspace) && backspaceAt > 0 && !backspaceReset && timer.Elapsed.TotalSeconds - backspaceAt >= 1)
        {
            session.Dial("reset", dhdHover, aim);
            backspaceReset = true;
        }
        if (!input.IsKeyDown(Key.Backspace))
            backspaceAt = 0;
    }
    readonly HashSet<int> inspectedHoverKeys = [];
    void CheckDhdHover()
    {
        if (session?.Gate is null)
            return;
        var dhd = session.World.Definition.Entities.First(e => e.Model == "dhd");
        int expected = DhdInteraction.Pick(dhd, camera.Position, camera.WorldForwardNormal)?.Key ?? -1;
        if (dhdHover != expected)
            throw new InvalidOperationException($"DHD hover disagrees with current rendered camera: {dhdHover} != {expected}");
        if (expected >= 0 && inspectedHoverKeys.Add(expected))
            Console.WriteLine($"DHD HOVER current-frame key={expected} distinct={inspectedHoverKeys.Count}");
    }
}
