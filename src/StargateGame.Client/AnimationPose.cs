using System.Numerics;

namespace StargateGame.Client;

internal readonly record struct AnimationPose(Vector3 Scale, Quaternion Rotation, Vector3 Translation)
{
    public static AnimationPose From(Matrix4x4 m)
    {
        if (!Matrix4x4.Decompose(m, out var s, out var q, out var t))
            throw new InvalidDataException("Invalid skeletal transform.");
        return new(s, q, t);
    }
    public AnimationPose Lerp(AnimationPose b, float t) => new(Vector3.Lerp(Scale, b.Scale, t),
        Quaternion.Slerp(Rotation, b.Rotation, t), Vector3.Lerp(Translation, b.Translation, t));
    public Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
    public Matrix4x4 Blend(AnimationPose b, float t) => Lerp(b, t).Matrix;

    // Shift both the weapon and arm roots together to preserve their grip. The
    // clip's motion relative to its initial pose is retained, without its camera offset.
    public static void AlignOrigins(Span<AnimationPose> poses, ReadOnlySpan<int> parents, int anchor, Vector3 origin)
    {
        Vector3 shift = origin - poses[anchor].Translation;
        for (int i = 0; i < poses.Length; i++)
            if (parents[i % parents.Length] < 0) poses[i] = poses[i] with { Translation = poses[i].Translation + shift };
    }
}

internal sealed class AnimationTransition
{
    readonly AnimationPose[] current = new AnimationPose[128], source = new AnimationPose[128];
    public string? Clip { get; private set; }
    public float Blend { get; private set; } = 1;
    float elapsed;
    public void Reset() => Clip = null;
    public void Apply(string clip, Span<AnimationPose> target, float dt)
    {
        if (Clip is null) { Clip = clip; elapsed = .15f; }
        else if (Clip != clip)
        {
            current.AsSpan(0, target.Length).CopyTo(source);
            Clip = clip; elapsed = 0;
        }
        else elapsed += Math.Clamp(dt, 0, .1f);
        float t = Math.Clamp(elapsed / .15f, 0, 1);
        Blend = t * t * (3 - 2 * t);
        for (int i = 0; i < target.Length; i++)
        {
            if (Blend < 1) target[i] = source[i].Lerp(target[i], Blend);
            current[i] = target[i];
        }
    }
}
