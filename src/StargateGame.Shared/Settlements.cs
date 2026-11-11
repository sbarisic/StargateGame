using System.Numerics;
using System.Text.Json;

namespace StargateGame.Shared;

public sealed record SettlementSettings(string Type = "auto", string Style = "auto")
{
    public const int Version = 1, LayoutRevision = 4, MaxRecords = 3072;
    public static readonly string[] Types = ["auto", "wilderness", "ruins", "village", "city"];
    public static readonly string[] Styles = ["auto", "mega", "seahill", "desert", "modern"];
    public void Validate()
    {
        if (!Types.Contains(Type) || !Styles.Contains(Style))
            throw new ArgumentException("Unknown settlement type/style.");
    }
}
public sealed record SettlementDoor(string Model, float[] Position, int Rotation);
public sealed record SettlementTemplate(string Id, string Palette, string Kind, float Width, float Depth, float Height, float[][] Entrances, SettlementDoor[] Doors, float FloorHeight, bool RoofCoverage);
public static class SettlementTemplates
{
    static readonly byte[] data = Read();
    public static string Hash
    {
        get;
    } = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
    public static IReadOnlyList<SettlementTemplate> All
    {
        get;
    } = JsonSerializer.Deserialize<SettlementTemplate[]>(data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    static readonly Dictionary<string, SettlementTemplate> index = All.ToDictionary(p => p.Id);
    static byte[] Read()
    {
        using var s = typeof(SettlementTemplates).Assembly.GetManifestResourceStream("settlement-templates.json")!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }
    public static SettlementTemplate Get(string id) => index.TryGetValue(id, out var value) ? value : throw new InvalidDataException("Unknown settlement template.");
}
public sealed record SettlementPlot(int Id, int Site, string Template, Vector3 Position, int Rotation);
public sealed record SettlementRoad(Vector3 A, Vector3 B, float Width, bool Paved);
public sealed record SettlementSite(int Id, Vector3 Centre, string Kind, int Requested, int Placed);
public sealed record SettlementLayout(int Version, string Outcome, string Palette, SettlementSite[] Sites, SettlementPlot[] Plots, SettlementRoad[] Roads, Dictionary<string, int> Rejections)
{
    public Vector3[] RejectedSites
    {
        get;
        init;
    } = [];
    public static SettlementLayout Empty
    {
        get;
    } = new(1, "wilderness", "mega", [], [], [], []);
    public void Validate()
    {
        if (RejectedSites.Length > 32 || RejectedSites.Any(p => !Finite(p)) || Version != SettlementSettings.Version || Sites.Length > 4 || Plots.Length > 96 || Roads.Length > 256 || !SettlementSettings.Types.Contains(Outcome) || !SettlementSettings.Styles.Contains(Palette))
            throw new InvalidDataException("Invalid settlement layout.");
        foreach (var p in Plots)
        {
            _ = SettlementTemplates.Get(p.Template);
            if (!Finite(p.Position) || p.Rotation is < 0 or > 11)
                throw new InvalidDataException("Invalid settlement plot.");
        }
        if (Plots.Select(p => p.Id).Distinct().Count() != Plots.Length || Roads.Any(r => !Finite(r.A) || !Finite(r.B) || r.Width is < 1 or > 12))
            throw new InvalidDataException("Invalid settlement roads/IDs.");
    }
    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) && Math.Abs(p.X) <= WorldDomain.HalfSize && Math.Abs(p.Z) <= WorldDomain.HalfSize && Math.Abs(p.Y) <= 2048;
}
