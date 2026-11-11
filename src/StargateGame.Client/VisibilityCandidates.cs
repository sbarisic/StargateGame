using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.Shared;
using StargateGame.WorldGen;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    // Chunk bounds form spatial buckets; each shadow frustum gets its own list.
    readonly Dictionary<ViewFrustum, VisibilitySet> visibility = [];
    readonly Stack<VisibilitySet> visibilityPool = [];
    readonly Dictionary<string, List<Matrix4x4>> visiblePrefabs = [];
    readonly Dictionary<ChunkCoordinate, (AxisAlignedBoundingBox Bounds, List<int> Indices)> prefabBuckets = [];
    sealed class VisibilitySet
    {
        public readonly List<Resident> Chunks = [];
        public readonly List<RockInstance> Rocks = [];
        public readonly List<TreeInstance> Trees = [];
        public void Clear()
        {
            Chunks.Clear();
            Rocks.Clear();
            Trees.Clear();
        }
    }
    void ResetVisibility()
    {
        foreach (var list in visibility.Values)
        {
            list.Clear();
            visibilityPool.Push(list);
        }
        visibility.Clear();
    }
    VisibilitySet Candidates(ViewFrustum frustum)
    {
        if (visibility.TryGetValue(frustum, out var list))
            return list;
        list = visibilityPool.TryPop(out var reused) ? reused : new();
        foreach (var chunk in resident.Values)
        {
            if (!frustum.Intersects(chunk.Bounds))
                continue;
            list.Chunks.Add(chunk);
            foreach (var rock in chunk.Rocks)
                if (!removedScenery.Contains(("rock", rock.Id)) && frustum.Intersects(new(rock.Min, rock.Max)))
                    list.Rocks.Add(rock);
            foreach (var tree in chunk.Trees)
                if (!removedScenery.Contains(("tree", tree.Id)) && frustum.Intersects(new(tree.Min - new Vector3(.6f), tree.Max + new Vector3(.6f))))
                    list.Trees.Add(tree);
        }
        visibility.Add(frustum, list);
        return list;
    }
    void RebuildPrefabBuckets()
    {
        prefabBuckets.Clear();
        for (int i = 0; i < buildingPrefabs.Count; i++)
        {
            var p = buildingPrefabs[i];
            var c = ChunkCoordinate.At(p.Transform.Translation.X, p.Transform.Translation.Z);
            if (prefabBuckets.TryGetValue(c, out var bucket))
            {
                bucket.Indices.Add(i);
                prefabBuckets[c] = (bucket.Bounds.Include(p.Bounds.Min).Include(p.Bounds.Max), bucket.Indices);
            }
            else
                prefabBuckets[c] = (p.Bounds, [i]);
        }
    }
}
