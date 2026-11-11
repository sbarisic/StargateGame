using System.Numerics;
namespace StargateGame.Shared;

/// <summary>Quadtree tile origin in chunk units. Level zero covers one chunk.</summary>
public readonly record struct FarTileKey(ChunkCoordinate Origin, int Level)
{
    public int Span => 1 << Level;
    public int Size => Span * ChunkCoordinate.Size;
    public bool Valid => Level is >= 0 and <= 4 && Origin.Valid &&
        (Origin.X + WorldDomain.HalfChunks) % Span == 0 && (Origin.Z + WorldDomain.HalfChunks) % Span == 0 &&
        Origin.X + Span <= WorldDomain.HalfChunks && Origin.Z + Span <= WorldDomain.HalfChunks;
    // A render bucket key, deliberately outside the canonical chunk domain.
    public ChunkCoordinate RenderKey => new(Origin.X + (Level + 1) * 128, Origin.Z);
    public float Distance(Vector3 eye)
    {
        float x = Math.Clamp(eye.X, Origin.X*64, Origin.X*64+Size), z = Math.Clamp(eye.Z, Origin.Z*64, Origin.Z*64+Size);
        return Vector2.Distance(new(x,z), new(eye.X,eye.Z));
    }
    public static IEnumerable<FarTileKey> Cover(Vector3 eye, float detailRadius)
    {
        IEnumerable<FarTileKey> Visit(FarTileKey k)
        {
            float d = k.Distance(eye);
            if (k.Level == 0) { if (d > detailRadius) yield return k; yield break; }
            if (d > Math.Max(detailRadius + 96, k.Size * 2)) { yield return k; yield break; }
            int s = k.Span/2;
            for (int z=0;z<2;z++) for (int x=0;x<2;x++)
                foreach (var child in Visit(new(new(k.Origin.X+x*s,k.Origin.Z+z*s),k.Level-1))) yield return child;
        }
        for(int z=-WorldDomain.HalfChunks;z<WorldDomain.HalfChunks;z+=16)
            for(int x=-WorldDomain.HalfChunks;x<WorldDomain.HalfChunks;x+=16)
                foreach(var k in Visit(new(new(x,z),4))) yield return k;
    }
}
