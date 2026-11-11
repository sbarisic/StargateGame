using System.Diagnostics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    Task<MountedAssetStore.Refresh?>? overrideRefresh;
    long nextOverrideCheck;
    string? blockedOverride;
    readonly HashSet<string> replacingModels = [];
    readonly Dictionary<string, long> modelAllocations = [];

    // Called before drawing. Completed replacements become visible only at this boundary.
    void PollDevelopmentAssets()
    {
        var store = modelAssets.Store;
        if (store?.HasDevelopment != true) return;
        if (overrideRefresh is { IsCompleted: true } task)
        {
            overrideRefresh = null;
            try
            {
                var refresh = task.GetAwaiter().GetResult();
                if (refresh is not null && refresh.Next.Generation != blockedOverride)
                {
                    var changed = refresh.Changed.ToHashSet();
                    var affected = store.Current.Assets.Concat(refresh.Next.Assets)
                        .Where(a => changed.Contains(a.Key) || a.Value.Dependencies.Any(changed.Contains)).Select(a => a.Key).Distinct().ToArray();
                    // Only static model descriptors currently have a frame-safe renderer swap.
                    // Never silently apply a collision or skeleton change to a running session.
                    bool unsupported = affected.Any(k => !k.StartsWith("models/") || !k.EndsWith(".json")
                        || !refresh.Next.Blocks.ContainsKey(k));
                    if (refresh.RequiresReload || unsupported)
                    {
                        blockedOverride = refresh.Next.Generation;
                        Console.WriteLine("ASSETS development change requires a scene/session reload. Restart the client to mount generation " + blockedOverride);
                        StartupTrace.Mark("override-reload-required", detail: blockedOverride);
                    }
                    else
                    {
                        var resume = modelJobs.Keys.ToArray();
                        foreach (var job in modelJobs.Values) job.Dispose();
                        modelJobs.Clear();
                        foreach (string key in textureRefinements.Keys.Where(k => changed.Contains(k.Split('|')[0])).ToArray())
                        {
                            textureRefinements[key].Dispose();
                            textureRefinements.Remove(key);
                            reducingTextures.Remove(key);
                        }
                        modelAssets.Publish(refresh);
                        foreach (string key in affected)
                        {
                            string name = key[7..^5];
                            if (!models.ContainsKey(name)) continue;
                            replacingModels.Add(name);
                            modelJobs[name] = UploadModel(name).GetEnumerator();
                        }
                        foreach (string name in resume) RequestModel(name);
                        StartupTrace.Mark("override-published", detail: refresh.Next.Generation);
                    }
                }
            }
            catch (Exception error)
            {
                // Failed publication/validation keeps the old snapshot drawable.
                Console.Error.WriteLine("ASSETS override refresh rejected: " + error.Message);
            }
        }
        if (overrideRefresh is null && Stopwatch.GetTimestamp() >= nextOverrideCheck)
        {
            nextOverrideCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            overrideRefresh = modelAssets.PrepareCpuAsync(store.CheckRefresh, startupStop.Token);
        }
    }

    void ReleaseModel(string name)
    {
        if (!models.Remove(name, out var parts)) return;
        foreach (var part in parts)
        {
            resources.Remove(part.mesh);
            part.mesh.Dispose();
        }
        if (modelAllocations.Remove(name, out long bytes)) modelMeshBytes -= bytes;
        modelBounds.Remove(name);
        modelBaseBounds.Remove(name);
        buildingModelCount = -1;
        buildingModelsPending = true;
    }
}
