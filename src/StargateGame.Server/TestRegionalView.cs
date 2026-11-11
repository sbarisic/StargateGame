using System.Numerics;
using System.Globalization;
using StargateGame.WorldGen;

namespace StargateGame.Server;

public sealed partial class GameServer
{
    string? testRegionalView;
    void PrepareTestRegionalView(string preset)
    {
        if (preset == "default" || runtime is null || runtime.Definition.Fixture is not null) return;
        var plan = runtime.Generation.Plan.GetAwaiter().GetResult();
        var l = plan.Landscape;
        int peak = Array.IndexOf(l.Heights, l.Heights.Max()); var summit = RegionalLandscape.Position(peak);
        float Slope(int i) { int x = i % RegionalLandscape.Grid, z = i / RegionalLandscape.Grid; return new Vector2(l.Heights[z * RegionalLandscape.Grid + x + 1] - l.Heights[z * RegionalLandscape.Grid + x - 1], l.Heights[(z + 1) * RegionalLandscape.Grid + x] - l.Heights[(z - 1) * RegionalLandscape.Grid + x]).Length() / 16; }
        bool Match(int i) => preset switch
        {
            "mountain-meadow" => l.Forest[i] < .2f && l.Heights[i] > 65 && l.Heights[i] < 220 && Slope(i) < .16f,
            "forest-interior" => l.Forest[i] > .65f && l.Heights[i] < 220 && Slope(i) < .15f,
            "desert-mesa" => l.Sediment[i] > 3 && l.Heights[i] < 100,
            "coastline" => l.Ocean[i] == 0 && l.Heights[i] > 3 && l.Heights[i] < 9,
            "icy-highlands" => l.Heights[i] > 180 && l.Heights[i] < 300,
            _ => throw new ArgumentException("Unknown regional preset.")
        };
        var candidates = Enumerable.Range(0, RegionalLandscape.Count).Where(i => Math.Abs(RegionalLandscape.Position(i).X) < 1700 && Math.Abs(RegionalLandscape.Position(i).Y) < 1700);
        int selected = candidates.Where(Match).OrderBy(i => Math.Abs(Vector2.Distance(RegionalLandscape.Position(i), summit) - 900)).First(i => { var p = RegionalLandscape.Position(i); return plan.Sample((int)p.X, (int)p.Y).Water == int.MinValue; });
        var q = RegionalLandscape.Position(selected); float height = preset switch { "desert-mesa" => 25, "coastline" => 65, "icy-highlands" => 18, _ => 1.8f };
        float y = plan.Sample((int)q.X, (int)q.Y).Height / 1000f + height;
        Vector3 aim = preset == "forest-interior" ? new(q.X + 30, y - .2f, q.Y - 65) : new(summit.X, y + 140, summit.Y);
        if (preset == "coastline") { int water = candidates.Where(i => l.Ocean[i] != 0).MinBy(i => Vector2.DistanceSquared(RegionalLandscape.Position(i), q)); var w = RegionalLandscape.Position(water); var direction = Vector2.Normalize(w - q); aim = new(q.X + direction.X * 450, 0, q.Y + direction.Y * 450); }
        testRegionalView = string.Create(CultureInfo.InvariantCulture, $"region:{q.X},{y},{q.Y},{aim.X},{aim.Y},{aim.Z}");
    }
}
