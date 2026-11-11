using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.Building;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    RenderTarget? farSunDepth;
    Matrix4x4 farSunMatrix;
    Vector3 farSunDirection;
    int farSunMeshes;
    long farSunRevision = -1;
    RenderTarget? cloudCurrent, cloudVolume, cloudHistory, cloudShadow, volumeDensity, volumeIntegrated;
    ShaderProgram? cloudTemporalShader, cloudVolumeShader, cloudShadowShader, volumeInjectShader, volumeIntegrateShader;
    int volumeWidth, volumeHeight;
    Vector2 previousCloudWind;
    Vector3 volumeGrid, previousCloudEye;
    Matrix4x4 volumeViewProjection;
    long volumeEpoch;
    float lastVolumeTime = float.NaN;
    bool cloudValid;
    public bool VolumeHistoryValid => cloudValid;
    public long VolumeBytes => new[] { cloudCurrent, cloudVolume, cloudHistory, cloudShadow, volumeDensity, volumeIntegrated, farSunDepth }.Where(t => t is not null).Sum(t => (long)t!.Width * t.Height * (t.ColorAttachments.Count * 8 + (t.DepthStencilAttachment is null ? 0 : 4)));
    public double AtmosphereGpuMilliseconds => gpuTimer.AtmosphereMilliseconds;
    public double CloudGpuMilliseconds => gpuTimer.CloudMilliseconds;
    public double FogGpuMilliseconds => gpuTimer.FogMilliseconds;
    public double WaterGpuMilliseconds => gpuTimer.WaterMilliseconds;
    void ClearVolumes()
    {
        foreach (var t in new[] { cloudCurrent, cloudVolume, cloudHistory, cloudShadow, volumeDensity, volumeIntegrated, farSunDepth })
            t?.Dispose();
        cloudCurrent = cloudVolume = cloudHistory = cloudShadow = volumeDensity = volumeIntegrated = farSunDepth = null;
        farSunRevision = -1;
        fogRoomRevision = -1;
        cloudValid = false;
        lastVolumeTime = float.NaN;
    }
    sealed class TextureBindings(List<IDisposable> values) : IDisposable
    {
        public void Dispose()
        {
            for (int i = values.Count - 1; i >= 0; i--)
                values[i].Dispose();
        }
    }
    IDisposable AtmosphereBindings() => new TextureBindings([
        (atmosphereSky?.ColorAttachments[0]??white).Bind(16),
        (atmosphereLight?.ColorAttachments[0]??white).Bind(17),
        (cloudVolume?.ColorAttachments[0]??white).Bind(18),
        (cloudShadow?.ColorAttachments[0]??white).Bind(19),
        (volumeIntegrated?.ColorAttachments[0]??white).Bind(20),(farSunDepth?.DepthStencilAttachment??white).Bind(21),(celestial?.ColorAttachments[0]??white).Bind(22)]);
    void AtmosphereUniforms(ShaderProgram p)
    {
        SurfaceUniforms(p);
        DustUniforms(p);
        p.SetUniform("uCelestial", 22);
        p.SetUniform("uCelestialPhase", (float)environmentFrame.Phase);
        p.SetUniform("uAtmosphereSky", 16);
        p.SetUniform("uAtmosphereLight", 17);
        p.SetUniform("uCloudVolume", 18);
        p.SetUniform("uCloudShadow", 19);
        p.SetUniform("uFogVolume", 20);
        p.SetUniform("uAtmosphereEnabled", atmosphereSky is null ? 0 : 1);
        p.SetUniform("uVolumeEnabled", VolumetricsEnabled && volumeIntegrated is not null ? 1 : 0);
        p.SetUniform("uCloudEnabled", CloudsEnabled && cloudVolume is not null ? 1 : 0);
        p.SetUniform("uFarSunDepth", 21);
        p.SetUniform("uFarSunMatrix", farSunMatrix);
        p.SetUniform("uFarSunEnabled", farSunDepth is null || !ShadowsEnabled ? 0 : 1);
        p.SetUniform("uVolumeGrid", volumeGrid);
        p.SetUniform("uVolumeViewProjection", volumeViewProjection);
    }
    void CloudUniforms(ShaderProgram p)
    {
        p.SetUniform("uCloudWind", weatherCloudOffset * 250);
        p.SetUniform("uCloudBase", World.Definition.Spawns[0].Y + (World.Definition.Fixture is null ? 1800 : 800));
        p.SetUniform("uCloudTop", World.Definition.Spawns[0].Y + (World.Definition.Fixture is null ? 3000 : 2400));
        p.SetUniform("uCloudCoverage", weatherFrame.Snow ? .85f : AtmosphereIllumination.CloudCoverage(World.Definition.Preset == "icy" ? "temperate" : World.Definition.Preset));
        p.SetUniform("uSunDirection", environmentFrame.SunDirection);
        p.SetUniform("uGroundLevel", World.Definition.Spawns[0].Y);
    }
    void PrepareVolumes(RenderFrame frame, Camera camera, SceneFrame snapshot, float time)
    {
        if (atmosphereLight is null) return;
        var quality = Settings.Quality;
        // This is a 360-degree atlas, not a screen-sized half-resolution image.
        // The former 960-wide atlas put only ~240 texels across a normal view.
        int cw = quality == GraphicsQuality.Low ? 1024 : quality == GraphicsQuality.High ? 3072 : 2048;
        int ch = cw / 2, shadow = quality == GraphicsQuality.Low ? 256 : quality == GraphicsQuality.High ? 1024 : 512;
        Vector3 grid = quality == GraphicsQuality.Low ? new(120, 68, 48) : quality == GraphicsQuality.High ? new(240, 135, 96) : new(160, 90, 64);
        grid.X = Math.Max(8, MathF.Ceiling(grid.X * graphics.Backbuffer.Width / 1920));
        grid.Y = Math.Max(8, MathF.Ceiling(grid.Y * graphics.Backbuffer.Height / 1080));
        if (volumeWidth != cw || volumeHeight != ch || grid != volumeGrid)
        {
            ClearVolumes();
            volumeGrid = grid;
            volumeWidth = cw;
            volumeHeight = ch;
        }
        var view = new RenderView(camera);
        Matrix4x4.Invert(camera.View * camera.Projection, out var inverse);
        volumeViewProjection = camera.View * camera.Projection;
        if (snapshot.PhysicsEpoch != volumeEpoch || !float.IsFinite(lastVolumeTime) || time < lastVolumeTime || time - lastVolumeTime > .25f || Vector3.DistanceSquared(previousCloudEye, camera.Position) > 100)
            cloudValid = false;
        volumeEpoch = snapshot.PhysicsEpoch;
        lastVolumeTime = time;
        if (CloudsEnabled)
        {
            cloudTemporalShader ??= RequestOptionalProgram("fullscreen.vert", "cloud_temporal.frag");
            cloudVolumeShader ??= RequestOptionalProgram("fullscreen.vert", "cloud_volume.frag");
            cloudShadowShader ??= RequestOptionalProgram("fullscreen.vert", "cloud_shadow.frag");
            if (cloudTemporalShader is null || cloudVolumeShader is null || cloudShadowShader is null) return;
            RenderTarget CloudTarget()
            {
                var target = AtmosphereTarget(cw, ch, 2);
                foreach (var tex in target.ColorAttachments)
                    tex.SetSampling(new(TextureFilter.Linear, TextureFilter.Linear, TextureWrap.Repeat, TextureWrap.ClampToEdge));
                return target;
            }
            cloudCurrent ??= CloudTarget();
            cloudVolume ??= CloudTarget();
            cloudHistory ??= CloudTarget();
            // Reuse last frame's resolved textures directly; no two full-atlas
            // color/depth copies or repeated sampler-state changes per frame.
            (cloudVolume, cloudHistory) = (cloudHistory, cloudVolume);
            cloudShadow ??= AtmosphereTarget(shadow, shadow);
            cloudVolumeShader.SetUniform("uLight", 2);
            cloudVolumeShader.SetUniform("uEye", camera.Position);
            cloudVolumeShader.SetUniform("uSteps", quality == GraphicsQuality.Low ? 40 : quality == GraphicsQuality.High ? 88 : 64);
            cloudVolumeShader.SetUniform("uJitter", FreezeVisuals ? .5f : (float)((frameNumber * .61803398875) % 1));
            CloudUniforms(cloudVolumeShader);
            CloudUniforms(cloudShadowShader);
            using (atmosphereLight!.ColorAttachments[0].Bind(2))
            using (var pass = frame.BeginPass(cloudCurrent, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear }))
            using (gpuTimer.Measure(pass, "cloud"))
                Mesh(pass, screen, white, cloudVolumeShader);
            cloudTemporalShader.SetUniform("uCurrent", 1);
            cloudTemporalShader.SetUniform("uCurrentDepth", 2);
            cloudTemporalShader.SetUniform("uHistory", 3);
            cloudTemporalShader.SetUniform("uHistoryDepth", 4);
            cloudTemporalShader.SetUniform("uEye", camera.Position);
            cloudTemporalShader.SetUniform("uPreviousEye", previousCloudEye);
            cloudTemporalShader.SetUniform("uWindDelta", weatherCloudOffset * 250 - previousCloudWind);
            cloudTemporalShader.SetUniform("uHistoryWeight", cloudValid ? .72f : 0);
            using (cloudCurrent.ColorAttachments[0].Bind(1))
            using (cloudCurrent.ColorAttachments[1].Bind(2))
            using (cloudHistory.ColorAttachments[0].Bind(3))
            using (cloudHistory.ColorAttachments[1].Bind(4))
            using (var pass = frame.BeginPass(cloudVolume, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
            using (gpuTimer.Measure(pass, "cloud"))
                Mesh(pass, screen, white, cloudTemporalShader);
            previousCloudWind = weatherCloudOffset * 250;
            if (!cloudValid || frameNumber % 8 == 0)
            {
                using var pass = frame.BeginPass(cloudShadow, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear });
                using (gpuTimer.Measure(pass, "cloud"))
                    Mesh(pass, screen, white, cloudShadowShader);
            }
            cloudValid = true;
        }
        previousCloudEye = camera.Position;
        PrepareFarSun(frame);
        if (!VolumetricsEnabled)
            return;
        volumeInjectShader ??= RequestOptionalProgram("fullscreen.vert", "volume_inject.frag");
        volumeIntegrateShader ??= RequestOptionalProgram("fullscreen.vert", "volume_integrate.frag");
        if (volumeInjectShader is null || volumeIntegrateShader is null) return;
        volumeDensity ??= AtmosphereTarget((int)grid.X * 8, (int)grid.Y * (int)Math.Ceiling(grid.Z / 8));
        volumeIntegrated ??= AtmosphereTarget(volumeDensity.Width, volumeDensity.Height);
        Lighting(volumeInjectShader);
        CloudUniforms(volumeInjectShader);
        GateLightingUniforms(volumeInjectShader);
        volumeInjectShader.SetUniform("uVolumeEnabled", 0);
        KawooshVolumeUniforms(volumeInjectShader, camera.Position);
        InteriorUniforms(volumeInjectShader, camera.Position);
        GroundMistUniforms(volumeInjectShader, 24);
        volumeInjectShader.SetUniform("uShelter", 23);
        volumeInjectShader.SetUniform("uShelterMatrix", shelterMatrix);
        volumeInjectShader.SetUniform("uShelterInverse", shelterInverse);
        volumeInjectShader.SetUniform("uBlowingSnowDensity", weatherFrame.Snow && weatherShelter is not null ? WeatherSettings.BlowingSnowDensity * Math.Clamp((weatherFrame.Gust - .15f) / .85f, 0, 1) : 0);
        volumeInjectShader.SetUniform("uSnowTravel", (float)(WeatherFrame.WindTravel(World.Definition.Seed, weatherFrame.Time, ActiveWeather) % 1024));
        volumeInjectShader.SetUniform("uGrid", grid);
        volumeInjectShader.SetUniform("uInverseViewProjection", inverse);
        volumeInjectShader.SetUniform("uViewPosition", camera.Position);
        volumeInjectShader.SetUniform("uView", camera.View);
        volumeInjectShader.SetUniform("uShadowMaps", new[] { 9, 9, 9 });
        volumeInjectShader.SetUniform("uDynamicShadowMaps", new[] { 9, 9, 9 });
        using ((weatherShelter?.DepthStencilAttachment ?? white).Bind(23))
        using ((mistGround?.DepthStencilAttachment ?? white).Bind(24))
        using (AtmosphereBindings())
        using (gateShadowAtlas?.DepthStencilAttachment.Bind(15))
        using (shadows.CurrentFrame.Bind(volumeInjectShader, 9))
        using (var pass = frame.BeginPass(volumeDensity, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear }))
        using (gpuTimer.Measure(pass, "fog"))
            Mesh(pass, screen, white, volumeInjectShader);
        volumeIntegrateShader.SetUniform("uGrid", grid);
        volumeIntegrateShader.SetUniform("uDensity", 1);
        using (volumeDensity.ColorAttachments[0].Bind(1))
        using (var pass = frame.BeginPass(volumeIntegrated, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear }))
        using (gpuTimer.Measure(pass, "fog"))
            Mesh(pass, screen, white, volumeIntegrateShader);
    }
    void PrepareFarSun(RenderFrame frame)
    {
        if (environmentFrame.SunDirection.Y <= 0)
            return;
        bool dirty = farSunDepth is null || farSunMeshes != resident.Count || farSunRevision != buildingState.Revision || Vector3.DistanceSquared(farSunDirection, environmentFrame.SunDirection) > .00000024f;
        if (!dirty)
            return;
        farSunDepth ??= graphics.CreateRenderTarget(new(1024, 1024, [], TextureFormat.Depth32Float));
        farSunDepth.DepthStencilAttachment.SetSampling(new(TextureFilter.Nearest, TextureFilter.Nearest, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge));
        Vector3 centre = new(0, World.Definition.Spawns[0].Y + 250, 0), eye = centre + environmentFrame.SunDirection * 6000;
        var view = Matrix4x4.CreateLookAt(eye, centre, Math.Abs(environmentFrame.SunDirection.Y) > .98f ? Vector3.UnitZ : Vector3.UnitY);
        var projection = Matrix4x4.CreateOrthographic(World.Definition.HalfSize * 2.9f, World.Definition.HalfSize * 2.9f, 1, 12000);
        farSunMatrix = view * projection;
        var frustum = ViewFrustum.FromMatrix(farSunMatrix);
        using (var pass = frame.BeginPass(farSunDepth, new RenderPassDescriptor { View = new(view, projection, eye, new(1024), 1, 12000), State = OpaqueState, DepthLoadAction = RenderLoadAction.Clear }))
        using (gpuTimer.Measure(pass, "atmosphere"))
            DrawStatic(pass, depthShader, frustum, true);
        farSunMeshes = resident.Count;
        farSunRevision = buildingState.Revision;
        farSunDirection = environmentFrame.SunDirection;
    }
    readonly List<(Vector4 Bounds, Vector4[] Edges, Vector3 Centre)> fogRooms = [];
    long fogRoomRevision = -1;
    StargateGame.Shared.WorldChanges? fogRoomState;
    void InteriorUniforms(ShaderProgram program, Vector3 eye)
    {
        if (fogRoomRevision != buildingState.Revision || !ReferenceEquals(fogRoomState, buildingState))
        {
            fogRooms.Clear();
            fogRoomRevision = buildingState.Revision;
            fogRoomState = buildingState;
            foreach (var structure in buildingState.Structures.Where(s => s.Generated))
                foreach (var root in buildingState.Pieces.Where(p => p.StructureId == structure.Id && p.Kind == PieceKind.Prefab && p.Parent == Guid.Empty))
                {
                    var template = SettlementTemplates.All.FirstOrDefault(t => t.Id == root.Prefab);
                    if (template is null || !template.RoofCoverage || template.Doors.Length == 0)
                        continue;
                    // Collision bounds describe the enclosed storey; visual bounds include roof overhangs.
                    var vertices = BuildingPrefabs.Get(root.Prefab).Triangles.SelectMany(t => t).ToArray();
                    float x0 = vertices.Min(v => v[0]) + .4f, x1 = vertices.Max(v => v[0]) - .4f;
                    float z0 = vertices.Min(v => v[2]) + .4f, z1 = vertices.Max(v => v[2]) - .4f;
                    var transform = BuildingPrefabs.Transform(structure, root);
                    var points = new[] { new Vector3(x0, template.FloorHeight, z0), new Vector3(x1, template.FloorHeight, z0), new Vector3(x1, template.FloorHeight, z1), new Vector3(x0, template.FloorHeight, z1) }.Select(p => Vector3.Transform(p, transform)).ToArray();
                    Vector3 centre = points.Aggregate(Vector3.Zero, (a, b) => a + b) / 4;
                    Vector4[] edges = new Vector4[4];
                    for (int i = 0; i < 4; i++)
                    {
                        var a = points[i];
                        var b = points[(i + 1) % 4];
                        var normal = Vector3.Normalize(new Vector3(-(b.Z - a.Z), 0, b.X - a.X));
                        if (Vector3.Dot(normal, centre - a) < 0)
                            normal = -normal;
                        edges[i] = new(normal, -Vector3.Dot(normal, a));
                    }
                    fogRooms.Add((new(centre.Y, centre.Y + 2.8f, 0, 0), edges, centre));
                }
            foreach (var roof in buildingState.Pieces.Where(p => BuildingGeometry.Floor(p.Kind) && p.Level > 0))
            {
                var structure = buildingState.Structures.Single(s => s.Id == roof.StructureId);
                var corners = BuildingGeometry.Corners(roof);
                bool closed = true;
                for (int i = 0; i < corners.Length; i++)
                {
                    var a = corners[i];
                    var b = corners[(i + 1) % corners.Length];
                    if (!buildingState.Pieces.Any(w => w.StructureId == roof.StructureId && w.Level == roof.Level - 1 && BuildingGeometry.Wall(w.Kind) &&
                        (w.Point == a && BuildingGeometry.Add(w.Point, BuildingGeometry.Direction(w.Rotation)) == b || w.Point == b && BuildingGeometry.Add(w.Point, BuildingGeometry.Direction(w.Rotation)) == a)))
                    {
                        closed = false;
                        break;
                    }
                }
                if (!closed)
                    continue;
                var points = corners.Select(c => BuildingGeometry.WorldPoint(structure, c, roof.Level)).ToArray();
                Vector3 centre = points.Aggregate(Vector3.Zero, (a, b) => a + b) / points.Length;
                Vector4[] edges = new Vector4[4];
                for (int i = 0; i < points.Length; i++)
                {
                    Vector3 a = points[i], b = points[(i + 1) % points.Length], normal = Vector3.Normalize(new Vector3(-(b.Z - a.Z), 0, b.X - a.X));
                    if (Vector3.Dot(normal, centre - a) < 0)
                        normal = -normal;
                    edges[i] = new(normal, -Vector3.Dot(normal, a));
                }
                if (points.Length == 3)
                    edges[3] = new(0, 0, 0, 1);
                // A storey is 3 m; the ceiling slab occupies its upper 0.2 m.
                // Include the floor itself so a low blanket cannot leak through the bottom 20 cm.
                fogRooms.Add((new(centre.Y - 3f, centre.Y - .2f, 0, 0), edges, centre));
            }
        }
        var nearest = fogRooms.OrderBy(r => Vector3.DistanceSquared(r.Centre, eye)).Take(32).ToArray();
        Vector4[] bounds = new Vector4[32], planes = new Vector4[128];
        for (int i = 0; i < nearest.Length; i++)
        {
            bounds[i] = nearest[i].Bounds;
            nearest[i].Edges.CopyTo(planes, i * 4);
        }
        program.SetUniform("uRoomCount", nearest.Length);
        for (int i = 0; i < nearest.Length; i++)
        {
            program.SetUniform($"uRoomBounds[{i}]", bounds[i]);
            for (int j = 0; j < 4; j++)
                program.SetUniform($"uRoomEdges[{i * 4 + j}]", planes[i * 4 + j]);
        }
    }

}
