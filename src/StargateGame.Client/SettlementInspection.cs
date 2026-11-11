using System.Numerics;
using StargateGame.Shared;
using StargateGame.Physics;

namespace StargateGame.Client;

static class SettlementInspection
{
    public static IEnumerable<DebugLine> Lines(PlanetDefinition planet)
    {
        if (planet.Settlements is not { } layout)
            yield break;
        foreach (var p in layout.Plots)
        {
            var t = SettlementTemplates.Get(p.Template);
            var transform = Matrix4x4.CreateRotationY(-p.Rotation * MathF.PI / 6) * Matrix4x4.CreateTranslation(p.Position + new Vector3(0, .12f, 0));
            Vector3[] corners = [new(-t.Width / 2, 0, -t.Depth / 2), new(t.Width / 2, 0, -t.Depth / 2), new(t.Width / 2, 0, t.Depth / 2), new(-t.Width / 2, 0, t.Depth / 2)];
            for (int i = 0; i < 4; i++)
                yield return new(Vector3.Transform(corners[i], transform), Vector3.Transform(corners[(i + 1) % 4], transform), new(.2f, 1, .2f));
            foreach (var e in t.Entrances)
            {
                var v = Vector3.Transform(new(e[0], e[1], e[2]), transform);
                yield return new(v, v + new Vector3(0, 3, 0), new(1, .8f, .1f));
            }
        }
        foreach (var r in layout.Roads)
        {
            var d = Vector3.Normalize(r.B - r.A);
            var n = new Vector3(-d.Z, 0, d.X) * r.Width * .5f;
            yield return new(r.A + n + Vector3.UnitY * .2f, r.B + n + Vector3.UnitY * .2f, new(.1f, .8f, 1));
            yield return new(r.A - n + Vector3.UnitY * .2f, r.B - n + Vector3.UnitY * .2f, new(.1f, .8f, 1));
        }
        foreach (var p in layout.RejectedSites)
        {
            yield return new(p + new Vector3(-3, 1, -3), p + new Vector3(3, 1, 3), new(1, .15f, .1f));
            yield return new(p + new Vector3(3, 1, -3), p + new Vector3(-3, 1, 3), new(1, .15f, .1f));
        }
    }
}
