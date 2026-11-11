using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public bool HandShadowBaseline { get; set; }
    public int HandInspection { get; set; }
    public string HandInspectionPose { get; set; } = "";
    SkeletalAsset? military, rifle, preparingCharacter;
    readonly Dictionary<int, Matrix4x4[]> actorPoses = [];
    sealed record ActorAnimation(string Clip, double Started, string? Previous = null, double PreviousStarted = 0);
    readonly Dictionary<int, ActorAnimation> actorAnimation = [];
    readonly Matrix4x4[] riflePose = new Matrix4x4[128];
    public IReadOnlyDictionary<int, CharacterPresentation> CharacterStates
    {
        get;
        set;
    } = new Dictionary<int, CharacterPresentation>();
    public bool LowerWeapon
    {
        get;
        set;
    }
    public bool HideWeapon
    {
        get;
        set;
    }
    public int InspectionCharacters
    {
        get;
        set;
    } = 1;
    public string? InspectionAnimation
    {
        get;
        set;
    }
    public double? InspectionTime
    {
        get;
        set;
    }
    RenderTarget? viewmodelTarget;
    RenderTarget? viewmodelShadow;
    Matrix4x4 viewmodelShadowMatrix;
    ShaderProgram? viewmodelComposite;
    bool viewmodelDrawn;
    public bool InspectionCamera { get; set; }
    float aimBlend;
    double lastCharacterTime;
    public double CharacterCpuMilliseconds
    {
        get;
        private set;
    }
    public double CharacterGpuMilliseconds => gpuTimer.CharacterMilliseconds;
    IEnumerable<bool> PrepareCharacterResources()
    {
        foreach (string name in new[] { "military.sga.gz", "retro-rifle.sga.gz" })
        {
            if (name == "military.sga.gz" ? military is not null : rifle is not null)
                continue;
            var work = modelAssets.PrepareCpuAsync(() => SkeletalAsset.Prepare(Asset("characters", name), modelAssets, startupStop.Token), startupStop.Token);
            while (!work.IsCompleted)
                yield return false;
            var data = work.GetAwaiter().GetResult();
            var asset = Own(new SkeletalAsset(data));
            preparingCharacter = asset;
            foreach (var p in data.Parts)
            {
                foreach (string texture in p.Textures)
                {
                    string key = texture + "|character";
                    if (!textures.ContainsKey(key))
                        foreach (bool step in UploadTexture(texture, key, TextureWrap.ClampToEdge, "characters/"))
                            yield return step;
                }
                foreach (bool step in asset.UploadPart(graphics, p, file => textures[file + "|character"]))
                    yield return step;
            }
            if (name == "military.sga.gz")
                military = asset;
            else
                rifle = asset;
            preparingCharacter = null;
            yield return true;
        }
    }
    void PrepareCharacters(SceneFrame snapshot, float time)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (military is null) return;
        foreach (var p in CharacterActors(snapshot))
        {
            if (!actorPoses.TryGetValue(p.Id, out var pose))
                actorPoses[p.Id] = pose = new Matrix4x4[128];
            var relative = p.Velocity - (p.Grounded ? p.GroundVelocity : Vector3.Zero);
            float speed = new Vector2(relative.X, relative.Z).Length();
            string clip = p.Swimming ? (speed > .3 ? "swim" : "swim-idle") : p.Crouched ? (speed > .2 ? "crouch-walk" : "crouch-idle") : !p.Grounded ? (p.Velocity.Y > 1 ? "jump" : "fall") : speed > 3.5 ? "run" : speed > .2 ? "walk" : "idle";
            if (p.Id < 0)
            {
                string inspectionClip = InspectionAnimation ?? clip;
                military.Sample(inspectionClip, InspectionTime ?? snapshot.SimulationTime, pose, inspectionClip is not ("jump" or "land"));
            }
            else
            {
                double now = snapshot.SimulationTime;
                if (!actorAnimation.TryGetValue(p.Id, out var playing))
                    playing = new(clip, now);
                if (playing.Clip != clip)
                    playing = new(clip, now, playing.Clip, playing.Started);
                actorAnimation[p.Id] = playing;
                military.SampleBlend(clip, now - playing.Started, playing.Previous, now - playing.PreviousStarted, (float)((now - playing.Started) / .15), pose, clip is not ("jump" or "land"));
            }
        }
        var retained = CharacterActors(snapshot).Select(p => p.Id).ToHashSet();
        foreach (int id in actorPoses.Keys.Where(id => !retained.Contains(id)).ToArray())
        {
            actorPoses.Remove(id);
            actorAnimation.Remove(id);
        }
        CharacterCpuMilliseconds = watch.Elapsed.TotalMilliseconds;
    }
    IEnumerable<PlayerState> CharacterActors(SceneFrame snapshot)
    {
        foreach (var p in snapshot.Remotes.Append(snapshot.Local).Where(p => !p.TransitFrozen))
            yield return p;
        if (World.Definition.Fixture == "graphics-lab")
            for (int i = 0; i < InspectionCharacters; i++)
                yield return new(-100 - i, "Inspection", new(-4 + i * 2, 0, -4), Vector3.Zero, 0, 0, true, 0);
    }
    void DrawSkin(RenderPass pass, ShaderProgram program, SkeletalAsset asset, Matrix4x4[] pose, float lod, bool depth)
    {
        program.SetUniform("uSkinning", 1);
        program.SetUniform("uBones", pose.AsSpan(0, asset.Bones.Length));
        program.SetUniform("uTint", Vector3.One);
        program.SetUniform("uTerrain", 0);
        program.SetUniform("uTreeVertices", 0);
        program.SetUniform("uTreeMode", 0);
        program.SetUniform("uFoliageCutoff", 0f);
        program.SetUniform("uModelNormal", 5);
        program.SetUniform("uMetallicRoughness", 6);
        foreach (var p in asset.Parts.Where(p => Math.Abs(p.Lod - lod) < .01))
        {
            // Keep skin and the military body's materials separate from the rifle finish.
            bool weaponPart = ReferenceEquals(asset, rifle) && p.Name != "FPS_Arms_Mesh";
            bool armPart = ReferenceEquals(asset, rifle) && p.Name == "FPS_Arms_Mesh";
            program.SetUniform("uArmSkin", !depth && armPart ? 1 : 0);
            program.SetUniform("uRifleFinish", !depth && weaponPart ? (p.Name.Contains("Magazine", StringComparison.Ordinal) ? 2 : 1) : 0);
            program.SetUniform("uHasModelNormal", !depth && p.Normal is not null ? 1 : 0);
            program.SetUniform("uHasMetallicRoughness", !depth && p.MetallicRoughness is not null ? 1 : 0);
            using var normal = depth ? null : p.Normal?.Bind(5);
            using var mr = depth ? null : p.MetallicRoughness?.Bind(6);
            Mesh(pass, p.Mesh, depth ? white : p.Albedo, program);
        }
        program.SetUniform("uSkinning", 0);
        program.SetUniform("uRifleFinish", 0);
        program.SetUniform("uArmSkin", 0);
        program.SetUniform("uHasModelNormal", 0);
        program.SetUniform("uHasMetallicRoughness", 0);
    }
    void DrawCharacters(RenderPass pass, ShaderProgram program, ViewFrustum frustum, SceneFrame snapshot, bool depth, bool excludeLocal = false)
    {
        foreach (var p in CharacterActors(snapshot))
        {
            if ((!depth || excludeLocal) && p.Id == snapshot.Local.Id)
                continue;
            if (!frustum.Intersects(new(p.Position - new Vector3(.8f, .1f, .8f), p.Position + new Vector3(.8f, 2.2f, .8f))))
                continue;
            float distance = Vector3.Distance(p.Position, grassEye), lod = distance < 25 ? 1 : distance < 60 ? .5f : .25f;
            using var model = pass.PushModel(Matrix4x4.CreateRotationY(-p.Yaw) * Matrix4x4.CreateTranslation(p.Position));
            if (military is not null && actorPoses.TryGetValue(p.Id, out var pose))
                DrawSkin(pass, program, military, pose, lod, depth);
            else
            {
                program.SetUniform("uSkinning", 0);
                program.SetUniform("uTint", new Vector3(.35f, .45f, .48f));
                Mesh(pass, body, white, program);
                program.SetUniform("uTint", Vector3.One);
            }
        }
    }
    void DrawViewmodel(RenderFrame frame, Camera camera, SceneFrame snapshot, float time)
    {
        viewmodelDrawn = false;
        if (InspectionCamera) return;
        if (rifle is null || HideWeapon)
            return;
        var state = CharacterStates.GetValueOrDefault(snapshot.Local.Id) ?? new(snapshot.Local.Id, 0, true, false, "idle", 0);
        double age = Math.Max(0, (GateVisualTick - state.StartTick) / 60);
        if (!state.Equipped && (state.Action != "holster" || age > rifle.Duration("holster")))
            return;
        float dt = (float)Math.Clamp(time - lastCharacterTime, 0, .1);
        lastCharacterTime = time;
        aimBlend += ((state.Aiming ? 1 : 0) - aimBlend) * Math.Min(1, dt * 14);
        string clip = state.Action;
        bool loop = false;
        if (clip == "idle" || age >= rifle.Duration(clip))
        {
            var relative = snapshot.Local.Velocity - (snapshot.Local.Grounded ? snapshot.Local.GroundVelocity : Vector3.Zero);
            float horizontalSpeed = new Vector2(relative.X, relative.Z).Length();
            clip = state.Aiming ? "aim" : snapshot.Local.Crouched ? (horizontalSpeed > .2f ? "crouch-walk" : "crouch-idle") : horizontalSpeed > 3.5f ? "run" : horizontalSpeed > .2f ? "walk" : "idle";
            age = time;
            loop = true;
        }
        if (clip == "fire" && state.Aiming)
            clip = "fire-aim";
        if (HandInspectionPose is "idle" or "aim" or "walk" or "reload" or "holster")
        {
            clip = HandInspectionPose; age = InspectionTime ?? .35; loop = false;
            aimBlend = clip == "aim" ? 1 : 0;
        }
        rifle.Sample(clip, age, riflePose, loop);
        if (viewmodelTarget?.Width != waterTarget!.Width || viewmodelTarget.Height != waterTarget.Height)
        {
            viewmodelTarget?.Dispose();
            viewmodelTarget = graphics.CreateRenderTarget(new(waterTarget.Width, waterTarget.Height, [TextureFormat.RGBA16Float], TextureFormat.Depth32Float));
        }
        var viewCamera = new Camera { Position = camera.Position };
        viewCamera.LookAt(camera.Position + camera.WorldForwardNormal);
        float vertical = (60 - 15 * aimBlend) * MathF.PI / 180;
        viewCamera.SetPerspective(viewmodelTarget.Width, viewmodelTarget.Height, 2 * MathF.Atan(MathF.Tan(vertical / 2) * viewmodelTarget.Width / viewmodelTarget.Height), .015f, 20);
        Matrix4x4.Invert(viewCamera.View, out var cameraWorld);
        float speed = new Vector2(snapshot.Local.Velocity.X, snapshot.Local.Velocity.Z).Length();
        float bob = MathF.Sin(time * (snapshot.Local.Crouched ? 6 : 9)) * Math.Min(speed, Movement.RunSpeed) * (snapshot.Local.Crouched ? .0005f : .001f);
        var transform = Matrix4x4.CreateTranslation(0, bob - (LowerWeapon ? .22f : 0), 0) * cameraWorld;
        if (ShadowsEnabled && !HandShadowBaseline)
            DrawViewmodelShadow(frame, camera, snapshot, transform);
        using (var pass = frame.BeginPass(viewmodelTarget, new RenderPassDescriptor { View = new(viewCamera), State = OpaqueState, ColorLoadAction = RenderLoadAction.Clear, ClearColor = new Color(0, 0, 0, 0), DepthLoadAction = RenderLoadAction.Clear }))
        using (gpuTimer.Measure(pass, "character"))
        using (pass.PushModel(transform))
        using (shadows.CurrentFrame.Bind(shader, 9))
        using (viewmodelShadow?.DepthStencilAttachment.Bind(29))
        using (gateShadowAtlas?.DepthStencilAttachment.Bind(15))
        {
            Lighting(shader);
            GateLightingUniforms(shader);
            shader.SetUniform("uFogDeferred", 1);
            shader.SetUniform("uDhdMatte", 0);
            shader.SetUniform("uDhdLightCount", 0);
            shader.SetUniform("uRockSnow", 0);
            shader.SetUniform("uTravelPlane", Vector4.Zero);
            shader.SetUniform("uShadowSurfaceBias", .015f);
            shader.SetUniform("uArmShadowBaseline", HandShadowBaseline ? 1 : 0);
            shader.SetUniform("uViewmodelShadowEnabled", ShadowsEnabled && !HandShadowBaseline ? 1 : 0);
            shader.SetUniform("uViewmodelShadow", 29);
            shader.SetUniform("uViewmodelShadowMatrix", viewmodelShadowMatrix);
            shader.SetUniform("uHandInspection", HandInspection);
            shader.SetUniform("uShadowComponents", HandInspection == 3 ? 1 : HandInspection == 4 ? 2 : 0);
            shader.SetUniform("uRoughness", .8f);
            DrawSkin(pass, shader, rifle, riflePose, 1, false);
            shader.SetUniform("uArmShadowBaseline", 0);
            shader.SetUniform("uViewmodelShadowEnabled", 0);
            shader.SetUniform("uHandInspection", 0);
            shader.SetUniform("uShadowComponents", 0);
        }
        viewmodelDrawn = true;
        viewmodelComposite ??= LoadProgram("fullscreen.vert", "viewmodel_composite.frag");
        using (var pass = frame.BeginPass(waterTarget, new RenderPassDescriptor { View = new(camera), State = ScreenState with { BlendEnabled = true, SourceBlend = BlendFactor.SourceAlpha, DestinationBlend = BlendFactor.OneMinusSourceAlpha }, ColorLoadAction = RenderLoadAction.Load }))
            Mesh(pass, screen, viewmodelTarget.ColorAttachments[0], viewmodelComposite);
    }

    // The world body and the camera-space arms have different poses. Keep the body
    // in world shadows, but replace its near-field caster with the actual viewmodel.
    void DrawViewmodelShadow(RenderFrame frame, Camera camera, SceneFrame snapshot, Matrix4x4 transform)
    {
        if (viewmodelShadow is null)
        {
            viewmodelShadow = Own(graphics.CreateRenderTarget(new(1024, 1024, [], TextureFormat.Depth32Float)));
            viewmodelShadow.DepthStencilAttachment.SetSampling(new(TextureFilter.Nearest, TextureFilter.Nearest,
                TextureWrap.ClampToEdge, TextureWrap.ClampToEdge));
        }
        var sun = environmentFrame.SunDirection;
        var center = camera.Position + camera.WorldForwardNormal * .5f;
        var light = new Camera { Position = center + sun * 100,
            CameraUpNormal = Math.Abs(Vector3.Dot(sun, Vector3.UnitY)) > .98f ? Vector3.UnitZ : Vector3.UnitY };
        light.LookAt(center);
        light.SetOrthogonal(-3, -3, 3, 3, .1f, 104);
        viewmodelShadowMatrix = light.View * light.Projection;
        using var pass = frame.BeginPass(viewmodelShadow, new RenderPassDescriptor {
            View = new(light), State = OpaqueState with { ColorWriteMask = ColorWriteMask.None,
                CullMode = CullMode.None, DepthBiasSlope = 1, DepthBiasConstant = 1 },
            DepthLoadAction = RenderLoadAction.Clear });
        using var timing = gpuTimer.Measure(pass, "character");
        DrawActors(pass, depthShader, ViewFrustum.FromCamera(light), snapshot, true, excludeLocal: true);
        depthShader.SetUniform("uTravelPlane", Vector4.Zero);
        using var model = pass.PushModel(transform);
        DrawSkin(pass, depthShader, rifle!, riflePose, 1, true);
    }
}
