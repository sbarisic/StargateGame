using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
namespace StargateGame.Shared;

// CPU counterpart of gate_transition.glsl. Coordinates are model metres, +Z forward.
public static class KawooshShape
{
    public const float Radius = 2.30f, Length = Radius * 2.4f;
    static float S(float a, float b, float t) => GateEffectTimeline.Smooth(a, b, t);
    static float Mix(float a, float b, float t) => a + (b - a) * t;
    static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
    {
        float v = 1 - t;
        return v * v * v * a + 3 * v * v * t * b + 3 * v * t * t * c + t * t * t * d;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector3 Floor(Vector3 p)
    {
        var f = Vector128.Floor(Vector128.Create(p.X, p.Y, p.Z, 0));
        return new(f.GetElement(0), f.GetElement(1), f.GetElement(2));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector3 Fract(Vector3 p) => p - Floor(p);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Hash(Vector3 p)
    {
        p = Fract(p * .1031f);
        p += new Vector3(Vector3.Dot(p, new Vector3(p.Y, p.Z, p.X) + new Vector3(33.33f)));
        float v = (p.X + p.Y) * p.Z;
        return v - MathF.Floor(v);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float Noise(Vector3 p)
    {
        var i = Floor(p);
        var f = p - i;
        var u = f * f * f * (f * (f * 6 - new Vector3(15)) + new Vector3(10));
        float a = Hash(i), b = Hash(i + Vector3.UnitX), c = Hash(i + Vector3.UnitY), d = Hash(i + Vector3.UnitX + Vector3.UnitY);
        float e = Hash(i + Vector3.UnitZ), g = Hash(i + Vector3.UnitZ + Vector3.UnitX), h = Hash(i + Vector3.UnitZ + Vector3.UnitY), j = Hash(i + Vector3.One);
        return Mix(Mix(Mix(a, b, u.X), Mix(c, d, u.X), u.Y), Mix(Mix(e, g, u.X), Mix(h, j, u.X), u.Y), u.Z) - .5f;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static Vector3 Position(Vector2 p, GateEffectFrame frame)
    {
        float r = Math.Clamp(p.Length() / Radius, 0, 1), s = frame.Extension, retract = frame.Retraction;
        if (s <= 0)
            return new(p, 0);
        Vector2 dir = p / Math.Max(p.Length(), .0001f), profile;
        float radial = 1, axial = .15f;
        if (r < .68f)
        {
            float a = r / .68f * 2.45f;
            (radial, axial) = MathF.SinCos(a);
            profile = new(1.67f * radial * (1 - .08f * axial), 3.10f + 1.50f * axial);
            profile.X *= 1 - .10f * retract;
            profile.Y -= retract * .9f * MathF.Exp(-MathF.Pow(r / .23f, 2));
        }
        else if (r < .90f)
        {
            profile = Bezier(new(1.132f, 1.945f), new(1.12f, 1.70f), new(1.04f, .75f), new(1.06f, .40f), (r - .68f) / .22f);
            profile.X *= 1 - retract * Mix(.10f, .50f, S(.68f, .86f, r));
        }
        else
        {
            float stem = 1 - .50f * retract;
            profile = Bezier(new(1.06f * stem, .40f), new(1.12f * stem, .16f), new(1.55f, .02f), new(Radius, 0), (r - .90f) / .10f);
        }
        Vector3 surface = new(dir * profile.X, profile.Y);
        float rim = 1 - S(.94f, 1, r), shape = S(0, .35f, s), t = frame.OpeningAge;
        float bubbles = Noise(surface * 2.2f + new Vector3(.2f, -.6f, -.8f) * t), rolling = Noise(surface * 3.6f + new Vector3(.4f, -.6f, -1.1f) * t), folds = Noise(surface * 8 + new Vector3(-.7f, .3f, 1.2f) * t);
        float bulges = (.65f * bubbles + .18f * rolling + .045f * folds) * rim * shape;
        float radius = Mix(p.Length(), profile.X, shape) + bulges * radial;
        Vector3 result = new(dir * Math.Max(radius, 0), s * (profile.Y * (Length / 4.60f) + bulges * axial));
        var wobble = s * rim * S(0, .15f, r) * new Vector2(.025f * MathF.Sin(t * 7), .035f * MathF.Sin(t * 5));
        result.X += wobble.X;
        result.Y += wobble.Y;
        return result;
    }
}
