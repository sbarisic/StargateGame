using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed record GateLightSettings
{
    public Vector3 ChevronColor
    {
        get;
        init;
    } = new(1, .20f, .025f);
    public float ChevronIntensity
    {
        get;
        init;
    } = 1.6f;
    public float ChevronRange
    {
        get;
        init;
    } = 3;
    public Vector3 MembraneColor
    {
        get;
        init;
    } = new(.20f, .48f, 1);
    public float MembraneIntensity
    {
        get;
        init;
    } = 12;
    public float MembraneRange
    {
        get;
        init;
    } = 10;
}

sealed partial class SceneRenderer
{
    public GateLightSettings GateLights
    {
        get;
        set;
    } = new();
    public bool GateLightingEnabled
    {
        get;
        set;
    } = true;
    GateLightSettings? cachedGateLights;
    sealed record GateLight(Vector3 Position, Vector3 Color, float Range, float SourceRadius);
    readonly record struct ShadowActor(int Kind, int Id, Vector3 Position, Quaternion Rotation);
    readonly List<GateLight> gateLights = [];
    readonly List<(StaticEntity Gate, ModelPart? Part)> gateLightSources = [];
    RenderTarget? gateShadowAtlas;
    ShaderProgram? gateDepthShader, gateEmissionShader;
    readonly Matrix4x4[] gateLightMatrices = new Matrix4x4[48];
    readonly ViewFrustum[] gateLightFrusta = new ViewFrustum[48];
    readonly RenderView[] gateLightViews = new RenderView[48];
    readonly bool[] gateStaticDirty = Enumerable.Repeat(true, 48).ToArray();
    readonly ShadowActor[]?[] gateActorState = new ShadowActor[48][];
    int gateFaceResolution;
    bool gateEmissionVisible;
    StaticEntity? litGate;
    const int GateAtlasColumns = 12, GateAtlasRows = 8, GateGuard = 2;
    public int GateLightCount => gateLights.Count(l => l.Color.LengthSquared() > 0);
    public int GateShadowUpdates
    {
        get;
        private set;
    }
    public long GateLightTargetBytes => (long)(gateShadowAtlas?.Width ?? 0) * (gateShadowAtlas?.Height ?? 0) * 4;
    void DisposeGateLights()
    {
        gateShadowAtlas?.Dispose();
        gateShadowAtlas = null;
        gateLights.Clear();
        gateLightSources.Clear();
        litGate = null;
        Array.Fill(gateStaticDirty, true);
        Array.Clear(gateActorState);
        gateEmissionVisible = false;
    }
    void InvalidateGateShadows(AxisAlignedBoundingBox? bounds = null)
    {
        InvalidateWeatherShelter(bounds);
        InvalidatePointShadows(bounds);
        farSunRevision = -1;
        for (int i = 0; i < gateLights.Count * 6; i++)
            if (bounds is null || gateLightFrusta[i].Intersects(bounds.Value))
                gateStaticDirty[i] = true;
    }
    void PrepareGateLights(RenderFrame frame, Camera camera, SceneFrame snapshot, float time)
    {
        GateShadowUpdates = 0;
        var endpoints = World.Definition.Entities.Where(e => e.Model == "stargate").ToArray();
        if (!HorizonSettings.Enabled || !endpoints.Any(e => InspectActiveGate || StateFor(e) is { } s && (s.Surface || s.AcceptedCount > 0)))
        {
            DisposeGateLights();
            return;
        }
        var gate = endpoints.FirstOrDefault();
        if (gate is null)
        {
            DisposeGateLights();
            return;
        }
        var frustum = ViewFrustum.FromCamera(camera);
        var centre = EventHorizonState.Centre(gate);
        var b = modelBounds[gate.Model];
        gateEmissionVisible = endpoints.Any(e => frustum.Intersects(new(b.Min * e.Scale + e.Position, b.Max * e.Scale + e.Position)));
        float influence = GateLights.MembraneRange + (EventHorizonState.ModelRadius + KawooshShape.Length) * gate.Scale;
        if (!endpoints.Any(e => frustum.Intersects(new(EventHorizonState.Centre(e) - new Vector3(influence), EventHorizonState.Centre(e) + new Vector3(influence)))))
        {
            DisposeGateLights();
            return;
        }
        if (!GateLightingEnabled)
        {
            gateShadowAtlas?.Dispose();
            gateShadowAtlas = null;
            gateLights.Clear();
            litGate = gate;
            gateEmissionShader ??= LoadProgram("world.vert", "gate_emission.frag");
            gateEmissionShader.SetUniform("uSceneDepth", 1);
            return;
        }
        int resolution = Settings.Quality switch
        {
            GraphicsQuality.Low => 128,
            GraphicsQuality.High => 512,
            _ => 256
        };
        if (gateShadowAtlas is null || gateFaceResolution != resolution || litGate != gate || cachedGateLights != GateLights)
        {
            DisposeGateLights();
            litGate = gate;
            cachedGateLights = GateLights;
            gateFaceResolution = resolution;
            gateEmissionVisible = endpoints.Any(e => frustum.Intersects(new(b.Min * e.Scale + e.Position, b.Max * e.Scale + e.Position)));
            foreach (var e in endpoints.Take(2))
            {
                // Share the existing eight-light/shadow budget between endpoints.
                var parts = models[e.Model].Where(p => p.LightPosition.HasValue).ToArray();
                if (endpoints.Length > 1)
                    parts = parts.Where((_, i) => i % 3 == 0).Take(3).ToArray();
                foreach (var part in parts)
                {
                    gateLightSources.Add((e, part));
                    gateLights.Add(new(Vector3.Transform(part.LightPosition!.Value, e.Transform), Vector3.Zero, GateLights.ChevronRange, .22f));
                }
                gateLightSources.Add((e, null));
                gateLights.Add(new(EventHorizonState.Centre(e), Vector3.Zero, GateLights.MembraneRange, 1.2f));
            }

            int tile = resolution + GateGuard * 2;
            gateShadowAtlas = graphics.CreateRenderTarget(new(tile * GateAtlasColumns, tile * GateAtlasRows, [], TextureFormat.Depth32Float));
            gateShadowAtlas.DepthStencilAttachment.SetSampling(new(TextureFilter.Nearest, TextureFilter.Nearest, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge));
            gateDepthShader ??= LoadProgram("gate_depth.vert", "depth.frag");
            gateEmissionShader ??= LoadProgram("world.vert", "gate_emission.frag");
            gateEmissionShader.SetUniform("uSceneDepth", 1);
            for (int i = 0; i < gateLights.Count; i++)
                for (int face = 0; face < 6; face++)
                {
                    var light = gateLights[i];
                    int index = i * 6 + face;
                    var shadowView = PointShadowView(light.Position, light.Range, resolution, face);
                    var view = shadowView.View; var projection = shadowView.Projection;
                    gateLightMatrices[index] = view * projection;
                    gateLightFrusta[index] = ViewFrustum.FromMatrix(view * projection);
                    gateLightViews[index] = shadowView;
                }
        }
        for (int i = 0; i < gateLightSources.Count; i++)
        {
            var source = gateLightSources[i];
            using var endpoint = SelectGate(source.Gate);
            var effect=GateEffect;
            float surge=effect.Extension;
            var color = source.Part is { } part ? (ChevronOn(part) ? GateLights.ChevronColor * GateLights.ChevronIntensity * GateEmissionScale : Vector3.Zero) :
                GateSurface ? Vector3.Lerp(GateLights.MembraneColor,new(.55f,.75f,1),surge)*GateLights.MembraneIntensity*effect.Light*(1+2*surge) : Vector3.Zero;
            var position=source.Part is null ? EventHorizonState.Centre(source.Gate)
                +Vector3.TransformNormal(Vector3.UnitZ,source.Gate.Transform)*(KawooshShape.Length*.55f*surge) : gateLights[i].Position;
            if(position!=gateLights[i].Position)
                for(int face=0;face<6;face++)
                {
                    int index=i*6+face;
                    var view=PointShadowView(position,gateLights[i].Range,resolution,face);
                    gateLightViews[index]=view;gateLightMatrices[index]=view.View*view.Projection;
                    gateLightFrusta[index]=ViewFrustum.FromMatrix(gateLightMatrices[index]);
                    gateStaticDirty[index]=true;gateActorState[index]=null;
                }
            gateLights[i] = gateLights[i] with
            {
                Color = color, Position=position
            };
        }
        for (int face = 0; face < gateLights.Count * 6; face++)
        {
            if (gateLights[face / 6].Color.LengthSquared() == 0)
                continue;
            if (gateStaticDirty[face])
            {
                DrawGateShadowFace(frame, face, false, snapshot, time);
                gateStaticDirty[face] = false;
            }
            var visible = new List<ShadowActor>();
            var faceFrustum = gateLightFrusta[face];
            foreach (var p in snapshot.Remotes.Append(snapshot.Local).OrderBy(p => p.Id))
                if (faceFrustum.Intersects(new(p.Position - new Vector3(.4f, .01f, .4f), p.Position + new Vector3(.4f, 1.8f, .4f))))
                    visible.Add(new(0, p.Id, p.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -p.Yaw)));
            foreach (var p in snapshot.Bodies.OrderBy(p => p.Id))
                if (World.Definition.Props.FirstOrDefault(d => d.Id == p.Id)?.CastsShadows == true && faceFrustum.Intersects(new(p.Position - new Vector3(.9f), p.Position + new Vector3(.9f))))
                    visible.Add(new(1, p.Id, p.Position, p.Rotation));
            var current = visible.ToArray();
            if (gateActorState[face] is not { } previous || !previous.SequenceEqual(current))
            {
                DrawGateShadowFace(frame, face, true, snapshot, time);
                gateActorState[face] = current;
            }
        }
    }
    void DrawGateShadowFace(RenderFrame frame, int face, bool dynamic, SceneFrame snapshot, float time)
    {
        int tile = gateFaceResolution + GateGuard * 2, index = face + (dynamic ? 48 : 0), x = index % GateAtlasColumns * tile, y = index / GateAtlasColumns * tile;
        var state = OpaqueState with
        {
            ColorWriteMask = ColorWriteMask.None,
            ScissorRectangle = new(new Vector3(x, y, 0), new Vector3(x + tile, y + tile, 1)),
            DepthBiasSlope = 1.2f,
            DepthBiasConstant = 1
        };
        using var pass = frame.BeginPass(gateShadowAtlas!, new RenderPassDescriptor { View = gateLightViews[face], State = state, Time = time });
        using var timer = gpuTimer.Measure(pass);
        // Clear honours the active scissor; a load-action clear would erase the entire atlas.
        pass.Clear(Color.Black, false, true, false);
        gateDepthShader!.SetUniform("uTile", new Vector4(1f / GateAtlasColumns, 1f / GateAtlasRows, (x + tile * .5f) * 2 / gateShadowAtlas!.Width - 1, (y + tile * .5f) * 2 / gateShadowAtlas.Height - 1));
        if (dynamic)
            DrawActors(pass, gateDepthShader, gateLightFrusta[face], snapshot, true);
        else
            DrawStatic(pass, gateDepthShader, gateLightFrusta[face], true);
        GateShadowUpdates++;
    }
    void GateLightingUniforms(ShaderProgram program)
    {
        program.SetUniform("uGateLightCount", gateLights.Count);
        program.SetUniform("uGateShadowAtlas", 15);
        if (gateShadowAtlas is null)
            return;
        program.SetUniform("uGateAtlasSize", new Vector2(gateShadowAtlas.Width, gateShadowAtlas.Height));
        program.SetUniform("uGateTileSize", (float)(gateFaceResolution + GateGuard * 2));
        program.SetUniform("uGateMatrices", gateLightMatrices.AsSpan());
        for (int i = 0; i < gateLights.Count; i++)
        {
            var light = gateLights[i];
            program.SetUniform($"uGatePositionRange[{i}]", new Vector4(light.Position, light.Range));
            program.SetUniform($"uGateColorRadius[{i}]", new Vector4(light.Color, light.SourceRadius));
        }
    }
    void DrawGateEmission(RenderPass pass, bool mask)
    {
        if (!HorizonSettings.Enabled || !gateEmissionVisible || gateEmissionShader is null || litGate is null)
            return;
        Lighting(gateEmissionShader);
        gateEmissionShader.SetUniform("uEmissiveOnly", mask ? 1 : 0);
        gateEmissionShader.SetUniform("uMaskSize", new Vector2(horizonMask?.Width ?? 1, horizonMask?.Height ?? 1));
        using var state = pass.PushState(mask ? pass.State : OpaqueState with
        {
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.LessOrEqual,
            BlendEnabled = true,
            SourceBlend = BlendFactor.One,
            DestinationBlend = BlendFactor.One
        });
        foreach (var gate in World.Definition.Entities.Where(e => e.Model == "stargate"))
        {
            using var endpoint = SelectGate(gate);
            using var transform = pass.PushModel(gate.Transform);
            foreach (var part in models[gate.Model])
                if (part.Emission is not null && ChevronOn(part))
                {
                    gateEmissionShader.SetUniform("uEmission", part.EmissionColor * part.EmissionIntensity * GateEmissionScale);
                    Mesh(pass, part.mesh, part.Emission, gateEmissionShader);
                    HorizonDrawCalls++;
                }
        }
    }
}
