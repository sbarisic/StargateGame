using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using System.Numerics;
namespace StargateGame.Client;

sealed partial class GameApp
{
    readonly List<(float X, float Y, string Text, float Size)> debugText = [];
    double nextDebugText;
    int debugScroll, debugLineCount;
    readonly List<double> debugHudTimes = [], dhdPickTimes = [], collisionOverlayTimes = [];
    Mesh2D? debugTextMesh;
    ShaderProgram? debugTextShader;
    FontAtlas? debugAtlas;
    Vector2 debugWindowSize, debugFramebufferSize;
    void Cache(float x, float y, string text, float size) => debugText.Add((x, y, text, size));
    void DrawDebugHud(RenderPass pass, double now)
    {
        if (!debugPhysics || session is null)
            return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (now >= nextDebugText || debugAtlas?.IsDisposed != false || debugWindowSize != new Vector2(UiWidth, UiHeight) || debugFramebufferSize != window.FramebufferSize)
        {
            nextDebugText = now + .2;
            debugText.Clear();
            if (debugPhysics && session.BuildingCounters is { } bc)
                Cache(24, 218, $"Server buildings {bc.Pieces} / pending {bc.PendingEdits} / save queue {bc.SaveQueue} / save {bc.SaveMilliseconds:F2} ms", 15);
            if (debugPhysics)
                Cache(24, 196, $"Buildings {session.Changes.Pieces.Length}  World revision {session.Changes.Revision}  Pending {session.BuildPending}  Rebuilt batches {session.Physics.RebuiltBatches}", 15);
            if (debugPhysics && scene is not null)
            {
                Cache(24, 240, $"Horizon {(eventHorizon ? "on" : "off")}  Contours {scene.HorizonObstacles}  Draws {scene.HorizonDrawCalls}  Targets {scene.HorizonTargetBytes / 1048576f:F1} MiB", 15);
                Cache(24, 262, $"Wave {scene.HorizonResolution} / steps {scene.HorizonSubsteps} / speed {scene.HorizonSettings.WaveSpeed:F2} / damping {scene.HorizonSettings.Damping:F1} / response {scene.HorizonSettings.BoundaryResponse:F1} / GPU {scene.HorizonWaveGpuMilliseconds:F3} ms", 15);
                Cache(24, 284, $"Gate lights {scene.GateLightCount}  Shadow updates {scene.GateShadowUpdates}  Atlas {scene.GateLightTargetBytes / 1048576f:F1} MiB", 15);
            }
            if (debugPhysics && audio is not null)
                Cache(24, 802, $"Audio {audio.Status} / voices {audio.Active} streams {audio.Streams} / {audio.MemoryBytes / 1048576f:F1} MiB / queries {audio.Queries} / {audio.UpdateMilliseconds:F3} ms / dropped {audio.Dropped} / underruns {audio.Stats.StreamUnderruns}", 14);
            if (debugPhysics && session.Gate is { } gateDebug)
                Cache(24, 738, $"Gate {gateDebug.Phase} rev={gateDebug.Revision} owner={gateDebug.Owner} connection={gateDebug.ConnectionId} tick={session.GateTime:F0}", 14);
            if (debugPhysics && scene is not null)
                Cache(24, 760, $"Time {scene.LocalHour:F2}h sun {scene.SunElevation:F1}deg | Sky {scene.AtmosphereGpuMilliseconds:F2} Clouds {scene.CloudGpuMilliseconds:F2} Fog {scene.FogGpuMilliseconds:F2} Water {scene.WaterGpuMilliseconds:F2}ms {(scene.RealisticWater ? "realistic" : "legacy")} | Crouch {session.Local.Crouched} Swim {session.Local.Swimming} {session.Local.Submersion:P0}", 14);
            if (debugPhysics && scene is not null)
                Cache(24, 780, $"Cloud history {scene.VolumeHistoryValid} | Volume {scene.VolumeBytes / 1048576.0:F1} MiB LUT/sky {scene.AtmosphereBytes / 1048576.0:F1} MiB | Water bodies {scene.WaterBodyCount}", 14);
            if (debugPhysics && scene is not null)
            {
                var cache = session.Destination;
                Cache(24, 824, $"Stream detailed {scene.ResidentMeshes - scene.FarMeshCount} far {scene.FarMeshCount} canonical {session.Terrain?.Count} | preload {cache?.Stage.ToString() ?? "none"} {scene.PreloadMeshes} tiles / CPU {(cache?.Bytes ?? 0) / 1048576f:F1} GPU {scene.PreloadGpuBytes / 1048576f:F1} MiB", 14);
                Cache(24, 846, $"Preload {session.PreloadBytesReceived / 1048576f:F2} MiB / prepare {cache?.Milliseconds ?? 0:F1} ms / upload {scene.PreloadUploadMilliseconds:F3} ms | far {session.FarBytesReceived / 1048576f:F2} MiB decode {session.FarDecodeMilliseconds:F1} ms", 14);
                Cache(24, 868, $"Collision reuse {session.Physics.CollisionCacheHits} / retained {session.Physics.RetainedCollisionShapes} / longest cook {session.Physics.LongestCollisionCookMilliseconds:F2} ms | sleeping water hits {session.Physics.SleepingWaterCacheHits} | server reuse {session.TerrainCounters?.CollisionCacheHits} cook {session.TerrainCounters?.CollisionCookMilliseconds:F2} ms", 14);
            }
            if (debugPhysics)
                Cache(24, 80, $"JOLT Grounded: {session.Local.Grounded} Support: {session.Local.SupportId} Tick: {session.ServerTick} | Prediction confirmations {session.PredictionConfirmations} corrections {session.PredictionCorrections}", 16);
            if (debugPhysics && scene is not null)
            {
                Cache(24, 306, $"Grass {scene.GrassRendererStatus} / {scene.GrassVolumePatches} patches / volume {scene.GrassVolumeBytes / 1048576f:F1} MiB / {scene.GrassDensity}/m2 / {scene.GrassClumps} clumps / {scene.GrassTiles} tiles ({scene.GrassDetailedTiles}/{scene.GrassSimpleTiles}/{scene.GrassFarTiles} LOD) / {scene.GrassTriangles} triangles / {scene.GrassPending} jobs / {scene.GrassUploadBytes / 1024} KiB upload / {scene.GrassBytes / 1048576f:F1} MiB / CPU {scene.GrassCpuMilliseconds:F2} / GPU {scene.GrassGpuMilliseconds:F2} ms", 15);
                Cache(24, 372, $"Trees {scene.VisibleVolumeTrees} volume / {scene.VisibleLegacyLeafTrees} old / LOD {string.Join('/', scene.CanopyLodCounts)} / {scene.CanopyDraws} draws / canopy {scene.CanopyGpuMilliseconds:F2} ms / models {scene.ModelGpuBytes / 1048576f:F0} MiB", 15);
                Cache(24, 350, $"Terrain {scene.TerrainMaterialBytes / 1048576f:F0} MiB / Clumps {scene.FoliageInstances} / Sand {scene.SandParticles} / Dust {scene.DustExtinction:F4} / Surface CPU {scene.SurfaceCpuMilliseconds + scene.FoliageCpuMilliseconds + scene.GrassCpuMilliseconds + scene.DustCpuMilliseconds:F2} ms", 15);
                Cache(24, 328, $"Snow {scene.Snowflakes} flakes / Gust {scene.Gust:F2} / Shelter updates {scene.ShelterUpdates} / {scene.WeatherBytes / 1048576f:F1} MiB / CPU {scene.WeatherCpuMilliseconds:F2} / GPU {scene.WeatherGpuMilliseconds:F2} ms", 15);
                Cache(24, 172, $"{scene.Settings.Quality}  Shadows {scene.ShadowUpdates} updates  Draws {scene.DrawCalls}  Targets {scene.TargetBytes / 1048576f:F1} MiB AO {scene.AoGpuMilliseconds:F2} ms / {scene.AoTargetBytes / 1048576f:F1} MiB canopy AO {scene.CanopyAmbientBytes / 1048576f:F2} MiB  CPU render {scene.RenderCpuMilliseconds:F2} ms  GPU {scene.RenderGpuMilliseconds:F2} ms", 15);
                Cache(24, 103, $"Chunks {scene.ResidentMeshes}  Rocks {scene.ResidentRocks}  Trees {scene.ResidentTrees}  Pending {scene.PendingMeshes}  Colliders {session.Physics.TerrainCoordinates.Count}  Epoch {session.Physics.Epoch}", 15);
                if (session.TerrainCounters is { } counters)
                    Cache(24, 149, $"Server pending {counters.Pending}  Cache {counters.Cached} chunks / {counters.CacheBytes / 1048576f:F1} MiB  Plan {counters.GenerationMilliseconds:F0} ms", 15);
                Cache(24, 126, $"Terrain received {session.TerrainBytesReceived / 1048576f:F1} MiB  GPU ~{scene.ApproximateGpuBytes / 1048576f:F1} MiB  Upload {scene.LastUploadMilliseconds:F1} ms", 15);
            }
            if (debugPhysics)
                Cache(24, 890, "Travel " + session.TransitDiagnostic, 14);
            if (scene is not null)
                Cache(24, 912, $"AgX {scene.ToneMapping.Mode} / exposure {scene.ExposureEv?.ToString("F2") ?? "pending"} / target {scene.ExposureTargetEv?.ToString("F2") ?? "pending"} EV {(scene.ExposureHeld ? "held" : "")} / meter {scene.ExposureGpuMilliseconds:F3} ms / tone {scene.ToneMapGpuMilliseconds:F3} ms", 14);

            ReflowDebugText();
            RebuildDebugText();
        }
        ui.Frame(pass, new UiRect(20, 94, Math.Min(1180, UiWidth - 40), UiHeight - 210), UiTheme.Diagnostics);
        Text(pass, 34, 104, $"DIAGNOSTICS / SCROLL TO READ  [{debugScroll + 1}/{Math.Max(1, debugLineCount)}]", 14, UiTheme.Amber);
        pass.DrawMesh(debugTextMesh!, debugAtlas!.Texture, debugTextShader);
        if (benchmark && now - connectedAt > benchmarkWarmup)
            debugHudTimes.Add(watch.Elapsed.TotalMilliseconds);
    }
    void ReflowDebugText()
    {
        var lines = new List<string>();
        float width = Math.Min(1180, UiWidth - 40) - 28;
        foreach (var source in debugText.OrderBy(t => t.Y))
        {
            string remaining = source.Text;
            while (remaining.Length > 0)
            {
                string fit = Fit(remaining, width, 14);
                int count = fit == remaining ? remaining.Length : fit.Length - 3;
                if (count <= 0)
                    break;
                lines.Add(remaining[..count]);
                remaining = remaining[count..];
            }
        }
        debugLineCount = lines.Count;
        int visible = Math.Max(1, (int)((UiHeight - 258) / 21));
        debugScroll = Math.Clamp(debugScroll, 0, Math.Max(0, lines.Count - visible));
        debugText.Clear();
        for (int row = 0; row < Math.Min(visible, lines.Count - debugScroll); row++)
            Cache(34, 136 + row * 21, lines[debugScroll + row], 14);
    }
    void RebuildDebugText()
    {
        var graphics = window.Graphics;
        debugAtlas = font.PrepareAtlas(graphics, string.Concat(debugText.Select(t => t.Text)));
        debugWindowSize = new Vector2(UiWidth, UiHeight);
        debugFramebufferSize = window.FramebufferSize;
        debugTextMesh ??= graphics.CreateMesh2D(BufferUsage.Dynamic);
        if (debugTextShader is null)
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "data", "shaders");
            using var vertex = graphics.LoadShaderStage(ShaderStageType.Vertex, Path.Combine(directory, "default2d.vert"));
            using var fragment = graphics.LoadShaderStage(ShaderStageType.Fragment, Path.Combine(directory, debugAtlas.RenderMode == FontRenderMode.SignedDistanceField ? "sdf_text.frag" : "default_tex_clr.frag"));
            debugTextShader = graphics.CreateShaderProgram(vertex, fragment);
            debugTextShader.SetUniform("uTexture", 0);
        }
        if (debugAtlas.RenderMode == FontRenderMode.SignedDistanceField)
            debugTextShader.SetUniform("uSdfPixelRange", debugAtlas.SdfPixelRange);
        var vertices = new List<Vertex2>();
        var scale = UiPixelScale;
        foreach (var line in debugText)
        {
            float size = MathF.Round(line.Size * scale.Y) / scale.Y;
            var origin = new Vector2(MathF.Round(line.X * scale.X) / scale.X, MathF.Round((UiHeight - line.Y - size) * scale.Y) / scale.Y);
            var glyphs = font.Layout(line.Text, size);
            for (int layer = 0; layer < 2; layer++)
                foreach (var glyph in glyphs)
                {
                    var p = origin + glyph.Position + (layer == 0 ? new Vector2(1 / scale.X, -1 / scale.Y) : Vector2.Zero);
                    var q = p + glyph.Size;
                    var uv = glyph.Glyph.AtlasPosition / new Vector2(debugAtlas.Width, debugAtlas.Height);
                    var uvSize = glyph.Glyph.AtlasSize / new Vector2(debugAtlas.Width, debugAtlas.Height);
                    var a = new Vector2(uv.X, 1 - uv.Y - uvSize.Y);
                    var b = new Vector2(uv.X + uvSize.X, 1 - uv.Y);
                    var color = layer == 0 ? new Color(0, 0, 0, 165) : UiTheme.Text;
                    vertices.Add(new(p, a, color));
                    vertices.Add(new(new(q.X, p.Y), new(b.X, a.Y), color));
                    vertices.Add(new(q, b, color));
                    vertices.Add(new(p, a, color));
                    vertices.Add(new(q, b, color));
                    vertices.Add(new(new(p.X, q.Y), new(a.X, b.Y), color));
                }
        }
        debugTextMesh.SetVertices(vertices.ToArray());
    }
}

