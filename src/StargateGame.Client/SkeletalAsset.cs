using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using Pose = StargateGame.Client.AnimationPose;

namespace StargateGame.Client;

/// <summary>Versioned, immutable GPU-skinned asset; animation never owns physical movement.</summary>
sealed class SkeletalAsset : IDisposable
{
    public sealed record Part(Mesh3D Mesh, Texture Albedo, Texture? Normal, Texture? MetallicRoughness, float Lod, string Name);
    internal sealed record Clip(float Duration, int Frames, Pose[] Poses);
    readonly Dictionary<string, Clip> clips = [];
    readonly Dictionary<string, Texture> textures = [];
    public List<Part> Parts
    {
        get;
    } = [];
    public string[] Bones
    {
        get;
    }
    public int[] Parents
    {
        get;
    }
    readonly Matrix4x4[] inverseBind;
    public IEnumerable<string> Clips => clips.Keys;
    public long GpuBytes { get; private set; }
    internal sealed record CpuPart(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, Vector4[] Joints, Vector4[] Weights, uint[] Indices, string Albedo, string? Normal, string? MetallicRoughness, float Lod, string Name)
    {
        public IEnumerable<string> Textures => new[] { Albedo, Normal, MetallicRoughness }.OfType<string>().Distinct();
    }
    internal sealed record Prepared(string[] Bones, int[] Parents, Matrix4x4[] InverseBind, List<CpuPart> Parts, Dictionary<string, Clip> Clips);
    public SkeletalAsset(Prepared data)
    {
        Bones = data.Bones;
        Parents = data.Parents;
        inverseBind = data.InverseBind;
        clips = data.Clips;
    }
    public IEnumerable<bool> UploadPart(GraphicsContext graphics, CpuPart p, Func<string, Texture> textureProvider)
    {
        var mesh = graphics.CreateMesh3D();
        bool complete = false;
        long bytes = (long)p.Positions.Length * 64 + (long)p.Indices.Length * 4;
        GpuBytes += bytes;
        try
        {
            foreach (bool step in mesh.UploadGeometry(p.Positions, p.Normals, p.Uvs, p.Indices, joints: p.Joints, weights: p.Weights))
                yield return step;
            Parts.Add(new(mesh, textureProvider(p.Albedo), p.Normal is null ? null : textureProvider(p.Normal), p.MetallicRoughness is null ? null : textureProvider(p.MetallicRoughness), p.Lod, p.Name));
            complete = true;
        }
        finally
        {
            if (!complete) { mesh.Dispose(); GpuBytes -= bytes; }
        }
    }
    public static Prepared Prepare(string file, CookedAssets assets, CancellationToken cancellation)
    {
        List<CpuPart> parts = [];
        Dictionary<string, Clip> clips = [];
        using var raw = new MemoryStream(assets.Read("characters/" + Path.GetFileName(file)));
        using Stream input = assets.IsCooked ? raw : new GZipStream(raw, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(input);
        var d = doc.RootElement;
        if (d.GetProperty("version").GetInt32() != 2 || d.GetProperty("animationSpace").GetString() != "local")
            throw new InvalidDataException("Unsupported skeletal package.");
        var Bones = d.GetProperty("bones").EnumerateArray().Select(v => v.GetString()!).ToArray();
        var Parents = d.GetProperty("parents").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        if (Bones.Length is < 1 or > 128 || Parents.Length != Bones.Length || Parents.Where((p, i) => p >= i || p < -1).Any())
            throw new InvalidDataException("Invalid skeleton hierarchy.");
        var inverseBind = d.GetProperty("inverseBind").EnumerateArray().Select(ReadMatrix).ToArray();
        if (inverseBind.Length != Bones.Length || inverseBind.Any(m => !Matrix4x4.Invert(m, out _)))
            throw new InvalidDataException("Invalid inverse bind matrices.");
        string directory = Path.GetDirectoryName(file)!;
        string? Tex(JsonElement p, string key) => p.TryGetProperty(key, out var n) ? n.GetString() : null;
        {
            foreach (var p in d.GetProperty("parts").EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                float[] F(string k) => p.GetProperty(k).ValueKind == JsonValueKind.String ? MemoryMarshal.Cast<byte, float>(assets.Read(p.GetProperty(k).GetString()!)).ToArray() : p.GetProperty(k).EnumerateArray().Select(v => v.GetSingle()).ToArray();
                var pos = F("positions");
                var norm = F("normals");
                var uv = F("uvs");
                var joints = F("joints");
                var weights = F("weights");
                var indices = p.GetProperty("indices").ValueKind == JsonValueKind.String ? MemoryMarshal.Cast<byte, uint>(assets.Read(p.GetProperty("indices").GetString()!)).ToArray() : p.GetProperty("indices").EnumerateArray().Select(v => v.GetUInt32()).ToArray();
                int count = pos.Length / 3;
                if (count is < 1 or > 200000 || pos.Length != count * 3 || norm.Length != pos.Length || uv.Length != count * 2 || joints.Length != count * 4 || weights.Length != count * 4 || indices.Length % 3 != 0 || indices.Any(i => i >= count) || pos.Concat(norm).Concat(uv).Concat(weights).Any(x => !float.IsFinite(x)) || joints.Any(j => !float.IsFinite(j) || j < 0 || j >= Bones.Length || j != MathF.Floor(j)))
                    throw new InvalidDataException("Invalid skin geometry.");
                var pp = new Vector3[count];
                var nn = new Vector3[count];
                var uu = new Vector2[count];
                var jj = new Vector4[count];
                var ww = new Vector4[count];
                for (int i = 0; i < count; i++)
                {
                    pp[i] = new(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
                    nn[i] = new(norm[i * 3], norm[i * 3 + 1], norm[i * 3 + 2]);
                    uu[i] = new(uv[i * 2], uv[i * 2 + 1]);
                    jj[i] = new(joints[i * 4], joints[i * 4 + 1], joints[i * 4 + 2], joints[i * 4 + 3]);
                    ww[i] = new(weights[i * 4], weights[i * 4 + 1], weights[i * 4 + 2], weights[i * 4 + 3]);
                    if (Math.Abs(Vector4.Dot(ww[i], Vector4.One) - 1) > .001f || weights.Skip(i * 4).Take(4).Any(w => w < 0))
                        throw new InvalidDataException("Invalid skin weights.");
                }
                // Older exports retain split/flat skin normals. Repair only the bare
                // viewmodel arms, including duplicate vertices along UV seams.
                if (Path.GetFileName(file) == "retro-rifle.sga.gz" && p.GetProperty("name").GetString() == "FPS_Arms_Mesh"
                    && !(p.TryGetProperty("preserveNormals", out var authoredNormals) && authoredNormals.GetBoolean()))
                    SmoothArmNormals(pp, nn, jj, ww, indices);
                parts.Add(new(pp, nn, uu, jj, ww, indices, Tex(p, "albedo") ?? throw new InvalidDataException("Missing character albedo."), Tex(p, "normal"), Tex(p, "metallicRoughness"), p.GetProperty("lod").GetSingle(), p.GetProperty("name").GetString()!));
            }
            foreach (var c in d.GetProperty("clips").EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                int frames = c.GetProperty("frames").GetInt32();
                float duration = c.GetProperty("duration").GetSingle();
                if (frames is < 2 or > 10000 || !float.IsFinite(duration) || duration <= 0)
                    throw new InvalidDataException("Invalid clip.");
                Pose[] poses;
                if (c.GetProperty("matrices").ValueKind == JsonValueKind.String)
                {
                    var values = MemoryMarshal.Cast<byte, float>(assets.Read(c.GetProperty("matrices").GetString()!));
                    if (values.Length != frames * Bones.Length * 16)
                        throw new InvalidDataException("Invalid cooked animation length.");
                    poses = new Pose[frames * Bones.Length];
                    for (int i = 0; i < poses.Length; i++)
                        poses[i] = Pose.From(ReadMatrix(values.Slice(i * 16, 16)));
                }
                else
                    poses = c.GetProperty("matrices").EnumerateArray().Select(m => Pose.From(ReadMatrix(m))).ToArray();
                if (poses.Length != frames * Bones.Length)
                    throw new InvalidDataException("Incomplete animation.");
                clips.Add(c.GetProperty("name").GetString()!, new(duration, frames, poses));
            }
        }
        return new(Bones, Parents, inverseBind, parts, clips);
    }
    public float Duration(string name) => clips.TryGetValue(name, out var clip) ? clip.Duration : 1;
    public void AlignLocomotionOrigins()
    {
        int anchor = Array.IndexOf(Bones, "Main");
        if (anchor < 0 || Parents[anchor] >= 0 || !clips.TryGetValue("idle", out var idle))
            throw new InvalidDataException("Rifle locomotion requires its Main root and idle clip.");
        foreach (string name in new[] { "walk", "run" })
            if (clips.TryGetValue(name, out var clip))
                Pose.AlignOrigins(clip.Poses, Parents, anchor, idle.Poses[anchor].Translation);
    }
    static void SmoothArmNormals(Vector3[] positions, Vector3[] normals, Vector4[] joints, Vector4[] weights, uint[] indices)
    {
        // UV splits may duplicate a skin vertex, but its normal should remain
        // continuous. Matching influences keeps independently moving surfaces apart.
        var groups = new Dictionary<(Vector3 Position, Vector4 Joints, Vector4 Weights), int>();
        var groupIds = new int[positions.Length];
        var sums = new List<Vector3>();
        for (int i = 0; i < positions.Length; i++)
        {
            var key = (positions[i], joints[i], weights[i]);
            if (!groups.TryGetValue(key, out int group))
            {
                group = sums.Count;
                groups.Add(key, group);
                sums.Add(Vector3.Zero);
            }
            groupIds[i] = group;
        }
        void Accumulate(int centre, int first, int second, Vector3 face)
        {
            var a = positions[first] - positions[centre];
            var b = positions[second] - positions[centre];
            float lengths = a.Length() * b.Length();
            if (lengths < 1e-12f) return;
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(a, b) / lengths, -1f, 1f));
            sums[groupIds[centre]] += face * angle;
        }
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
            var face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            if (face.LengthSquared() < 1e-16f) continue;
            face = Vector3.Normalize(face);
            if (Vector3.Dot(face, normals[a] + normals[b] + normals[c]) < 0) face = -face;
            Accumulate(a, b, c, face);
            Accumulate(b, c, a, face);
            Accumulate(c, a, b, face);
        }
        for (int i = 0; i < normals.Length; i++)
            if (sums[groupIds[i]].LengthSquared() > 1e-12f)
                normals[i] = Vector3.Normalize(sums[groupIds[i]]);
    }

    static Matrix4x4 ReadMatrix(JsonElement m)
    {
        var v = m.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        if (v.Length != 16 || v.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Invalid bone matrix.");
        return ReadMatrix(v.AsSpan());
    }
    static Matrix4x4 ReadMatrix(ReadOnlySpan<float> v)
    {
        return new(v[0], v[4], v[8], v[12], v[1], v[5], v[9], v[13], v[2], v[6], v[10], v[14], v[3], v[7], v[11], v[15]);
    }
    public void Sample(string name, double seconds, Span<Matrix4x4> output, bool loop = true, AnimationTransition? transition = null, float dt = 0)
        => SampleBlend(name, seconds, null, 0, 1, output, loop, transition, dt);
    public void SampleBlend(string name, double seconds, string? previous, double previousSeconds, float blend, Span<Matrix4x4> output, bool loop = true, AnimationTransition? transition = null, float dt = 0)
    {
        var clip = clips.GetValueOrDefault(name) ?? clips["idle"];
        float t = loop ? (float)(seconds % clip.Duration) / clip.Duration : Math.Clamp((float)seconds / clip.Duration, 0, 1);
        t = Math.Max(0, t) * (clip.Frames - 1);
        int a = (int)t, b = Math.Min(a + 1, clip.Frames - 1);
        Clip? old = previous is not null ? clips.GetValueOrDefault(previous) : null;
        float oldT = old is null ? 0 : (float)(Math.Max(0, previousSeconds) % old.Duration) / old.Duration * (old.Frames - 1);
        int oldA = (int)oldT, oldB = old is null ? 0 : Math.Min(oldA + 1, old.Frames - 1);
        Span<Pose> locals = stackalloc Pose[Bones.Length];
        for (int i = 0; i < Bones.Length; i++)
        {
            var local = clip.Poses[a * Bones.Length + i].Lerp(clip.Poses[b * Bones.Length + i], t - a);
            if (old is not null && blend < 1)
            {
                var previousLocal = old.Poses[oldA * Bones.Length + i].Lerp(old.Poses[oldB * Bones.Length + i], oldT - oldA);
                local = previousLocal.Lerp(local, Math.Clamp(blend, 0, 1));
            }
            locals[i] = local;
        }
        transition?.Apply(name, locals, dt);
        Span<Matrix4x4> global = stackalloc Matrix4x4[128];
        for (int i = 0; i < Bones.Length; i++)
        {
            var local = locals[i].Matrix;
            global[i] = Parents[i] < 0 ? local : local * global[Parents[i]];
            output[i] = inverseBind[i] * global[i];
        }
    }
    public void Dispose()
    {
        foreach (var p in Parts)
            p.Mesh.Dispose();
        Parts.Clear();
        GpuBytes = 0;
        foreach (var t in textures.Values)
            t.Dispose();
        textures.Clear();
    }
}
