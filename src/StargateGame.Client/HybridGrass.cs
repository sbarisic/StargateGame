using System.Numerics;
using System.Text.Json;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    sealed class GrassVolume
    {
        public Texture Color = null!, Normal = null!, Ambient = null!;
        public Mesh3D Mesh = null!;
        public int Vertices;
        public float Extent,Tip;
    }
    GrassVolume[]? grassVolumes;
    Task<Dictionary<string, byte[]>>? grassVolumeRead;
    IEnumerator<bool>? grassVolumeUpload;
    ShaderProgram? hybridGrassShader, hybridMeshShader;
    Mesh3D? grassVolumeQuad;
    readonly List<(GrassTile Key, GrassResident Tile, float Distance)> visibleGrassTiles = [];
    public int GrassVolumePatches { get; private set; }
    public long GrassVolumeBytes { get; private set; }
    public string GrassRendererStatus => GroundCoverSettings.Hybrid ? grassVolumes is null ? "Hybrid / loading (mesh fallback)" : "Hybrid" : "Mesh";

    void PrepareGrassVolumes()
    {
        if (!GroundCoverSettings.Hybrid) return;
        hybridGrassShader ??= RequestOptionalProgram("grass_volume.vert", "grass_volume.frag");
        hybridMeshShader ??= RequestOptionalProgram("grass_patch.vert", "grass_patch.frag");
        if (grassVolumes is not null) return;
        grassVolumeRead ??= Task.Run(() =>
        {
            var result = new Dictionary<string, byte[]>();
            string descriptor = "vegetation/grass-volume/grass.json";
            result[descriptor] = ReadRuntimeAsset(descriptor);
            using var doc = JsonDocument.Parse(result[descriptor]);
            if (doc.RootElement.GetProperty("version").GetInt32() != 2) throw new InvalidDataException("Unsupported grass volume version.");
            foreach (var dependency in doc.RootElement.GetProperty("dependencies").EnumerateArray())
            {
                startupStop.Token.ThrowIfCancellationRequested();
                string key = dependency.GetString()!;
                if (!key.StartsWith("vegetation/grass-volume/") || key.Contains("..")) throw new InvalidDataException("Invalid grass dependency.");
                result[key] = ReadRuntimeAsset(key);
            }
            return result;
        }, startupStop.Token);
        if (!grassVolumeRead.IsCompleted) return;
        grassVolumeUpload ??= UploadGrassVolumes(grassVolumeRead.GetAwaiter().GetResult()).GetEnumerator();
        // Bound GPU upload admissions; the first near mesh remains available throughout.
        for (int i = 0; i < 2; i++) if (!grassVolumeUpload.MoveNext()) { grassVolumeUpload.Dispose(); grassVolumeUpload = null; break; }
    }
    IEnumerable<bool> UploadGrassVolumes(Dictionary<string, byte[]> data)
    {
        using var doc = JsonDocument.Parse(data["vegetation/grass-volume/grass.json"]);
        var variants = doc.RootElement.GetProperty("variants");
        if (variants.GetArrayLength() != 8) throw new InvalidDataException("Grass requires four short and four meadow variants.");
        GrassVolume[] result = new GrassVolume[8]; long bytes = 0;
        byte[] Get(string key) => data["vegetation/grass-volume/" + key];
        for (int v = 0; v < 8; v++)
        {
            var entry = variants[v]; var volume = result[v] = new();
            volume.Extent=entry.GetProperty("extent").GetSingle(); volume.Tip=entry.GetProperty("tip").GetSingle();
            var levels = entry.GetProperty("levels");
            var descriptor = new TextureDescriptor(128, 32, TextureFormat.RGBA8Unorm, dimension: TextureDimension.Texture3D, depth: 128,
                mipLevels: 8, sampling: new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge));
            volume.Color = Own(graphics.CreateTexture(descriptor)); volume.Normal = Own(graphics.CreateTexture(descriptor));
            for (int mip = 0; mip < 8; mip++)
            {
                int w = Math.Max(1, 128 >> mip), h = Math.Max(1, 32 >> mip), d = Math.Max(1, 128 >> mip);
                for (int channel = 0; channel < 2; channel++)
                {
                    byte[] raw = Get(levels[mip].GetProperty("files")[channel].GetString()!);
                    if (raw.Length != w * h * d * 4) throw new InvalidDataException("Invalid grass mip.");
                    (channel == 0 ? volume.Color : volume.Normal).Write<byte>(raw, TextureDataFormat.RGBA8Unorm, new TextureRegion3D(0, 0, 0, w, h, d), mip);
                    bytes += raw.Length;
                }
                yield return true;
            }
            volume.Ambient = Own(graphics.CreateTexture(new TextureDescriptor(32, 8, TextureFormat.RG8Unorm, dimension: TextureDimension.Texture3D, depth: 32,
                sampling: new(TextureFilter.Linear, TextureFilter.Linear, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge))));
            byte[] ambient = Get(entry.GetProperty("ambient").GetString()!);
            if (ambient.Length != 32 * 8 * 32 * 2) throw new InvalidDataException("Invalid grass ambient field.");
            volume.Ambient.Write<byte>(ambient, TextureDataFormat.RG8Unorm); bytes += ambient.Length;
            byte[] mesh = Get(entry.GetProperty("mesh").GetString()!); int count = BitConverter.ToInt32(mesh);
            if (count <= 0 || count > 100000 || mesh.Length != count * 32 + 4) throw new InvalidDataException("Invalid grass mesh.");
            Vector3[] positions = new Vector3[count], normals = new Vector3[count]; Vector2[] uv = new Vector2[count];
            using (var reader = new BinaryReader(new MemoryStream(mesh, 4, mesh.Length - 4)))
                for (int i = 0; i < count; i++)
                { positions[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); normals[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()); uv[i] = new(reader.ReadSingle(), reader.ReadSingle()); }
            volume.Mesh = Own(graphics.CreateMesh3D()); volume.Mesh.SetVertices(positions); volume.Mesh.SetNormals(normals); volume.Mesh.SetUVs(uv); volume.Vertices = count;
            bytes += mesh.Length; yield return true;
        }
        grassVolumeQuad = Own(graphics.CreateMesh3D());
        grassVolumeQuad.SetVertices([new(-1,-1,0),new(1,-1,0),new(1,1,0),new(-1,-1,0),new(1,1,0),new(-1,1,0)]);
        GrassVolumeBytes = bytes; grassVolumes = result; data.Clear();
    }
    bool DrawHybridGrass(RenderPass pass, Camera camera, Matrix4x4 inverse, ViewFrustum frustum)
    {
        GrassVolumePatches = 0;
        if (!GroundCoverSettings.Hybrid || grassVolumes is null || hybridGrassShader is null || hybridMeshShader is null) return false;
        grassBladeColor ??= LoadTerrainTexture("grass-blades.png", "vegetation");
        var transition = HybridGrass.Transition((int)Settings.Quality);
        var (range, density) = GroundCoverSettings.EffectiveBudget((int)Settings.Quality);
        void Configure(ShaderProgram shader)
        {
            Lighting(shader); GateLightingUniforms(shader); MeadowUniforms(shader);
            shader.SetUniform("uSurface", 4); shader.SetUniform("uGrassColor", 1); shader.SetUniform("uGrassNormal", 2); shader.SetUniform("uGrassAmbient", 3);
            shader.SetUniform("uSceneDepth", 6); shader.SetUniform("uTransition", transition); shader.SetUniform("uRange", (float)range);
            shader.SetUniform("uDensity", density / 24f); shader.SetUniform("uViewPosition", camera.Position);
            shader.SetUniform("uGrassViewProjection", camera.View * camera.Projection); shader.SetUniform("uInverseViewProjection", inverse);
            shader.SetUniform("uViewport", new Vector2(opaqueTarget!.Width, opaqueTarget.Height)); shader.SetUniform("uShadowSurfaceBias", .01f);
            shader.SetUniform("uGrassGroundLayer", Math.Max(0, Array.FindIndex(terrainGrass, value => value > 0)));
        }
        Configure(hybridGrassShader); Configure(hybridMeshShader);
        using var terrain = BindTerrainPbr(hybridGrassShader);
        using var meshTerrain = BindTerrainPbr(hybridMeshShader);
        using var surfaceFieldBinding = BindSurface();
        using var depth = waterTarget!.DepthStencilAttachment.Bind(6);
        using var gates = gateShadowAtlas?.DepthStencilAttachment.Bind(15);
        using var shadow = shadows.CurrentFrame.Bind(hybridGrassShader, 9);
        // Both programs share the current shadow bindings, but their uniforms are independent.
        using var meshShadows = shadows.CurrentFrame.Bind(hybridMeshShader, 9);
        var eye = new Vector2(camera.Position.X, camera.Position.Z);
        visibleGrassTiles.Clear();
        foreach (var (key, tile) in grassTiles)
        {
            if (tile.Surface is null || tile.Count == 0 || !frustum.Intersects(tile.Bounds)) continue;
            float distance = Vector2.Distance(key.Centre, eye);
            if (distance - 6 > range) continue;
            visibleGrassTiles.Add((key, tile, distance));
        }
        // Cull before sorting; reuse storage and retain front-to-back depth rejection.
        visibleGrassTiles.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        uint salt = GroundCover.PlanetSalt(World.Definition);
        float now = (float)coverClock.Elapsed.TotalSeconds;
        foreach (var (key, tile, distance) in visibleGrassTiles)
        {
            // Height varies continuously in the surface field, not at tile edges.
            int variant = 4 + (int)(GroundCover.Hash(unchecked((uint)key.X * 73856093u ^ (uint)key.Z * 19349663u) ^ salt) % 4);
            var volume = grassVolumes[variant];
            using var surface = tile.Surface!.Bind(4);
            using var color = volume.Color.Bind(1); using var normal = volume.Normal.Bind(2); using var ambient = volume.Ambient.Bind(3);
            void TileUniforms(ShaderProgram program)
            {
                program.SetUniform("uTile", new Vector4(key.X * 8, key.Z * 8, tile.Bounds.Min.Y, tile.Bounds.Max.Y + volume.Extent));
                program.SetUniform("uGrassExtent", volume.Extent); program.SetUniform("uGrassTip", volume.Tip);
                program.SetUniform("uGrowth", Math.Clamp((now - tile.Born) / .4f, 0, 1));
            }
            if (distance - 6 < transition.Y)
            {
                TileUniforms(hybridMeshShader);
                pass.DrawMeshInstances(volume.Mesh, 16, grassBladeColor, hybridMeshShader); DrawCalls++;
                GrassTriangles += volume.Vertices / 3 * 16; GrassDetailedTiles++;
            }
            if (distance + 6 > transition.X)
            {
                TileUniforms(hybridGrassShader);
                pass.DrawMeshInstances(grassVolumeQuad!, 16, white, hybridGrassShader); DrawCalls++;
                GrassVolumePatches += 16; GrassFarTiles++;
            }
        }
        return true;
    }
}
