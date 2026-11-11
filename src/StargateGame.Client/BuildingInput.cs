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
    bool demolitionNeedsRelease;
    WorldChanges? demolitionState;
    Guid demolitionPreviewTarget, demolitionOwner;
    Guid[] demolitionHighlightIds=[];
    string demolitionName = "";
    string BuildAction => buildSelection < BuildingPrefabs.Palette.Count ? "place" : buildSelection == BuildingPrefabs.Palette.Count ? "remove" : "clear";
    double DemolitionProgress => demolitionStarted == 0 ? 0 : Math.Clamp(timer.Elapsed.TotalSeconds-demolitionStarted,0,1);
    void CancelDemolition(bool requireRelease = true)
    {
        demolitionStarted=0; heldTarget=Guid.Empty; heldScenery=0;
        if(requireRelease)demolitionNeedsRelease=true;
    }
    void BuildingInput(InputManager input)
    {
        if(cableTool){CancelDemolition();return;}
        bool down=input.IsMouseButtonDown(MouseButton.Left);
        if(!down) { CancelDemolition(false); demolitionNeedsRelease=false; }
        if(session is null || paused || buildPaletteOpen || !buildMode)
        { CancelDemolition(); scene?.Demolition(null,[],false); return; }
        if(paletteMouseGuard)
        {
            if(down) { CancelDemolition(); return; }
            paletteMouseGuard=false;
        }
        if(input.WasKeyPressed(Key.R)) {buildRotation=(buildRotation+1)%12;nextBuildPreview=0;}
        if(input.WasKeyPressed(Key.PageUp)) {buildElevation+=.25f;nextBuildPreview=0;}
        if(input.WasKeyPressed(Key.PageDown)) {buildElevation-=.25f;nextBuildPreview=0;}
        if(buildSelection==BuildingPrefabs.Palette.Count || timer.Elapsed.TotalSeconds>=nextBuildPreview || input.WasMouseButtonPressed(MouseButton.Left))
        { UpdateBuildPreview(); nextBuildPreview=timer.Elapsed.TotalSeconds+.1; }
        if(previewCommand is null || previewEdit?.Valid!=true)
        { if(down)CancelDemolition(); return; }
        if(buildSelection<BuildingPrefabs.Palette.Count)
        {
            CancelDemolition();
            if(input.WasMouseButtonPressed(MouseButton.Left)) session.Build(previewCommand with{RequestId=Guid.NewGuid()});
            return;
        }
        if(!down||demolitionNeedsRelease||session.BuildPending)return;
        if(demolitionStarted!=0 && (heldTarget!=(BuildAction=="remove"?demolitionOwner:previewCommand.Target)||heldScenery!=previewCommand.SceneryId))
        {CancelDemolition();return;}
        if(demolitionStarted==0)
        {demolitionStarted=timer.Elapsed.TotalSeconds;heldTarget=BuildAction=="remove"?demolitionOwner:previewCommand.Target;heldScenery=previewCommand.SceneryId;}
        if(DemolitionProgress>=1)
        {
            session.Build(previewCommand with{RequestId=Guid.NewGuid()});
            CancelDemolition(); // Exactly one request per press, including rejections.
        }
    }
    long targetingFrame, pickedFrame=-1;
    WorldChanges? pickedState;
    Vector3 pickedEye, pickedAim;
    (BuildingPiece? Piece,Vector3 Point) pickedResult;
    (BuildingPiece? Piece, Vector3 Point) PickPiece()
    {
        var eye = camera.Position;
        var aim = camera.WorldForwardNormal;
        if(pickedFrame==targetingFrame && ReferenceEquals(pickedState,session!.Changes) && pickedEye==eye && pickedAim==aim)return pickedResult;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var query = session!.Physics.BuildingQuery;
        var hit = query.Ray(eye, aim);
        if (hit.Piece is not null && session.Physics.InteractionBlocked(eye, aim,
            Vector3.Distance(eye, hit.Point) - .015f, ignorePlayer: session.Local.Id, ignoreBuildings: true))
            hit = (null, hit.Point);
        queryCount++; queryCandidates += query.Candidates; queryTriangles += query.Triangles;
        queryMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (timer.Elapsed.TotalSeconds >= nextQueryReport)
        {
            Console.WriteLine($"BUILD query count={queryCount} avgMs={queryMilliseconds/Math.Max(queryCount,1):F3} candidates={queryCandidates/Math.Max(queryCount,1)} triangles={queryTriangles/Math.Max(queryCount,1)}");
            nextQueryReport = timer.Elapsed.TotalSeconds + 15;
            queryCount = queryCandidates = queryTriangles = 0; queryMilliseconds = 0;
        }
        pickedFrame=targetingFrame;pickedState=session.Changes;pickedEye=eye;pickedAim=aim;pickedResult=hit;
        return hit;
    }
    double nextQueryReport = 15, queryMilliseconds;
    int queryCount, queryCandidates, queryTriangles;

    void UpdateBuildPreview()
    {
        previewCommand = null;
        var previousEdit = previewEdit;
        previewEdit = null;
        scene?.Preview(null, false);
        if (session is null || !buildMode)
            return;
        var state = session.Changes;
        var hit = PickPiece();
        var eye = camera.Position;
        bool Dry(float x, float z)
        {
            if (session.Terrain is null || !session.Terrain.TryGetChunk(ChunkCoordinate.At(x, z), out var c))
                return false;
            return float.IsNaN(c.WaterHeight(Math.Clamp((int)MathF.Round(x - c.Coordinate.X * 64), 0, 64), Math.Clamp((int)MathF.Round(z - c.Coordinate.Z * 64), 0, 64)));
        }
        if (buildSelection != BuildingPrefabs.Palette.Count) { scene?.Demolition(null, [], false); demolitionState=null; }
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
                            if (BuildingGeometry.Ray(eye, camera.WorldForwardNormal, mesh, best) is { } d)
                            {
                                best = d;
                                selected = mesh;
                                previewCommand = new(Guid.NewGuid(), state.Revision, "clear", Category: category, SceneryId: id, Chunk: c);
                            }
                        }
                    }
            if(previewCommand is not null) previewEdit=BuildingRules.Edit(state,previewCommand,session.World.SampleHeight,Dry);
            demolitionName=previewCommand?.Category ?? "";
            scene?.Preview(selected, previewEdit?.Valid==true);
            buildHint = selected is null ? "Aim at a tree trunk or rock within 8 m." : "Hold left mouse for one second to clear.";
            return;
        }
        if (buildSelection == BuildingPrefabs.Palette.Count)
        {
            if (hit.Piece is null)
            {
                scene?.Demolition(null, [], false);
                demolitionState=null;
                buildHint = "Aim at a building piece.";
                return;
            }
            previewCommand = new(Guid.NewGuid(), state.Revision, "remove", Target: hit.Piece.Id, HitPoint: hit.Point);
            if(!ReferenceEquals(state,demolitionState)||demolitionPreviewTarget!=hit.Piece.Id||previousEdit is null)
            {
                previewEdit=BuildingRules.Edit(state,previewCommand,session.World.SampleHeight,Dry);
                var owner=BuildingPrefabs.IsAssemblyChild(state,hit.Piece) ? state.Pieces.Single(p=>p.Id==hit.Piece.Parent) : hit.Piece;
                demolitionOwner=owner.Id;
                demolitionHighlightIds=state.Pieces.Where(p=>p.Id==owner.Id||p.Parent==owner.Id&&BuildingPrefabs.IsAssemblyChild(state,p)).Select(p=>p.Id).ToArray();
                demolitionName=owner.Kind==PieceKind.Prefab?BuildingPrefabs.Get(owner.Prefab).Title:owner.Kind.ToString();
                demolitionState=state;demolitionPreviewTarget=hit.Piece.Id;
            }
            else previewEdit=previousEdit;
            scene?.Demolition(state,demolitionHighlightIds,previewEdit.Valid);
            buildHint=previewEdit.Valid ? $"Hold left mouse for one second: {demolitionName}" : previewEdit.Error;
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
