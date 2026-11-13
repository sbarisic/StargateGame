using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

// Renderer-owned, derived from the presented owner pose. Never persisted separately.
sealed record PointLight(Guid Owner, Vector3 Position, Vector3 Color, float Intensity, float Range, float SourceRadius, bool Shadows, int SourcePlayer = 0);

sealed partial class SceneRenderer
{
    const int MaximumPointLights = 16, MaximumPointFaces = MaximumPointLights * 6;
    readonly List<PointLight> propLights = [];
    RenderTarget? propShadowAtlas;
    ShaderProgram? propDepthShader;
    readonly Matrix4x4[] propLightMatrices = new Matrix4x4[MaximumPointFaces];
    readonly ViewFrustum[] propLightFrusta = new ViewFrustum[MaximumPointFaces];
    readonly RenderView[] propLightViews = new RenderView[MaximumPointFaces];
    readonly bool[] propStaticDirty = Enumerable.Repeat(true, MaximumPointFaces).ToArray();
    readonly ShadowActor[]?[] propActorState = new ShadowActor[MaximumPointFaces][];
    readonly double[] propAnimationTime = new double[MaximumPointFaces];
    readonly bool[] propHadCables = new bool[MaximumPointFaces];
    readonly double[] propCableTime = new double[MaximumPointFaces];
    int propFaceResolution, propShadowSlots;
    bool pointShadowPass;
    readonly double[] propWindTime = Enumerable.Repeat(double.NaN, MaximumPointFaces).ToArray();
    SceneFrame? lanternFrame;
    bool lanternEmissionVisible;
    PropClip[] pointShadowClips = [];
    static readonly Vector3[] pointDirections = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
    static readonly Vector3[] pointUps = [-Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitY, -Vector3.UnitY];
    public int PointLightCount => propLights.Count;
    public int PointShadowUpdates { get; private set; }
    public long PointLightTargetBytes => (long)(propShadowAtlas?.Width ?? 0) * (propShadowAtlas?.Height ?? 0) * 4;
    public double PointShadowGpuMilliseconds => gpuTimer.PointShadowMilliseconds;
    public double PointLightGpuMilliseconds => gpuTimer.PointLightMilliseconds;

    static RenderView PointShadowView(Vector3 position, float range, int resolution, int face)
    {
        int tile = resolution + GateGuard * 2;
        return new(Matrix4x4.CreateLookAt(position, position + pointDirections[face], pointUps[face]),
            Matrix4x4.CreatePerspectiveFieldOfView(2 * MathF.Atan(tile / (float)resolution), 1, .04f, range), position, new(tile, tile), .04f, range);
    }
    void DisposePointLights()
    {
        rifleEffects.Clear(); rifleLights.Clear(); propShadowSlots = 0;
        propShadowAtlas?.Dispose(); propShadowAtlas = null; propLights.Clear(); lanternFrame = null; lanternEmissionVisible = false; pointShadowClips = [];
        Array.Fill(propStaticDirty, true); Array.Clear(propActorState); Array.Fill(propAnimationTime, double.NaN);
    }
    void InvalidatePointShadows(AxisAlignedBoundingBox? bounds)
    {
        for (int i = 0; i < propLights.Count * 6; i++)
            if (bounds is null || propLightFrusta[i].Intersects(bounds.Value)) propStaticDirty[i] = true;
    }
    void PreparePointLights(RenderFrame frame, Camera camera, SceneFrame snapshot, float time)
    {
        lanternFrame = snapshot; PointShadowUpdates = 0;
        var frustum = ViewFrustum.FromCamera(camera);
        lanternEmissionVisible = false;
        if (!pointShadowClips.SequenceEqual(snapshot.Clips ?? []))
        { pointShadowClips = (snapshot.Clips ?? []).ToArray(); Array.Clear(propActorState); }
        var selected = new List<PointLight>();
        foreach (var definition in World.Definition.Props.Where(p => p.Kind == PropKind.Lantern).OrderBy(p => p.PersistentId))
        {
            var body = snapshot.Bodies.FirstOrDefault(b => b.Id == definition.Id);
            if (body.Id != definition.Id) continue;
            float extent = definition.HalfExtent.Length();
            lanternEmissionVisible |= frustum.Intersects(new(body.Position - new Vector3(extent), body.Position + new Vector3(extent)));
            var position = body.Position + Vector3.Transform(LanternAsset.LightPosition, body.Rotation);
            // Cull the influence volume, not the lantern mesh: offscreen lamps still illuminate.
            if (!frustum.Intersects(new(position - new Vector3(5), position + new Vector3(5)))) continue;
            var clip = snapshot.Clips?.FirstOrDefault(c => c.Body == definition.Id);
            if (clip is not null && clip.Gate.Distance(position) < 0) continue;
            selected.Add(new(definition.PersistentId, position, new(1, .55f, .25f), 4, 5, .04f, true));
        }
        if (selected.Count > LanternAsset.LocationLimit) throw new InvalidOperationException("Lantern renderer capacity exceeded (8 per location).");
        selected.AddRange(rifleLights);
        int resolution = Settings.Quality switch { GraphicsQuality.Low => 128, GraphicsQuality.High => 512, _ => 256 };
        for (int light = 0; light < selected.Count; light++)
            if (light >= propLights.Count || propLights[light].Position != selected[light].Position ||
                propLights[light].Range != selected[light].Range || propLights[light].Owner != selected[light].Owner ||
                propLights[light].SourcePlayer != selected[light].SourcePlayer)
                for (int face = light * 6; face < light * 6 + 6; face++) { propStaticDirty[face] = true; propActorState[face] = null; }
        // Keep the high-water capacity between short flashes. Static/dynamic
        // face offsets use capacity too, so lantern caches never shift on fire.
        if (resolution != propFaceResolution || selected.Count > propShadowSlots || !ShadowsEnabled)
        {
            propShadowAtlas?.Dispose(); propShadowAtlas = null; propFaceResolution = resolution;
            propShadowSlots = Math.Max(propShadowSlots, selected.Count);
            Array.Fill(propStaticDirty, true); Array.Clear(propActorState);
        }
        propLights.Clear(); propLights.AddRange(selected);
        if (selected.Count == 0 || !ShadowsEnabled) return;
        int tile = resolution + GateGuard * 2;
        if (propShadowAtlas is null)
        {
            propShadowAtlas = graphics.CreateRenderTarget(new(tile * GateAtlasColumns, tile * propShadowSlots, [], TextureFormat.Depth32Float));
            propShadowAtlas.DepthStencilAttachment.SetSampling(new(TextureFilter.Nearest, TextureFilter.Nearest, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge));
        }
        propDepthShader ??= LoadProgram("point_depth.vert", "depth.frag");
        for (int i = 0; i < propLights.Count * 6; i++)
        {
            var light = propLights[i / 6];
            if (!light.Shadows) continue;
            propLightViews[i] = PointShadowView(light.Position, light.Range, resolution, i % 6);
            var view = propLightViews[i];
            propLightMatrices[i] = view.View * view.Projection;
            var face = propLightFrusta[i] = ViewFrustum.FromMatrix(propLightMatrices[i]);
            bool wind = weatherFrame.Wind.LengthSquared() > 0 && Candidates(face).Trees.Any();
            if (wind && propWindTime[i] != weatherFrame.Time) propStaticDirty[i] = true;
            propWindTime[i] = weatherFrame.Time;
            if (propStaticDirty[i]) { DrawPropShadowFace(frame, i, false, snapshot, time); propStaticDirty[i] = false; }
            var actors = new List<ShadowActor>();
            foreach (var p in CharacterActors(snapshot).OrderBy(p => p.Id))
                if (face.Intersects(new(p.Position - new Vector3(.4f, .01f, .4f), p.Position + new Vector3(.4f, 1.8f, .4f))))
                    actors.Add(new(0, p.Id, p.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -p.Yaw)));
            foreach (var p in snapshot.Bodies.OrderBy(p => p.Id))
                if (World.Definition.Props.FirstOrDefault(d => d.Id == p.Id)?.CastsShadows == true && face.Intersects(new(p.Position - new Vector3(.9f), p.Position + new Vector3(.9f))))
                    actors.Add(new(1, p.Id, p.Position, p.Rotation));
            bool animated = actors.Any(a => a.Kind == 0);
            bool hasCables = cableVisuals.Values.Any(c => c.Visible && face.Intersects(c.Bounds));
            var current = actors.ToArray();
            if (propActorState[i] is not { } old || !old.SequenceEqual(current) || (animated || light.SourcePlayer == snapshot.Local.Id && riflePoseReady) && propAnimationTime[i] != snapshot.SimulationTime || propHadCables[i] != hasCables || hasCables && propCableTime[i] != cableLastTime)
            {
                DrawPropShadowFace(frame, i, true, snapshot, time); propActorState[i] = current; propAnimationTime[i] = snapshot.SimulationTime; propHadCables[i] = hasCables; propCableTime[i] = cableLastTime;
            }
        }
    }
    void DrawPropShadowFace(RenderFrame frame, int face, bool dynamic, SceneFrame snapshot, float time)
    {
        int tile = propFaceResolution + GateGuard * 2, index = face + (dynamic ? propShadowSlots * 6 : 0), x = index % GateAtlasColumns * tile, y = index / GateAtlasColumns * tile;
        var state = OpaqueState with { ColorWriteMask = ColorWriteMask.None, ScissorRectangle = new(new Vector3(x,y,0),new Vector3(x+tile,y+tile,1)), DepthBiasSlope = 1.2f, DepthBiasConstant = 1 };
        using var pass = frame.BeginPass(propShadowAtlas!, new RenderPassDescriptor { View = propLightViews[face], State = state, Time = time });
        using var timer = gpuTimer.Measure(pass, "point-shadow");
        pass.Clear(Color.Black, false, true, false);
        propDepthShader!.SetUniform("uTile", new Vector4(1f/GateAtlasColumns,1f/propShadowSlots,(x+tile*.5f)*2/propShadowAtlas!.Width-1,(y+tile*.5f)*2/propShadowAtlas.Height-1));
        if (dynamic)
        {
            bool muzzle = propLights[face / 6].SourcePlayer == snapshot.Local.Id && riflePoseReady;
            DrawActors(pass, propDepthShader, propLightFrusta[face], snapshot, true, excludeLocal: muzzle);
            DrawCables(pass, propDepthShader, propLightFrusta[face], true);
            if (muzzle)
            {
                using var model = pass.PushModel(rifleWorldTransform);
                DrawSkin(pass, propDepthShader, rifle!, riflePose, 1, true);
            }
        }
        else
        {
            pointShadowPass = true;
            try { DrawStatic(pass, propDepthShader, propLightFrusta[face], true); }
            finally { pointShadowPass = false; }
        }
        PointShadowUpdates++;
    }
    void PointLightingUniforms(ShaderProgram p, bool viewmodel = false)
    {
        // Approximate the flash's local diffuse bounce only on the shooter's
        // viewmodel. The actual light stays at the muzzle and retains shadows.
        p.SetUniform("uPropMuzzleSpillMask", viewmodel ? propLights.Select((l,i) => l.SourcePlayer != 0 && l.SourcePlayer == rifleLocal ? 1 << i : 0).Aggregate(0,(a,b)=>a|b) : 0);
        p.SetUniform("uPropShadowMask", propLights.Select((l,i) => l.Shadows ? 1 << i : 0).Aggregate(0,(a,b)=>a|b));
        p.SetUniform("uPropLightCount", propLights.Count); p.SetUniform("uPropShadowCount", propShadowSlots); p.SetUniform("uPropShadowAtlas", 23);
        p.SetUniform("uPropShadows", ShadowsEnabled && propShadowAtlas is not null ? 1 : 0);
        if (propShadowAtlas is not null)
        {
            p.SetUniform("uPropAtlasSize", new Vector2(propShadowAtlas.Width, propShadowAtlas.Height));
            p.SetUniform("uPropTileSize", (float)(propFaceResolution + GateGuard*2));
        }
        for (int i = 0; i < propLights.Count; i++)
        {
            var l = propLights[i];
            p.SetUniform($"uPropPositionRange[{i}]", new Vector4(l.Position,l.Range));
            p.SetUniform($"uPropColorRadius[{i}]", new Vector4(l.Color*l.Intensity,l.SourceRadius));
        }
    }
}
