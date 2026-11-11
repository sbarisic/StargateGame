using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

readonly record struct EnvironmentFrame(Vector3 SunDirection, double Phase, double UtcSeconds);
sealed partial class SceneRenderer
{
    public float? TimeOfDay
    {
        get;
        set;
    }
    public bool FreezeVisuals
    {
        get;
        set;
    }
    public bool PauseSky
    {
        get;
        set;
    }
    public bool VolumetricsEnabled
    {
        get;
        set;
    } = true;
    public bool CloudsEnabled
    {
        get;
        set;
    } = true;
    public bool WaterReflections
    {
        get;
        set;
    } = true;
    EnvironmentFrame environmentFrame;
    double? pausedEnvironment;
    RenderTarget? atmosphereTrans, atmosphereSingle, atmosphereScatter, atmosphereIrr, atmosphereSky, atmosphereLight, celestial;
    ShaderProgram? atmosphereTransShader, atmosphereDirectShader, atmosphereSingleShader, atmosphereDensityShader, atmosphereIndirectShader, atmosphereMultipleShader, atmosphereAddShader, atmosphereSkyShader, atmosphereLightShader, celestialShader;
    sealed record AtmosphereTables(RenderTarget Trans, RenderTarget Single, RenderTarget Scatter, RenderTarget Irr) : IDisposable
    {
        public void Dispose()
        {
            Trans.Dispose();
            Single.Dispose();
            Scatter.Dispose();
            Irr.Dispose();
        }
    }
    readonly Dictionary<string, AtmosphereTables> atmosphereCache = [];
    string? atmosphereCodeHash;
    void DisposeAtmosphereCache()
    {
        foreach (var value in atmosphereCache.Values)
            value.Dispose();
        atmosphereCache.Clear();
    }
    Guid atmosphereLocation;
    float aerosol = 1;
    public double LocalHour => environmentFrame.Phase * 24;
    public float SunElevation => MathF.Asin(environmentFrame.SunDirection.Y) * 180 / MathF.PI;
    public long AtmosphereBytes => atmosphereCache.Values.SelectMany(t => new[] { t.Trans, t.Single, t.Scatter, t.Irr }).Concat(new[] { atmosphereSky, atmosphereLight, celestial }).Where(t => t is not null).Sum(t => (long)t!.Width * t.Height * 8 * t.ColorAttachments.Count);
    RenderTarget AtmosphereTarget(int width, int height, int attachments = 1)
    {
        var t = graphics.CreateRenderTarget(new(width, height, Enumerable.Repeat(TextureFormat.RGBA16Float, attachments).ToArray(), null));
        foreach (var texture in t.ColorAttachments)
            texture.SetSampling(new(TextureFilter.Linear, TextureFilter.Linear, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge));
        return t;
    }
    void ClearAtmosphere()
    {
        foreach (var t in new[] { atmosphereSky, atmosphereLight, celestial })
            t?.Dispose();
        atmosphereTrans = atmosphereSingle = atmosphereScatter = atmosphereIrr = atmosphereSky = atmosphereLight = celestial = null;
        pausedEnvironment = null;
        ClearVolumes();
    }
    void AtmospherePass(RenderFrame frame, RenderView view, RenderTarget target, ShaderProgram program, params Texture[] textures)
    {
        List<IDisposable> bindings = [];
        try
        {
            for (int i = 0; i < textures.Length; i++)
                bindings.Add(textures[i].Bind((uint)(i + 1)));
            program.SetUniform("uAerosol", aerosol);
            using var pass = frame.BeginPass(target, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear });
            using (gpuTimer.Measure(pass, "atmosphere"))
                Mesh(pass, screen, white, program);
        }
        finally
        {
            foreach (var b in bindings)
                b.Dispose();
        }
    }
    void PrepareAtmosphere(RenderFrame frame, Camera camera, SceneFrame snapshot)
    {
        var definition = World.Definition.Environment ?? PlanetEnvironmentDefinition.Create(World.Definition.LocationId, World.Definition.Seed, World.Definition.Preset == "legacy" ? "temperate" : World.Definition.Preset);
        double utc = snapshot.EnvironmentSeconds > 0 ? snapshot.EnvironmentSeconds : 946684800 + snapshot.SimulationTime;
        if (PauseSky)
        {
            pausedEnvironment ??= utc;
            utc = pausedEnvironment.Value;
        }
        else
            pausedEnvironment = null;
        double phase = TimeOfDay is { } hour ? hour / 24.0 : definition.Phase(utc);
        environmentFrame = new(definition.Sun(phase), phase, utc);
        var view = new RenderView(camera);
        if (atmosphereTrans is null || atmosphereLocation != snapshot.LocationId)
        {
            ClearAtmosphere();
            atmosphereLocation = snapshot.LocationId;
            aerosol = World.Definition.Preset == "desert" ? 1.4f : .65f;
            atmosphereTransShader ??= RequestOptionalProgram("fullscreen.vert", "atmosphere_trans.frag");
            atmosphereDirectShader ??= RequestOptionalProgram("fullscreen.vert", "atmosphere_direct.frag");
            atmosphereSingleShader ??= RequestOptionalProgram("fullscreen.vert", "atmosphere_single.frag");
            atmosphereDensityShader ??= RequestOptionalProgram("fullscreen.vert", "atmosphere_density.frag");
            atmosphereIndirectShader ??= RequestOptionalProgram("fullscreen.vert", "atmosphere_indirect.frag");
            atmosphereMultipleShader ??= RequestOptionalProgram("fullscreen.vert", "atmosphere_multiple.frag");
            atmosphereAddShader ??= RequestOptionalProgram("fullscreen.vert", "atmosphere_add.frag");
            atmosphereSkyShader ??= RequestOptionalProgram("fullscreen.vert", "atmosphere_sky.frag");
            atmosphereLightShader ??= RequestOptionalProgram("fullscreen.vert", "atmosphere_light.frag");
            celestialShader ??= RequestOptionalProgram("fullscreen.vert", "celestial.frag");
            if (atmosphereTransShader is null || atmosphereDirectShader is null || atmosphereSingleShader is null || atmosphereDensityShader is null || atmosphereIndirectShader is null || atmosphereMultipleShader is null || atmosphereAddShader is null || atmosphereSkyShader is null || atmosphereLightShader is null || celestialShader is null) return;
            foreach (var p in new[] { atmosphereDirectShader, atmosphereSingleShader, atmosphereDensityShader, atmosphereIndirectShader, atmosphereMultipleShader })
            {
                p.SetUniform("uTrans", 1);
                p.SetUniform("uRay", 2);
                p.SetUniform("uMie", 3);
                p.SetUniform("uMultiple", 4);
                p.SetUniform("uIrr", 5);
                p.SetUniform("uDensity", 6);
            }
            atmosphereAddShader.SetUniform("uA", 1);
            atmosphereAddShader.SetUniform("uB", 2);
            atmosphereCodeHash ??= AtmosphereSourceHash();
            string cacheKey = $"{atmosphereCodeHash}:{aerosol:R}:16:64:16:8:4";
            bool reused = atmosphereCache.TryGetValue(cacheKey, out var tables);
            if (reused)
            {
                atmosphereTrans = tables!.Trans;
                atmosphereSingle = tables.Single;
                atmosphereScatter = tables.Scatter;
                atmosphereIrr = tables.Irr;
            }
            else
            {
                atmosphereTrans = AtmosphereTarget(256, 64);
                atmosphereSingle = AtmosphereTarget(128, 1024, 2);
                atmosphereScatter = AtmosphereTarget(128, 1024);
                atmosphereIrr = AtmosphereTarget(64, 16);
                atmosphereCache.Add(cacheKey, new(atmosphereTrans, atmosphereSingle, atmosphereScatter, atmosphereIrr));
            }
            atmosphereSky = AtmosphereTarget(512, 256);
            atmosphereLight = AtmosphereTarget(2, 1);
            celestial = AtmosphereTarget(2048, 1024);
            atmosphereSky.ColorAttachments[0].SetSampling(new(TextureFilter.Linear, TextureFilter.Linear, TextureWrap.Repeat, TextureWrap.ClampToEdge));
            celestial.ColorAttachments[0].SetSampling(new(TextureFilter.Linear, TextureFilter.Linear, TextureWrap.Repeat, TextureWrap.ClampToEdge));
            if (!reused)
            {
                using var density = AtmosphereTarget(128, 1024);
                using var multiple = AtmosphereTarget(128, 1024, 2);
                using var scratch = AtmosphereTarget(128, 1024);
                using var deltaIrr = AtmosphereTarget(64, 16);
                using var irradianceScratch = AtmosphereTarget(64, 16);
                AtmospherePass(frame, view, atmosphereTrans, atmosphereTransShader);
                AtmospherePass(frame, view, deltaIrr, atmosphereDirectShader, atmosphereTrans.ColorAttachments[0]);
                AtmospherePass(frame, view, atmosphereSingle, atmosphereSingleShader, atmosphereTrans.ColorAttachments[0]);
                atmosphereSingle.ColorAttachments[0].CopyTo(atmosphereScatter.ColorAttachments[0]);
                // Empty indirect irradiance and multiple-scattering targets are explicitly initialized.
                using (frame.BeginPass(atmosphereIrr, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear }))
                {
                }
                using (frame.BeginPass(multiple, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear }))
                {
                }
                for (int order = 2; order <= 4; order++)
                {
                    atmosphereDensityShader.SetUniform("uOrder", order);
                    atmosphereIndirectShader.SetUniform("uOrder", order);
                    Texture[] inputs = [atmosphereTrans.ColorAttachments[0], atmosphereSingle.ColorAttachments[0], atmosphereSingle.ColorAttachments[1], multiple.ColorAttachments[0], deltaIrr.ColorAttachments[0]];
                    AtmospherePass(frame, view, density, atmosphereDensityShader, inputs);
                    AtmospherePass(frame, view, irradianceScratch, atmosphereIndirectShader, inputs);
                    irradianceScratch.ColorAttachments[0].CopyTo(deltaIrr.ColorAttachments[0]);
                    AtmospherePass(frame, view, irradianceScratch, atmosphereAddShader, atmosphereIrr.ColorAttachments[0], deltaIrr.ColorAttachments[0]);
                    irradianceScratch.ColorAttachments[0].CopyTo(atmosphereIrr.ColorAttachments[0]);
                    AtmospherePass(frame, view, multiple, atmosphereMultipleShader, [inputs[0], inputs[1], inputs[2], white, inputs[4], density.ColorAttachments[0]]);
                    AtmospherePass(frame, view, scratch, atmosphereAddShader, atmosphereScatter.ColorAttachments[0], multiple.ColorAttachments[1]);
                    scratch.ColorAttachments[0].CopyTo(atmosphereScatter.ColorAttachments[0]);
                }
            }
            celestialShader.SetUniform("uSeed", (definition.CelestialSeed & 65535) * .01f);
            AtmospherePass(frame, view, celestial, celestialShader);
        }
        atmosphereSkyShader!.SetUniform("uTrans", 1);
        atmosphereSkyShader.SetUniform("uRay", 2);
        atmosphereSkyShader.SetUniform("uMie", 3);
        atmosphereSkyShader.SetUniform("uStars", 4);
        atmosphereSkyShader.SetUniform("uSunDirection", environmentFrame.SunDirection);
        atmosphereSkyShader.SetUniform("uPhase", (float)phase);
        atmosphereSkyShader.SetUniform("uAltitude", Math.Max(2, camera.Position.Y - World.Definition.Spawns[0].Y));
        atmosphereSkyShader.SetUniform("uSkyRadianceScale", AtmosphereIllumination.SkyRadianceScale(environmentFrame.SunDirection.Y, World.Definition.Preset));
        AtmospherePass(frame, view, atmosphereSky!, atmosphereSkyShader, atmosphereTrans!.ColorAttachments[0], atmosphereScatter!.ColorAttachments[0], atmosphereSingle!.ColorAttachments[1], celestial!.ColorAttachments[0]);
        atmosphereLightShader!.SetUniform("uTrans", 1);
        atmosphereLightShader.SetUniform("uIrr", 2);
        atmosphereLightShader.SetUniform("uSunDirection", environmentFrame.SunDirection);
        atmosphereLightShader.SetUniform("uTwilightFill", AtmosphereIllumination.SkyFill(environmentFrame.SunDirection.Y, World.Definition.Preset));
        atmosphereLightShader.SetUniform("uAmbientTint", AtmosphereIllumination.AmbientTint(World.Definition.Preset));
        atmosphereLightShader.SetUniform("uSkyIrradianceScale", AtmosphereIllumination.SkyIrradianceScale(environmentFrame.SunDirection.Y, World.Definition.Preset));
        AtmospherePass(frame, view, atmosphereLight!, atmosphereLightShader, atmosphereTrans.ColorAttachments[0], atmosphereIrr!.ColorAttachments[0]);
    }
}
