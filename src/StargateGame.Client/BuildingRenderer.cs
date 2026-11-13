using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Building;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    Vector3 settlementCamera;
    public void SettlementCamera(Vector3 position) => settlementCamera = position;
    readonly HashSet<string> settlementFar = [];
    WorldChanges buildingState = WorldChanges.Empty;
    bool buildingModelsPending;
    Mesh3D? buildingBaseMesh;
    long buildingBaseBytes;
    int buildingModelCount = -1;
    readonly Dictionary<ChunkCoordinate, (BuildingPiece[] Pieces, Mesh3D Mesh, AxisAlignedBoundingBox Bounds)> buildingMeshes = [];
    readonly HashSet<(string, ulong)> removedScenery = [];
    readonly List<(string Model, Matrix4x4 Transform, AxisAlignedBoundingBox Bounds)> buildingPrefabs = [];
    readonly Dictionary<int, BuildingPiece> prefabDoors = [];
    readonly List<(Guid Id, Mesh3D Mesh, AxisAlignedBoundingBox Bounds, Vector3 Pivot)> legacyDoors = [];
    readonly Dictionary<Guid, float> doorAngles = [];
    Mesh3D? buildPreview;
    ShaderProgram? previewShader;
    Vector3 previewColor;
    public void UpdateBuildings(WorldChanges changes)
    {
        if (ReferenceEquals(changes, buildingState) && (!buildingModelsPending || buildingModelCount == models.Count))
            return;
        // Hinge targets do not change the structural revision or any mesh.
        if (!buildingModelsPending && changes.LocationId == buildingState.LocationId && changes.Revision == buildingState.Revision &&
            ReferenceEquals(changes.Structures, buildingState.Structures) && ReferenceEquals(changes.Removed, buildingState.Removed) && changes.Pieces.Length == buildingState.Pieces.Length)
        {
            buildingState = changes;
            var pieces = changes.Pieces.ToDictionary(p => p.Id);
            foreach (var index in prefabDoors.Keys.ToArray()) prefabDoors[index] = pieces[prefabDoors[index].Id];
            return;
        }
        bool ready = true;
        var baseTriangles = BuildingBaseCourses.Mesh(changes);
        if (baseTriangles.Length > 0) ready &= RequestModel("kit-stone-foundation");
        foreach (var piece in changes.Pieces.Where(p => p.Kind == PieceKind.Prefab).Concat(BuildingCorners.Pieces(changes)))
            ready &= RequestModel(piece.Prefab);
        if (!ready && changes.LocationId == buildingState.LocationId)
        {
            buildingModelsPending = true;
            buildingModelCount = models.Count;
            return; // Keep the complete old visual revision while uploads finish.
        }
        if (changes.LocationId != buildingState.LocationId)
            ClearBuildings();
        buildingModelsPending = !ready;
        buildingModelCount = models.Count;
        buildingState = changes;
        var replacementBase = baseTriangles.Length > 0 ? BuildingMesh(baseTriangles, 2) : null;
        buildingBaseMesh?.Dispose();
        buildingBaseMesh = replacementBase;
        buildingBaseBytes = (long)baseTriangles.Length * 3 * 32;
        removedScenery.Clear();
        foreach (var r in changes.Removed)
            removedScenery.Add((r.Category, r.Id));
        buildingPrefabs.Clear();
        prefabDoors.Clear();
        foreach (var p in changes.Pieces.Where(p => p.Kind == PieceKind.Prefab).Concat(BuildingCorners.Pieces(changes)))
        {
            if (BuildingBaseCourses.ReplacesOwner(changes,p.Id)) continue;
            if (!models.ContainsKey(p.Prefab))
                continue;
            if (BuildingPrefabs.Get(p.Prefab).Group == "Generated" && BuildingPrefabs.Get(p.Prefab).Role != "furnishing" && !BuildingPrefabs.IsDoor(p) && !settlementFar.Contains(p.Prefab))
            {
                RequestModel(p.Prefab + "-far");
                settlementFar.Add(p.Prefab);
            }
            var s = changes.Structures.Single(s => s.Id == p.StructureId);
            var transform = BuildingPrefabs.Transform(s, p);
            var b = modelBounds[p.Prefab];
            var bounds = AxisAlignedBoundingBox.FromPoints(Enumerable.Range(0, 8).Select(i => Vector3.Transform(new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z), transform)));
            if (BuildingPrefabs.IsDoor(p)) prefabDoors[buildingPrefabs.Count] = p;
            buildingPrefabs.Add((p.Prefab, transform, bounds));
        }
        foreach (var d in legacyDoors) d.Mesh.Dispose();
        legacyDoors.Clear();
        foreach (var p in changes.Pieces.Where(p => p.Kind == PieceKind.Door))
        {
            var triangles = BuildingGeometry.Mesh(changes, p with { Open = false, DoorAngle = null });
            var structure = changes.Structures.Single(s => s.Id == p.StructureId);
            var pivot = Vector3.Transform(DoorGeometry.Pivot(p), DoorGeometry.Root(structure, p));
            legacyDoors.Add((p.Id, BuildingMesh(triangles), AxisAlignedBoundingBox.FromPoints(triangles.SelectMany(t => new[] { t.A, t.B, t.C })), pivot));
        }
        var groups = changes.Pieces.Where(p => p.Kind != PieceKind.Prefab && p.Kind != PieceKind.Door).GroupBy(p => BuildingGeometry.Chunks(BuildingGeometry.Bounds(BuildingGeometry.Mesh(changes, p))).Order().First()).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).ToArray());
        foreach (var c in buildingMeshes.Keys.Where(c => !groups.ContainsKey(c)).ToArray())
        {
            buildingMeshes[c].Mesh.Dispose();
            buildingMeshes.Remove(c);
        }
        foreach (var (c, pieces) in groups)
        {
            if (buildingMeshes.TryGetValue(c, out var old) && old.Pieces.SequenceEqual(pieces))
                continue;
            var triangles = pieces.SelectMany(p => BuildingGeometry.Mesh(changes, p)).ToArray();
            var replacement = BuildingMesh(triangles);
            old.Mesh?.Dispose();
            buildingMeshes[c] = (pieces, replacement, AxisAlignedBoundingBox.FromPoints(triangles.SelectMany(t => new[] { t.A, t.B, t.C })));
        }
        RebuildPrefabBuckets();
        shadows.InvalidateAll();
        InvalidateGateShadows();
        fogRoomRevision = -1;
    }
    public void UpdateDoorVisuals(DoorMotion[] motions)
    {
        bool changed = false;
        var present = motions.Select(m => m.Id).ToHashSet();
        foreach (var id in doorAngles.Keys.Where(id => !present.Contains(id)).ToArray())
            changed |= doorAngles.Remove(id);
        foreach (var motion in motions)
        {
            changed |= !doorAngles.TryGetValue(motion.Id, out float old) || old != motion.Angle;
            doorAngles[motion.Id] = motion.Angle;
        }
        foreach (var (index, p) in prefabDoors)
        {
            var s = buildingState.Structures.Single(s => s.Id == p.StructureId);
            var transform = BuildingPrefabs.Transform(s, p with { DoorAngle = doorAngles.GetValueOrDefault(p.Id, p.Open ? MathF.PI / 2 : 0) });
            var old = buildingPrefabs[index];
            if (old.Transform == transform) continue;
            var b = modelBounds[p.Prefab];
            var bounds = AxisAlignedBoundingBox.FromPoints(Enumerable.Range(0, 8).Select(i => Vector3.Transform(new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z), transform)));
            buildingPrefabs[index] = (p.Prefab, transform, bounds); changed = true;
        }
        if (changed) { RebuildPrefabBuckets(); shadows.InvalidateAll(); InvalidateGateShadows(); }
    }
    Mesh3D BuildingMesh(BuildTriangle[] triangles, float textureMetres = 2)
    {
        var vertices = triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        var normals = triangles.SelectMany(t => Enumerable.Repeat(Vector3.Normalize(Vector3.Cross(t.B - t.A, t.C - t.A)), 3)).ToArray();
        var uv = vertices.Select((v, i) =>
        {
            var normal = normals[i];
            if (Math.Abs(normal.Y) > .5f)
                return new Vector2(v.X, v.Z) / textureMetres;
            // Unit horizontal tangent avoids compressed textures on 30/60-degree walls.
            // Canonical sign gives coplanar overlaps exactly the same texture coordinates.
            var tangent = Vector3.Normalize(new Vector3(normal.Z, 0, -normal.X));
            if (tangent.X < -.001f || Math.Abs(tangent.X) <= .001f && tangent.Z < 0)
                tangent = -tangent;
            return new Vector2(Vector3.Dot(v, tangent), v.Y) / textureMetres;
        }).ToArray();
        var mesh = graphics.CreateMesh3D();
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(uv);
        return mesh;
    }
    public void Preview(BuildTriangle[]? triangles, bool valid)
    {
        buildPreview?.Dispose();
        buildPreview = null;
        if (triangles is null || triangles.Length == 0)
            return;
        buildPreview = BuildingMesh(triangles);
        previewColor = valid ? new(.12f, .85f, .45f) : new(.95f, .15f, .12f);
        previewShader ??= LoadProgram("world.vert", "building-preview.frag");
    }
    void DrawBuildings(RenderPass pass, ShaderProgram program, ViewFrustum frustum, bool depth)
    {
        program.SetUniform("uTerrain", 3);
        program.SetUniform("uTint", Vector3.One);
        program.SetUniform("uHasModelNormal", depth ? 0 : 1);
        program.SetUniform("uRockSnow", 0);
        program.SetUniform("uFoliageCutoff", 0f);
        using var normal = depth ? null : buildingNormal.Bind(5);
        foreach (var batch in buildingMeshes.Values)
            if (frustum.Intersects(batch.Bounds))
                Mesh(pass, batch.Mesh, depth ? white : buildingTexture, program);
        foreach (var d in legacyDoors)
        {
            var p = buildingState.Pieces.Single(p => p.Id == d.Id);
            float angle = doorAngles.GetValueOrDefault(d.Id, p.Open ? MathF.PI / 2 : 0);
            using var model = pass.PushModel(Matrix4x4.CreateTranslation(-d.Pivot) * Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateTranslation(d.Pivot));
            Mesh(pass, d.Mesh, depth ? white : buildingTexture, program);
        }
        program.SetUniform("uTerrain", 0);
        program.SetUniform("uTint", Vector3.One);
        program.SetUniform("uHasModelNormal", 0);
        if (buildingBaseMesh is not null && models.TryGetValue("kit-stone-foundation", out var masonry))
        {
            var stone = masonry[0];
            program.SetUniform("uModelHeightScale", depth ? 0 : stone.HeightScale);
            program.SetUniform("uHasModelNormal", !depth && stone.normal is not null ? 1 : 0);
            program.SetUniform("uHasMetallicRoughness", !depth && stone.MetallicRoughness is not null ? 1 : 0);
            program.SetUniform("uMetallicRoughness", 6);
            using var stoneNormal = depth ? null : stone.normal?.Bind(5);
            using var stoneRoughness = depth ? null : stone.MetallicRoughness?.Bind(6);
            Mesh(pass, buildingBaseMesh, depth ? white : stone.texture, program);
            program.SetUniform("uHasModelNormal", 0);
            program.SetUniform("uHasMetallicRoughness", 0);
        program.SetUniform("uModelHeightScale", 0f);
        }
        using var twoSided = pass.PushState(pass.State with
        {
            CullMode = CullMode.None
        });
        foreach (var piece in buildingState.Pieces.Where(p => p.Kind == PieceKind.Prefab && !models.ContainsKey(p.Prefab)))
        {
            var structure = buildingState.Structures.FirstOrDefault(s => s.Id == piece.StructureId);
            if (structure is not null) DrawModelProxy(pass, program, piece.Prefab, BuildingPrefabs.Transform(structure, piece));
        }
        foreach (var list in visiblePrefabs.Values)
            list.Clear();
        foreach (var bucket in prefabBuckets.Values)
        {
            if (!frustum.Intersects(bucket.Bounds))
                continue;
            foreach (var index in bucket.Indices)
            {
                var prefab = buildingPrefabs[index];
                if (!frustum.Intersects(prefab.Bounds))
                    continue;
                if (BuildingPrefabs.Get(prefab.Model).Role == "furnishing" && Vector3.DistanceSquared(prefab.Transform.Translation, settlementCamera) > 100 * 100)
                    continue;
                var model = settlementFar.Contains(prefab.Model) && models.ContainsKey(prefab.Model + "-far") && Vector3.DistanceSquared(prefab.Transform.Translation, settlementCamera) > 180 * 180 ? prefab.Model + "-far" : prefab.Model;
                if (!visiblePrefabs.TryGetValue(model, out var list))
                    visiblePrefabs[model] = list = [];
                list.Add(prefab.Transform);
            }
        }
        foreach (var (name, instances) in visiblePrefabs)
        {
            if (instances.Count == 0)
                continue;
            foreach (var part in models[name])
            {
                program.SetUniform("uFoliageCutoff", part.AlphaCutoff);
                program.SetUniform("uHasModelNormal", !depth && part.normal is not null ? 1 : 0);
                program.SetUniform("uHasMetallicRoughness", !depth && part.MetallicRoughness is not null ? 1 : 0);
                program.SetUniform("uMetallicRoughness", 6);
                program.SetUniform("uModelHeightScale", depth ? 0 : part.HeightScale);
                using var n = depth ? null : part.normal?.Bind(5);
                using var mr = depth ? null : part.MetallicRoughness?.Bind(6);
                program.SetUniform("uTreeMode", 0);
                program.SetUniform("uTreeVertices", -1);
                for (int offset = 0; offset < instances.Count; offset += TreeBatchSize)
                {
                    int count = Math.Min(TreeBatchSize, instances.Count - offset);
                    instances.CopyTo(offset, treeTransforms, 0, count);
                    program.SetUniform("uTreeTransforms", treeTransforms.AsSpan(0, count));
                    pass.DrawMeshInstances(part.mesh, count, depth && part.AlphaCutoff == 0 ? white : part.texture, program);
                    DrawCalls++;
                }
                program.SetUniform("uTreeVertices", 0);
            }
        }
        program.SetUniform("uHasModelNormal", 0);
        program.SetUniform("uHasMetallicRoughness", 0);
        program.SetUniform("uModelHeightScale", 0f);
        program.SetUniform("uFoliageCutoff", 0f);
    }
    void DrawBuildPreview(RenderPass pass)
    {
        if (buildPreview is null || previewShader is null)
            return;
        using var state = pass.PushState(pass.State with
        {
            BlendEnabled = true,
            DepthWriteEnabled = false,
            CullMode = CullMode.None
        });
        previewShader.SetUniform("uTint", previewColor);
        Mesh(pass, buildPreview, white, previewShader);
    }
    void ClearBuildings()
    {
        foreach (var d in legacyDoors) d.Mesh.Dispose();
        legacyDoors.Clear(); prefabDoors.Clear(); doorAngles.Clear();
        ClearDemolition();
        foreach (var b in buildingMeshes.Values)
            b.Mesh.Dispose();
        buildingMeshes.Clear();
        buildingBaseMesh?.Dispose();
        buildingBaseMesh = null;
        buildingBaseBytes = 0;
        buildingPrefabs.Clear();
        prefabBuckets.Clear();
        removedScenery.Clear();
        buildingState = WorldChanges.Empty;
        buildPreview?.Dispose();
        buildPreview = null;
    }
}
