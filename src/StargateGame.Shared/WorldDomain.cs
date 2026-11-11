namespace StargateGame.Shared;

public static class WorldDomain
{
    public const int Size = 4096, HalfSize = Size / 2, ChunkCount = Size / ChunkCoordinate.Size,
        HalfChunks = ChunkCount / 2, ContextStride = Size * 2;
}

public enum LandscapeMode { Auto, Inland, Coastal }
public enum Habitat : byte { Meadow, ForestInterior, ForestEdge, WetBank, ExposedRock, Alpine, Beach, Arid }
public enum Geology : byte { Granite, Sandstone, Limestone }
public enum WaterKind : byte { None, River, Lake, Ocean }

// Quantized shared environmental fields, independent of rendering and chunk LOD.
public readonly record struct LandscapeEnvironment(float Moisture, float Exposure, float Soil, float Sediment,
    float Forest, Geology Geology, Habitat Habitat, float Snow);
