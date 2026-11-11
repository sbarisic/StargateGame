using System.Numerics;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Building;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Client;

sealed partial class GameApp
{
    bool buildMode;
    int buildSelection = 1, buildRotation;
    float buildElevation;
    double nextBuildPreview, demolitionStarted;
    string buildHint = "";
    BuildCommand? previewCommand;
    BuildEdit? previewEdit;
    Guid heldTarget;
    ulong heldScenery;
    Vector3 Look => new(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
    string SelectedBuild => BuildName(buildSelection);
    void BuildingInput(InputManager input)
    {
        if (session is null)
            return;
        if (paused)
            return;
        if (paletteMouseGuard)
        {
            if (input.IsMouseButtonDown(MouseButton.Left))
                return;
            paletteMouseGuard = false;
        }
        if (input.WasKeyPressed(Key.R))
        {
            buildRotation = (buildRotation + 1) % 12;
            nextBuildPreview = 0;
        }
        if (input.WasKeyPressed(Key.PageUp))
        {
            buildElevation += .25f;
            nextBuildPreview = 0;
        }
        if (input.WasKeyPressed(Key.PageDown))
        {
            buildElevation -= .25f;
            nextBuildPreview = 0;
        }
        if (!buildMode)
            return;
        if (timer.Elapsed.TotalSeconds >= nextBuildPreview || input.WasMouseButtonPressed(MouseButton.Left))
        {
            UpdateBuildPreview();
            nextBuildPreview = timer.Elapsed.TotalSeconds + .1;
        }
        if (!buildMode || previewCommand is null)
            return;
        if (buildSelection < BuildingPrefabs.Palette.Count)
        {
            if (input.WasMouseButtonPressed(MouseButton.Left) && previewEdit?.Valid == true)
                session.Build(previewCommand with
                {
                    RequestId = Guid.NewGuid()
                });
        }
        else if (input.IsMouseButtonDown(MouseButton.Left))
        {
            if (demolitionStarted == 0 || heldTarget != previewCommand.Target || heldScenery != previewCommand.SceneryId)
            {
                demolitionStarted = timer.Elapsed.TotalSeconds;
                heldTarget = previewCommand.Target;
                heldScenery = previewCommand.SceneryId;
            }
            if (timer.Elapsed.TotalSeconds - demolitionStarted >= 1 && !session.BuildPending)
            {
                session.Build(previewCommand with
                {
                    RequestId = Guid.NewGuid()
                });
                demolitionStarted = 0;
            }
        }
        else
            demolitionStarted = 0;
    }
    (BuildingPiece? Piece, Vector3 Point) PickPiece()
    {
        var eye = Movement.EyePosition(session!.Local);
        float best = 8;
        BuildingPiece? found = null;
        foreach (var p in session.Changes.Pieces)
        {
            var mesh = BuildingGeometry.Mesh(session.Changes, p);
            if (BuildingGeometry.Ray(eye, Look, mesh, best) is { } distance)
            {
                best = distance;
                found = p;
            }
        }
        return (found, eye + Look * best);
    }
    void UpdateBuildPreview()
    {
        previewCommand = null;
        previewEdit = null;
        scene?.Preview(null, false);
        if (session is null || !buildMode)
            return;
        var state = session.Changes;
        var hit = PickPiece();
        var eye = Movement.EyePosition(session.Local);
        bool Dry(float x, float z)
        {
            if (session.Terrain is null || !session.Terrain.TryGetChunk(ChunkCoordinate.At(x, z), out var c))
                return false;
            return float.IsNaN(c.WaterHeight(Math.Clamp((int)MathF.Round(x - c.Coordinate.X * 64), 0, 64), Math.Clamp((int)MathF.Round(z - c.Coordinate.Z * 64), 0, 64)));
        }
        if (buildSelection == BuildingPrefabs.Palette.Count + 1)
        {
            float best = 8;
            BuildTriangle[]? selected = null;
            if (session.Terrain is not null)
                foreach (var c in ChunkCoordinate.At(eye.X, eye.Z).Around(1))
                    if (session.Terrain.TryGetChunk(c, out var data))
                    {
                        var candidates = TreeScenery.Resolve(data, session.World.Definition.Preset, session.World.Definition.Fixture).Select(t => ("tree", t.Id, t.Min, t.Max)).Concat(RockScenery.Resolve(data, session.World.Definition.Preset).Select(t => ("rock", t.Id, t.Min, t.Max)));
                        foreach (var (category, id, min, max) in candidates)
                        {
                            if (state.IsRemoved(category, id) || Vector3.DistanceSquared(eye, Vector3.Clamp(eye, min, max)) > 64)
                                continue;
                            var mesh = session.Physics.SceneryMesh(category, id, c).ToArray();
                            if (BuildingGeometry.Ray(eye, Look, mesh, best) is { } d)
                            {
                                best = d;
                                selected = mesh;
                                previewCommand = new(Guid.NewGuid(), state.Revision, "clear", Category: category, SceneryId: id, Chunk: c);
                            }
                        }
                    }
            scene?.Preview(selected, true);
            buildHint = selected is null ? "Aim at a tree trunk or rock within 8 m." : "Hold left mouse for one second to clear.";
            return;
        }
        if (buildSelection == BuildingPrefabs.Palette.Count)
        {
            if (hit.Piece is null)
            {
                buildHint = "Aim at a building piece.";
                return;
            }
            previewCommand = new(Guid.NewGuid(), state.Revision, "remove", Target: hit.Piece.Id);
            previewEdit = BuildingRules.Edit(state, previewCommand, session.World.SampleHeight, Dry);
            var ids = previewEdit.Valid ? [hit.Piece.Id] : previewEdit.Dependents;
            scene?.Preview(state.Pieces.Where(p => ids.Contains(p.Id)).SelectMany(p => BuildingGeometry.Mesh(state, p)).ToArray(), previewEdit.Valid);
            buildHint = previewEdit.Valid ? "Hold left mouse for one second to demolish." : previewEdit.Error;
            return;
        }
        UpdatePrefabPreview(hit, eye, Dry);
        return;
    }
    void UpdatePrefabPreview((BuildingPiece? Piece, Vector3 Point) hit, Vector3 eye, Func<float, float, bool> dry)
    {
        var state = session!.Changes;
        var prefab = BuildingPrefabs.Palette[buildSelection];
        Vector3 anchor = eye + Look * 5;
        int socket = 0;
        if (hit.Piece is { } target)
        {
            var structure = state.Structures.Single(s => s.Id == target.StructureId);
            if (target.Kind == PieceKind.Prefab)
            {
                var sockets = BuildingPrefabs.Sockets(target, prefab);
                if (sockets.Length > 0)
                {
                    var ranked = Enumerable.Range(0, sockets.Length).OrderBy(i => Vector3.DistanceSquared(Vector3.Transform(BuildingPrefabs.Attach(target, sockets[i]).Offset, BuildingGeometry.Transform(structure)), hit.Point)).ToArray();
                    socket = ranked[buildRotation % ranked.Length];
                }
            }
        }
        else
        {
            for (float distance = .5f; distance <= 8; distance += .1f)
            {
                var sample = eye + Look * distance;
                if (sample.Y <= session.World.SampleHeight(sample.X, sample.Z))
                {
                    anchor = sample;
                    break;
                }
            }
            anchor.X = MathF.Round(anchor.X * 4) / 4;
            anchor.Z = MathF.Round(anchor.Z * 4) / 4;
            anchor.Y = MathF.Ceiling((session.World.SampleHeight(anchor.X, anchor.Z) + (prefab.Role == "foundation" ? .25f : 0)) * 4) / 4 + buildElevation;
        }
        previewCommand = new(Guid.NewGuid(), state.Revision, "place", PieceKind.Prefab, hit.Piece?.Id ?? Guid.Empty, socket, anchor, buildRotation, Prefab: prefab.Id);
        previewEdit = BuildingRules.Edit(state, previewCommand, session.World.SampleHeight, dry);
        buildHint = previewEdit.Error.Length > 0 ? previewEdit.Error : "Left click to place. Server validates clearance.";
        if (previewEdit.Piece is { } piece)
        {
            var mesh = BuildingGeometry.Mesh(previewEdit.State, piece).Concat(previewEdit.State.Pieces.Where(p => p.Parent == piece.Id && BuildingPrefabs.IsAssemblyChild(previewEdit.State, p)).SelectMany(p => BuildingGeometry.Mesh(previewEdit.State, p))).ToArray();
            bool valid = previewEdit.Valid;
            scene?.Preview(mesh, valid);
        }
    }
}
