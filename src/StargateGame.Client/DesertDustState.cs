using System.Numerics;
namespace StargateGame.Client;

readonly record struct DesertDustFrame(float Gust, float Extinction, Vector2 Wind, Vector2 Travel, bool Moving);
static class DesertDust
{
    public static DesertDustFrame Sample(int seed, double time, bool moving)
    {
        double phase = (uint)seed * .013, w1 = Math.Tau / 43, w2 = Math.Tau / 29;
        float gust = moving ? (float)(.5 + .3 * Math.Sin(time * w1 + phase) + .2 * Math.Sin(time * w2 + phase * .63)) : 0;
        var direction = Vector2.Normalize(new Vector2(1, .38f));
        // Exact integral of speed=2+4*gust; changing gusts never jump the field.
        double travel = moving ? 4 * time - 1.2 / w1 * Math.Cos(time * w1 + phase) - .8 / w2 * Math.Cos(time * w2 + phase * .63) : 0;
        return new(gust, .0012f + .0013f * gust, direction * (2 + 4 * gust), direction * (float)travel, moving);
    }
    public static (int Count, int Range) Budget(int quality) => quality switch { 1 => (400, 32), 2 => (2400, 64), _ => (1200, 48) };
}
