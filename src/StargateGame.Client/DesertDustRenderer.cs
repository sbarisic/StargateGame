using System.Diagnostics;
using System.Numerics;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    DesertDustFrame dustFrame;
    Mesh3D? sandParticles;
    ShaderProgram? sandShader;
    int sandCapacity;
    public float DustExtinction => dustFrame.Extinction;
    public int SandParticles => dustFrame.Moving ? sandCapacity : 0;
    public double DustCpuMilliseconds
    {
        get;
        private set;
    }
    public long DustBytes => (long)sandCapacity * 6 * 20;
    void ClearDesertDust()
    {
        sandParticles?.Dispose();
        sandParticles = null;
        sandCapacity = 0;
        dustFrame = default;
    }
    void DustUniforms(ShaderProgram p)
    {
        bool desert = DustWeather;
        p.SetUniform("uDustExtinction", desert ? dustFrame.Extinction : 0);
        p.SetUniform("uDustGust", dustFrame.Gust);
        p.SetUniform("uDustMoving", desert && dustFrame.Moving ? 1f : 0f);
        p.SetUniform("uDustTravel", dustFrame.Travel);
        p.SetUniform("uDustWind", dustFrame.Wind);
    }
    void DrawDesertDust(RenderFrame frame, RenderView view, Camera camera, Matrix4x4 inverse, float time)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            if (!DustWeather || !dustFrame.Moving)
            {
                if (sandParticles is not null)
                {
                    sandParticles.Dispose();
                    sandParticles = null;
                    sandCapacity = 0;
                }
                return;
            }
            var (count, range) = DesertDust.Budget((int)Settings.Quality);
            if (sandParticles is null || sandCapacity != count)
            {
                sandParticles?.Dispose();
                sandParticles = graphics.CreateMesh3D();
                sandCapacity = count;
                Vector3[] points = new Vector3[count * 6];
                Vector2[] uv = new Vector2[count * 6];
                Vector2[] corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, -1), new(1, 1), new(-1, 1)];
                for (int i = 0; i < count; i++)
                {
                    float R(int salt) => (GroundCover.Hash((uint)(i * 17 + salt)) & 65535) / 65535f;
                    for (int v = 0; v < 6; v++)
                    {
                        points[i * 6 + v] = new(R(1), R(2), R(3));
                        uv[i * 6 + v] = corners[v];
                    }
                }
                sandParticles.SetVertices(points);
                sandParticles.SetUVs(uv);
            }
            sandShader ??= LoadProgram("desert_sand.vert", "desert_sand.frag");
            var p = sandShader;
            Lighting(p);
            InteriorUniforms(p, camera.Position);
            Matrix4x4.Invert(camera.View, out var world);
            p.SetUniform("uCameraRight", new Vector3(world.M11, world.M12, world.M13));
            p.SetUniform("uCameraUp", new Vector3(world.M21, world.M22, world.M23));
            p.SetUniform("uDustRange", (float)range);
            p.SetUniform("uDustTime", (float)(weatherFrame.Time % 10000));
            p.SetUniform("uSceneDepth", 1);
            p.SetUniform("uInverseViewProjection", inverse);
            p.SetUniform("uTargetSize", new Vector2(waterTarget!.Width, waterTarget.Height));
            using var pass = frame.BeginPass(waterTarget, new RenderPassDescriptor { View = view, Time = time, State = OpaqueState with { DepthWriteEnabled = false, BlendEnabled = true, SourceBlend = BlendFactor.SourceAlpha, DestinationBlend = BlendFactor.OneMinusSourceAlpha }, ColorLoadAction = RenderLoadAction.Load, DepthLoadAction = RenderLoadAction.Load });
            using (gpuTimer.Measure(pass, "weather"))
            using (opaqueTarget!.DepthStencilAttachment.Bind(1))
                Mesh(pass, sandParticles, white, p);
        }
        finally
        {
            DustCpuMilliseconds = clock.Elapsed.TotalMilliseconds;
        }
    }
}
