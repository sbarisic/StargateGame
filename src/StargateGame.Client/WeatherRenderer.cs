using System.Numerics;
using StargateGame.Shared;
using System.Diagnostics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public WeatherSettings WeatherSettings
    {
        get;
        set;
    } = new();
    readonly WeatherClock weatherClock = new();
    public WeatherFrame AudioWeather => weatherFrame;
    WeatherFrame weatherFrame;
    Vector2 weatherCloudOffset;
    RenderTarget? weatherShelter;
    ShaderProgram? snowShader;
    Mesh3D? snowParticles;
    Matrix4x4 shelterMatrix, shelterInverse;
    Vector2 shelterCentre;
    bool shelterDirty = true;
    int particleCapacity, shelterResolution;
    public int Snowflakes
    {
        get;
        private set;
    }
    public int ShelterUpdates
    {
        get;
        private set;
    }
    public float Gust => weatherFrame.Gust;
    public long WeatherTargetBytes => (long)(weatherShelter?.Width ?? 0) * (weatherShelter?.Height ?? 0) * 4 + GroundMistBytes;
    public long WeatherParticleBytes => (long)particleCapacity * 6 * 20;
    public long WeatherBytes => WeatherTargetBytes + WeatherParticleBytes;
    public double WeatherCpuMilliseconds
    {
        get;
        private set;
    }
    public double WeatherGpuMilliseconds => gpuTimer.WeatherMilliseconds;
    void ClearWeather()
    {
        ClearGroundMist();
        ClearDesertDust();
        weatherChecked = null;
        weatherShelter?.Dispose();
        weatherShelter = null;
        snowParticles?.Dispose();
        snowParticles = null;
        particleCapacity = 0;
        weatherClock.Clear();
        shelterDirty = true;
        Snowflakes = 0;
        weatherFrame = default;
    }
    PlanetWeather? DebugWeather => World.Definition.Environment is { DebugConfigured: true } e ? e.Weather : null;
    bool DustWeather => DebugWeather is PlanetWeather.DesertDust || (DebugWeather is null or PlanetWeather.Auto && World.Definition.Preset == "desert");
    WinterWeather ActiveWeather => DebugWeather switch {
        PlanetWeather.Clear or PlanetWeather.DesertDust => WinterWeather.Off,
        PlanetWeather.LightSnow => WinterWeather.LightSnow,
        PlanetWeather.Snowstorm => WinterWeather.Storm,
        PlanetWeather.Auto => WinterWeather.Auto,
        _ => WeatherSettings.Mode };
    void PrepareWeather(SceneFrame frame, float time)
    {
        ShelterUpdates = 0;
        WeatherCpuMilliseconds = 0;
        double clock = weatherClock.Update(frame.LocationId, frame.SimulationTime, time);
        weatherFrame = WeatherFrame.Sample(World.Definition.Preset, World.Definition.Seed, clock, ActiveWeather);
        weatherCloudOffset = Vector2.Normalize(new(1, .38f)) * (float)(WeatherFrame.WindTravel(World.Definition.Seed, clock, ActiveWeather) * .004);
        dustFrame = DustWeather ? DesertDust.Sample(World.Definition.Seed, clock, DebugWeather is PlanetWeather.DesertDust || ActiveWeather != WinterWeather.Off && WeatherSettings.DesertDustEnabled) : default;
        if (DustWeather)
        {
            weatherFrame = weatherFrame with
            {
                Gust = dustFrame.Gust,
                Wind = dustFrame.Wind
            };
            weatherCloudOffset = dustFrame.Travel * .004f;
        }
        if (!weatherFrame.Snow && (weatherShelter is not null || snowParticles is not null))
        {
            weatherShelter?.Dispose();
            weatherShelter = null;
            snowParticles?.Dispose();
            snowParticles = null;
            particleCapacity = 0;
            Snowflakes = 0;
        }
    }
    void InvalidateWeatherShelter(AxisAlignedBoundingBox? bounds = null)
    {
        if (bounds is null || Math.Abs((bounds.Value.Min.X + bounds.Value.Max.X) * .5f - shelterCentre.X) < 48 + (bounds.Value.Max.X - bounds.Value.Min.X) * .5f &&
            Math.Abs((bounds.Value.Min.Z + bounds.Value.Max.Z) * .5f - shelterCentre.Y) < 48 + (bounds.Value.Max.Z - bounds.Value.Min.Z) * .5f)
            shelterDirty = true;
    }
    void PrepareSnow(RenderFrame frame, Camera camera)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            PrepareSnowCore(frame, camera);
        }
        finally
        {
            WeatherCpuMilliseconds += watch.Elapsed.TotalMilliseconds;
        }
    }
    void PrepareSnowCore(RenderFrame frame, Camera camera)
    {
        if (!weatherFrame.Snow)
            return;
        var (count, resolution) = WeatherSettings.Budget((int)Settings.Quality);
        if (weatherShelter is null || shelterResolution != resolution)
        {
            weatherShelter?.Dispose();
            weatherShelter = graphics.CreateRenderTarget(new(resolution, resolution, [], TextureFormat.Depth32Float));
            weatherShelter.DepthStencilAttachment.SetSampling(new(TextureFilter.Nearest, TextureFilter.Nearest, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge));
            shelterResolution = resolution;
            shelterDirty = true;
        }
        var centre = new Vector2(MathF.Floor(camera.Position.X / 4) * 4, MathF.Floor(camera.Position.Z / 4) * 4);
        if (centre != shelterCentre)
        {
            shelterCentre = centre;
            shelterDirty = true;
        }
        if (shelterDirty)
        {
            var eye = new Vector3(centre.X, 2050, centre.Y);
            var view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitY, -Vector3.UnitZ);
            var projection = Matrix4x4.CreateOrthographic(88, 88, .1f, 4100);
            shelterMatrix = view * projection;
            Matrix4x4.Invert(shelterMatrix, out shelterInverse);
            var frustum = ViewFrustum.FromMatrix(shelterMatrix);
            using var pass = frame.BeginPass(weatherShelter, new RenderPassDescriptor
            {
                View = new(view, projection, eye, new(resolution, resolution), .1f, 4100),
                State = OpaqueState,
                DepthLoadAction = RenderLoadAction.Clear
            });
            using (gpuTimer.Measure(pass, "weather"))
            {
                DrawStatic(pass, depthShader, frustum, true);
                depthShader.SetUniform("uFoliageCutoff", 0f);
                foreach (var r in resident.Values)
                    if (r.Water is not null && frustum.Intersects(r.Bounds))
                        Mesh(pass, r.Water, white, depthShader);
            }
            shelterDirty = false;
            ShelterUpdates++;
        }
        if (particleCapacity != count || snowParticles is null)
        {
            snowParticles?.Dispose();
            snowParticles = graphics.CreateMesh3D();
            particleCapacity = count;
            List<Vector3> positions = [];
            List<Vector2> uv = [];
            Vector2[] corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, -1), new(1, 1), new(-1, 1)];
            for (int i = 0; i < count; i++)
            {
                float Random(int salt) => (GroundCover.Hash((uint)(i * 17 + salt)) & 0xffffff) / 16777216f;
                var seed = new Vector3(Random(1), Random(2), Random(3));
                foreach (var corner in corners)
                {
                    positions.Add(seed);
                    uv.Add(corner);
                }
            }
            snowParticles.SetVertices(positions.ToArray());
            snowParticles.SetUVs(uv.ToArray());
        }
        snowShader ??= LoadProgram("snowfall.vert", "snowfall.frag");
        snowShader.SetUniform("uSceneDepth", 1);
        snowShader.SetUniform("uShelter", 2);
        Snowflakes = (int)(count * weatherFrame.Snowfall);
    }
    static Vector2 SnowWindOffset(int seed, double time, WinterWeather mode)
    {
        double travel = WeatherFrame.WindTravel(seed, time, mode);
        var direction = Vector2.Normalize(new(1, .38f));
        return new((float)(travel * direction.X % 48), (float)(travel * direction.Y % 48));
    }
    void DrawSnow(RenderFrame frame, RenderView view, Camera camera, Matrix4x4 inverse, float time)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            DrawSnowCore(frame, view, camera, inverse, time);
        }
        finally
        {
            WeatherCpuMilliseconds += watch.Elapsed.TotalMilliseconds;
        }
    }
    void DrawSnowCore(RenderFrame frame, RenderView view, Camera camera, Matrix4x4 inverse, float time)
    {
        if (!weatherFrame.Snow || snowShader is null || snowParticles is null || weatherShelter is null)
            return;
        Matrix4x4.Invert(camera.View, out var cameraWorld);
        Lighting(snowShader);
        GateLightingUniforms(snowShader);
        snowShader.SetUniform("uShelterMatrix", shelterMatrix);
        snowShader.SetUniform("uShelterInverse", shelterInverse);
        snowShader.SetUniform("uInverseViewProjection", inverse);
        snowShader.SetUniform("uTargetSize", new Vector2(waterTarget!.Width, waterTarget.Height));
        snowShader.SetUniform("uCameraRight", new Vector3(cameraWorld.M11, cameraWorld.M12, cameraWorld.M13));
        snowShader.SetUniform("uCameraUp", new Vector3(cameraWorld.M21, cameraWorld.M22, cameraWorld.M23));
        snowShader.SetUniform("uSnowfall", weatherFrame.Snowfall);
        snowShader.SetUniform("uGust", weatherFrame.Gust);
        snowShader.SetUniform("uFallTime", (float)(weatherFrame.Time % 128));
        snowShader.SetUniform("uWindOffset", SnowWindOffset(World.Definition.Seed, weatherFrame.Time, ActiveWeather));
        snowShader.SetUniform("uDriftPhase", new Vector2((float)Math.Sin(weatherFrame.Time * .7), (float)Math.Cos(weatherFrame.Time * .3)));
        using var pass = frame.BeginPass(waterTarget, new RenderPassDescriptor
        {
            View = view,
            Time = time,
            State = OpaqueState with
            {
                DepthWriteEnabled = false,
                BlendEnabled = true,
                SourceBlend = BlendFactor.SourceAlpha,
                DestinationBlend = BlendFactor.OneMinusSourceAlpha
            },
            ColorLoadAction = RenderLoadAction.Load,
            DepthLoadAction = RenderLoadAction.Load
        });
        using (gpuTimer.Measure(pass, "weather"))
        using (opaqueTarget!.DepthStencilAttachment.Bind(1))
        using (weatherShelter.DepthStencilAttachment.Bind(2))
        using (gateShadowAtlas?.DepthStencilAttachment.Bind(15))
            Mesh(pass, snowParticles, white, snowShader);
    }
}
