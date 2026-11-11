using System.Diagnostics;
using System.Numerics;
using FishGfx;
using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    bool ForestAtmosphere => StargateGame.Shared.GraphicsFixture.Forest(World.Definition);
    const float MistMapSize = 256;
    RenderTarget? mistGround;
    Matrix4x4 mistGroundMatrix, mistGroundInverse;
    Vector2 mistGroundCentre;
    bool mistGroundDirty = true;
    public int GroundMistUpdates
    {
        get;
        private set;
    }
    public long GroundMistBytes => (long)(mistGround?.Width ?? 0) * (mistGround?.Height ?? 0) * 4;
    public double GroundMistMapGpuMilliseconds => gpuTimer.MistMapMilliseconds;
    public double GroundMistMapCpuMilliseconds
    {
        get;
        private set;
    }

    void ClearGroundMist()
    {
        mistGround?.Dispose();
        mistGround = null;
        mistGroundDirty = true;
        GroundMistUpdates = 0;
        GroundMistMapCpuMilliseconds = 0;
    }

    void InvalidateGroundMist(AxisAlignedBoundingBox bounds)
    {
        if (bounds.Min.X <= mistGroundCentre.X + MistMapSize / 2 && bounds.Max.X >= mistGroundCentre.X - MistMapSize / 2 &&
           bounds.Min.Z <= mistGroundCentre.Y + MistMapSize / 2 && bounds.Max.Z >= mistGroundCentre.Y - MistMapSize / 2)
            mistGroundDirty = true;
    }

    void PrepareGroundMist(RenderFrame frame, Camera camera)
    {
        GroundMistUpdates = 0;
        GroundMistMapCpuMilliseconds = 0;
        if ((!weatherFrame.Snow && !ForestAtmosphere) || !VolumetricsEnabled || !WeatherSettings.GroundMistEnabled || (!ForestAtmosphere && WeatherSettings.GroundMistDensity <= 0))
        {
            if (mistGround is not null)
                ClearGroundMist();
            return;
        }
        var watch = Stopwatch.StartNew();
        int resolution = WeatherSettings.Budget((int)Settings.Quality).ShelterResolution;
        if (mistGround is null || mistGround.Width != resolution)
        {
            ClearGroundMist();
            mistGround = graphics.CreateRenderTarget(new(resolution, resolution, [], TextureFormat.Depth32Float));
            mistGround.DepthStencilAttachment.SetSampling(new(TextureFilter.Nearest, TextureFilter.Nearest, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge));
        }
        float texel = MistMapSize / resolution;
        var centre = new Vector2(MathF.Floor(camera.Position.X / texel) * texel, MathF.Floor(camera.Position.Z / texel) * texel);
        if (centre != mistGroundCentre)
        {
            mistGroundCentre = centre;
            mistGroundDirty = true;
        }
        if (mistGroundDirty)
        {
            var eye = new Vector3(centre.X, 2050, centre.Y);
            var view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitY, -Vector3.UnitZ);
            var projection = Matrix4x4.CreateOrthographic(MistMapSize, MistMapSize, .1f, 4100);
            mistGroundMatrix = view * projection;
            Matrix4x4.Invert(mistGroundMatrix, out mistGroundInverse);
            var frustum = ViewFrustum.FromMatrix(mistGroundMatrix);
            using var pass = frame.BeginPass(mistGround, new RenderPassDescriptor
            {
                View = new(view, projection, eye, new(resolution), .1f, 4100),
                State = OpaqueState,
                DepthLoadAction = RenderLoadAction.Clear
            });
            using (gpuTimer.Measure(pass, "mistmap"))
            {
                // Ground mist follows the terrain and water, never roofs or tree canopies.
                depthShader.SetUniform("uTreeVertices", 0);
                depthShader.SetUniform("uFoliageCutoff", 0f);
                if (terrain is not null)
                    Mesh(pass, terrain, white, depthShader);
                foreach (var r in resident.Values)
                    if (frustum.Intersects(r.Bounds))
                    {
                        Mesh(pass, r.Terrain, white, depthShader);
                        if (r.Water is not null)
                            Mesh(pass, r.Water, white, depthShader);
                    }
            }
            mistGroundDirty = false;
            GroundMistUpdates++;
        }
        GroundMistMapCpuMilliseconds = watch.Elapsed.TotalMilliseconds;
    }

    void GroundMistUniforms(ShaderProgram program, int textureUnit)
    {
        program.SetUniform("uMistGround", textureUnit);
        program.SetUniform("uMistGroundMatrix", mistGroundMatrix);
        program.SetUniform("uMistGroundInverse", mistGroundInverse);
        bool forest = ForestAtmosphere;
        bool enabled = (weatherFrame.Snow || forest) && VolumetricsEnabled && WeatherSettings.GroundMistEnabled && mistGround is not null;
        program.SetUniform("uForestMist", enabled && forest ? 1 : 0);
        program.SetUniform("uGroundMistDensity", enabled ? (forest ? .012f : WeatherSettings.GroundMistDensity) : 0);
        float blanket = forest ? .6f : Math.Max(.05f, WeatherSettings.GroundMistBlanketHeight);
        program.SetUniform("uMistHeights", new Vector2(blanket, forest ? 2 : Math.Max(blanket + .01f, WeatherSettings.GroundMistWispHeight)));
        // The noise lattice is periodic over 4096 m along the wind direction.
        program.SetUniform("uMistTravel", (float)((weatherFrame.Time * (forest ? .15f : WeatherSettings.GroundMistSpeed)) % 4096));
    }
}
