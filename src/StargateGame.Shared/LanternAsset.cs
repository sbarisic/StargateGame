using System.Numerics;
namespace StargateGame.Shared;

// tools/import_lantern.py bakes this bottom-pivot offset into every mesh part.
public static class LanternAsset
{
    public const string Identity = "wooden-lantern-01-cc0-rigid-v1";
    public const int LocationLimit = 8;
    public static readonly Vector3 AssetToBodyOffset = new(0, .26555f, -.00012f);
    public static readonly Vector3 HalfExtent = new(.111f, .2656f, .118f);
    public static readonly Vector3 LightPosition = new Vector3(0, .235f, -.0068f) - AssetToBodyOffset;
    // Closed housing, with three convex handle sections leaving its centre open.
    public static readonly (Vector3 Centre, Vector3 Half)[] Sections =
    [
        (new Vector3(0, .202f, -.00012f) - AssetToBodyOffset, new(.111f, .202f, .118f)),
        (new Vector3(-.079f, .444f, -.009f) - AssetToBodyOffset, new(.012f, .08f, .012f)),
        (new Vector3(.079f, .444f, -.009f) - AssetToBodyOffset, new(.012f, .08f, .012f)),
        (new Vector3(0, .519f, -.009f) - AssetToBodyOffset, new(.079f, .011f, .012f))
    ];
}
