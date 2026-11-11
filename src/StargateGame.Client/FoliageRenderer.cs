using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    sealed record FoliageMesh(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs);
    sealed class FoliageSpecies : IDisposable
    {
        public int Material, Count, Capacity;
        public string Kind = "";
        public FoliageMesh[] Source = [];
        public Mesh3D[] Meshes = [];
        public Texture? Records;
        public float MaxHeight;
        public void Dispose()
        {
            foreach (var m in Meshes)
                m.Dispose();
            Meshes = [];
            Records?.Dispose();
            Records = null;
        }
    }
    FoliageSpecies[]? foliageSpecies;
    Texture[]? foliageMaterials;
    ShaderProgram? foliageShader;
    int foliageTextureSize;
    IEnumerator<bool>? foliageLoader;
    readonly Dictionary<GrassTile, FoliageInstance[]> foliageTiles = [];
    readonly Dictionary<GrassTile, (int Revision, Task<FoliageInstance[]> Task)> foliageJobs = [];
    CancellationTokenSource foliageCancellation = new();
    int foliageRevision;
    bool foliageDirty;
    Dictionary<int, FoliageInstance[]>? foliageUpload;
    int foliageUploadIndex;
    Vector3 foliageUploadEye;
    int foliageMaterialCount;
    public long FoliageBytes => (foliageMaterials is null ? 0 : (long)foliageTextureSize * foliageTextureSize * 4 * 3 * foliageMaterialCount * 4 / 3) + (foliageSpecies?.Sum(s => (long)s.Capacity * 64 + s.Source.Sum(m => (long)m.Positions.Length * 32)) ?? 0);
    public int FoliageInstances => foliageSpecies?.Sum(s => s.Count) ?? 0;
    public int FoliageTriangles
    {
        get;
        private set;
    }
    public double FoliageCpuMilliseconds
    {
        get;
        private set;
    }
    void ClearFoliage(bool preserveAssets = false)
    {
        foliageLoader?.Dispose();
        foliageLoader = null;
        foliageCancellation.Cancel();
        foliageCancellation.Dispose();
        foliageCancellation = new();
        foliageJobs.Clear();
        foliageTiles.Clear();
        foliageRevision++;
        if (preserveAssets)
        {
            foreach (var s in foliageSpecies ?? [])
                s.Count = 0;
        }
        else
        {
            foreach (var s in foliageSpecies ?? [])
                s.Dispose();
            foliageSpecies = null;
            foreach (var t in foliageMaterials ?? [])
                t.Dispose();
            foliageMaterials = null;
        }
        foliageDirty = false;
        foliageUpload = null;
        FoliageTriangles = 0;
    }
    FoliageMesh ReadFoliageMesh(string file)
    {
        var (p, n, uv) = ReadMesh(modelAssets.Read("foliage/" + file));
        return new(p, n, uv);
    }
    void LoadFoliage()
    {
        int size = Settings.Quality switch
        {
            GraphicsQuality.Low => 512,
            GraphicsQuality.High => 2048,
            _ => 1024
        };
        if (foliageTextureSize != size)
        {
            ClearFoliage();
            foliageTextureSize = size;
        }
        if (foliageSpecies is null && foliageLoader is null)
            foliageLoader = UploadFoliageAssets(size).GetEnumerator();
    }
    IEnumerable<bool> UploadFoliageAssets(int size)
    {
        byte[] data;
        while (!modelAssets.TryTake("foliage/manifest.json", out data))
            yield return false;
        using var json = JsonDocument.Parse(data);
        List<FoliageSpecies> species = [];
        List<Texture> maps = [];
        bool complete = false;
        try
        {
            foreach (var m in json.RootElement.GetProperty("models").EnumerateArray())
            {
                List<FoliageMesh> source = [];
                foreach (var lod in m.GetProperty("lods").EnumerateArray())
                {
                    while (!modelAssets.TryTake("foliage/" + lod.GetProperty("file").GetString(), out data))
                        yield return false;
                    var (p, n, uv) = ReadMesh(data);
                    source.Add(new(p, n, uv));
                    yield return true;
                }
                var s = new FoliageSpecies { Material = m.GetProperty("material").GetInt32(), Kind = m.GetProperty("kind").GetString()!, Source = source.ToArray() };
                s.MaxHeight = 1.4f / Math.Max(s.Source[0].Positions.Max(p => p.X) - s.Source[0].Positions.Min(p => p.X), s.Source[0].Positions.Max(p => p.Z) - s.Source[0].Positions.Min(p => p.Z));
                species.Add(s);
            }
            var flower=RegionalPlant(false); var wood=RegionalPlant(true);
            species.Add(new(){Material=0,Kind="flower",Source=[flower,flower],MaxHeight=.8f});
            species.Add(new(){Material=0,Kind="deadwood",Source=[wood,wood],MaxHeight=1.2f});
            int skip = 11 - (int)MathF.Log2(size);
            foliageMaterialCount = json.RootElement.GetProperty("materials").GetArrayLength();
            for (int i = 0; i < 3; i++)
            {
                maps.Add(graphics.CreateTexture(new TextureDescriptor(size, size, TextureFormat.RGBA8Unorm, dimension: TextureDimension.Texture2DArray, arrayLayers: foliageMaterialCount, mipLevels: 1 + (int)MathF.Log2(size), sampling: new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear, TextureWrap.Repeat, TextureWrap.Repeat))));
                yield return true;
            }
            int layer = 0;
            foreach (var material in json.RootElement.GetProperty("materials").EnumerateArray())
            {
                if (material.TryGetProperty("cookedMips", out var mipKeys))
                {
                    for (int mip = skip; mip < 12; mip++)
                        for (int map = 0; map < 3; map++)
                        {
                            while (!modelAssets.TryTake(mipKeys[mip][map].GetString()!, out data))
                                yield return false;
                            int side = 2048 >> mip;
                            if (data.Length != side * side * 4)
                                throw new InvalidDataException("Invalid foliage mip length.");
                            maps[map].Write<byte>(data, TextureDataFormat.RGBA8Unorm, new TextureArrayRegion(0, 0, layer, side, side, 1), mip - skip);
                            yield return true;
                        }
                }
                else
                {
                    while (!modelAssets.TryTake("foliage/" + material.GetProperty("file").GetString(), out data))
                        yield return false;
                    using var stream = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
                    using var reader = new BinaryReader(stream);
                    if (reader.ReadUInt32() != 0x31425054 || reader.ReadInt32() != 2048 || reader.ReadInt32() != 12)
                        throw new InvalidDataException("Foliage texture format");
                    for (int mip = 0; mip < 12; mip++)
                        for (int map = 0; map < 3; map++)
                        {
                            int side = 2048 >> mip;
                            byte[] pixels = reader.ReadBytes(side * side * 4);
                            if (mip >= skip)
                            {
                                maps[map].Write<byte>(pixels, TextureDataFormat.RGBA8Unorm, new TextureArrayRegion(0, 0, layer, side, side, 1), mip - skip);
                                yield return true;
                            }
                        }
                }
                layer++;
            }
            foliageShader ??= LoadProgram("foliage.vert", "foliage.frag");
            foliageSpecies = species.ToArray();
            foliageMaterials = maps.ToArray();
            complete = true;
        }
        finally
        {
            if (!complete)
            {
                foreach (var s in species)
                    s.Dispose();
                foreach (var t in maps)
                    t.Dispose();
            }
        }
    }
    void PrepareFoliage(Vector3 eye, double used)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            if (!GroundCoverSettings.Enabled || World.Definition.Preset != "temperate" || terrainStore is null)
            {
                if (foliageSpecies is not null)
                    ClearFoliage();
                return;
            }
            // GPU resources are initialized in the render path; worker preparation stays bounded.
            if (foliageSpecies is null)
                return;
            if (Vector2.DistanceSquared(new(eye.X, eye.Z), new(foliageUploadEye.X, foliageUploadEye.Z)) > 4)
                foliageDirty = true;
            var focus = GrassTile.At(eye.X, eye.Z);
            float range = Settings.Quality == GraphicsQuality.Low ? 40 : Settings.Quality == GraphicsQuality.High ? 80 : 64;
            var desired = Enumerable.Range(-11, 23).SelectMany(z => Enumerable.Range(-11, 23).Select(x => new GrassTile(focus.X + x, focus.Z + z))).Where(t => Vector2.Distance(t.Centre, new(eye.X, eye.Z)) < range + 6 && ChunkCoordinate.At(t.X * 8, t.Z * 8).Valid).OrderBy(t => Vector2.DistanceSquared(t.Centre, new(eye.X, eye.Z))).ToArray();
            var wanted = desired.ToHashSet();
            foreach (var tile in foliageTiles.Keys.Where(k => !wanted.Contains(k)).ToArray())
            {
                foliageTiles.Remove(tile);
                foliageDirty = true;
            }
            if (foliageRevision != surfaceRevision)
            {
                foliageRevision = surfaceRevision;
                foliageTiles.Clear();
                foliageDirty = true;
                foliageUpload = null;
                foreach (var s in foliageSpecies)
                    s.Count = 0;
            }
            foreach (var tile in desired)
            {
                if (foliageJobs.Count >= 2)
                    break;
                if (foliageTiles.ContainsKey(tile) || foliageJobs.ContainsKey(tile))
                    continue;
                var owner = ChunkCoordinate.At(tile.X * 8, tile.Z * 8);
                if (!surfaceMaps.TryGetValue(owner, out var field) || !terrainStore.TryGetChunk(owner, out var chunk))
                    continue;
                var obstacles = CoverObstacles(tile);
                if (obstacles is null)
                    continue;
                var token = foliageCancellation.Token;
                var planet = World.Definition;
                int grassCount = foliageSpecies.Count(s => s.Kind == "grass");
                foliageJobs[tile] = (surfaceRevision, Task.Run(() => FoliagePlacement.Build(tile, chunk, planet, field, obstacles, token, grassCount), token));
            }
            foreach (var key in foliageJobs.Keys.ToArray())
            {
                var job = foliageJobs[key];
                if (!job.Task.IsCompleted)
                    continue;
                foliageJobs.Remove(key);
                if (job.Task.IsCanceled || job.Revision != surfaceRevision || !wanted.Contains(key))
                    continue;
                foliageTiles[key] = job.Task.GetAwaiter().GetResult();
                foliageDirty = true;
            }
            // Spread uploads across frames instead of rebuilding every species in one burst.
            // Edits invalidate the snapshot; streaming changes queue another complete snapshot.
            if (foliageDirty && foliageUpload is null && clock.Elapsed.TotalMilliseconds + used < .25)
            {
                foliageUpload = foliageTiles.Values.SelectMany(v => v).GroupBy(v => v.Species).ToDictionary(g => g.Key, g => g.ToArray());
                foliageUploadEye = eye;
                foliageUploadIndex = 0;
                foliageDirty = false;
            }
            while (foliageUpload is not null && clock.Elapsed.TotalMilliseconds + used < .35)
            {
                UploadFoliageSpecies(foliageUploadIndex, foliageUpload.GetValueOrDefault(foliageUploadIndex, []));
                if (++foliageUploadIndex == foliageSpecies.Length)
                    foliageUpload = null;
            }
        }
        finally
        {
            FoliageCpuMilliseconds = clock.Elapsed.TotalMilliseconds;
        }
    }
    void UploadFoliageSpecies(int index, FoliageInstance[] instances)
    {
        var s = foliageSpecies![index];
        if (s.Kind == "grass")
        {
            float quality = Settings.Quality == GraphicsQuality.Low ? .55f : Settings.Quality == GraphicsQuality.High ? 1f : .8f;
            // Conservative eight-metre margin keeps all potentially visible ranks resident
            // while uploads follow camera movement. Exact fades stay in the vertex shader.
            instances = instances.Where(t =>
            {
                float d = Math.Max(0, Vector2.Distance(new(t.Root.X, t.Root.Z), new(foliageUploadEye.X, foliageUploadEye.Z)) - 8);
                float retention = float.Lerp(1, .20f, SurfaceEcology.Smooth(6, 16, d));
                retention = float.Lerp(retention, .025f, SurfaceEcology.Smooth(16, 32, d));
                retention = float.Lerp(retention, .005f, SurfaceEcology.Smooth(32, 48, d));
                return t.Rank < quality * retention + .012f;
            }).ToArray();
        }
        s.Count = instances.Length;
        if (s.Count == 0)
            return;
        if (s.Count > s.Capacity)
        {
            s.Records?.Dispose();
            s.Capacity = 128;
            while (s.Capacity < s.Count)
                s.Capacity *= 2;
            s.Records = graphics.CreateTexture(new TextureDescriptor(128, s.Capacity * 4 / 128, TextureFormat.RGBA32Float));
            if (s.Meshes.Length == 0)
                s.Meshes = s.Source.Select(source =>
                {
                    var mesh = graphics.CreateMesh3D();
                    mesh.SetVertices(source.Positions);
                    mesh.SetNormals(source.Normals);
                    mesh.SetUVs(source.Uvs);
                    return mesh;
                }).ToArray();
        }
        float maxHeight = s.MaxHeight;
        Vector4[] data = new Vector4[s.Capacity * 4];
        for (int i = 0; i < s.Count; i++)
        {
            var t = instances[i];
            data[i * 4] = new(t.Root, Math.Min(t.Height, maxHeight));
            data[i * 4 + 1] = new(t.Yaw, t.Rank, t.Shade, t.WindPhase);
            data[i * 4 + 2] = t.GroundMaterial;
            data[i * 4 + 3] = new(t.GroundNormal, 0);
        }
        s.Records!.Write<Vector4>(data, TextureDataFormat.RGBA32Float);
    }
    void DrawFoliage(RenderPass pass)
    {
        FoliageTriangles = 0;
        if (foliageSpecies is null || foliageShader is null || !GroundCoverSettings.Enabled)
            return;
        var p = foliageShader;
        Lighting(p);
        GateLightingUniforms(p);
        MeadowUniforms(p);
        using var terrainMaterial = BindTerrainPbr(p);
        p.SetUniform("uRecords", 1);
        p.SetUniform("uFoliageColor", 2);
        p.SetUniform("uFoliageNormal", 3);
        p.SetUniform("uFoliageMasks", 4);
        p.SetUniform("uShadowMaps", new[] { 9, 9, 9 });
        p.SetUniform("uDynamicShadowMaps", new[] { 9, 9, 9 });
        p.SetUniform("uShadowSurfaceBias", .01f);
        p.SetUniform("uDensity", Settings.Quality == GraphicsQuality.Low ? .55f : Settings.Quality == GraphicsQuality.High ? 1f : .8f);
        using (foliageMaterials![0].Bind(2))
        using (foliageMaterials[1].Bind(3))
        using (foliageMaterials[2].Bind(4))
        using (shadows.CurrentFrame.Bind(p, 9))
        using (gateShadowAtlas?.DepthStencilAttachment.Bind(15))
            foreach (var s in foliageSpecies)
            {
                if (s.Count == 0 || s.Records is null)
                    continue;
                p.SetUniform("uCount", s.Count);
                p.SetUniform("uMaterial", s.Material);
                p.SetUniform("uBend", s.Kind == "bush" ? .08f : s.Kind == "fern" ? .15f : .3f);
                p.SetUniform("uGrass", s.Kind == "grass" ? 1 : 0);
                p.SetUniform("uRegionalPlant", s.Kind=="flower"?1:s.Kind=="deadwood"?2:0);
                float range = s.Kind == "grass" ? 64 : 48;
                if (Settings.Quality == GraphicsQuality.Low)
                    range *= .65f;
                else if (Settings.Quality == GraphicsQuality.High)
                    range *= 1.25f;
                p.SetUniform("uRange", range);
                using (s.Records.Bind(1))
                    for (int lod = 0; lod < 2; lod++)
                    {
                        p.SetUniform("uDetailed", lod == 0 ? 1 : 0);
                        p.SetUniform("uVertices", s.Source[lod].Positions.Length);
                        pass.DrawMeshInstances(s.Meshes[lod], s.Count, white, p);
                        DrawCalls++;
                        FoliageTriangles += s.Source[lod].Positions.Length / 3 * s.Count;
                    }
            }
    }
}
