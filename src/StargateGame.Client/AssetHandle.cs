namespace StargateGame.Client;

enum AssetLoadState { Unloaded, Requested, CpuReady, Usable, Refined, Failed, Cancelled }

/// <summary>Identity survives streaming replacement; consumers can retain this handle.</summary>
sealed class AssetHandle<T>(string id) where T : class
{
    public string Id { get; } = id;
    public T? Value { get; private set; }
    public AssetLoadState State { get; private set; }
    public int References { get; set; }
    public int Priority { get; set; } = int.MaxValue;
    public long LastUsed { get; set; }
    public Exception? Failure { get; private set; }
    public void Request() { if (Value is null) { State = AssetLoadState.Requested; Failure = null; } }
    public void CpuReady() { if (Value is null) State = AssetLoadState.CpuReady; }
    public void Publish(T value) { Value = value; Failure = null; State = AssetLoadState.Usable; }
    public void Refine() { if (Value is not null) State = AssetLoadState.Refined; }
    public void Fail(Exception error) { Failure = error; if (Value is null) State = AssetLoadState.Failed; }
    public void Cancel() { if (Value is null) State = AssetLoadState.Cancelled; }
    public void Evict()
    {
        if (References != 0) throw new InvalidOperationException("Cannot evict referenced asset " + Id);
        Value = null; State = AssetLoadState.Unloaded; Priority = int.MaxValue; Failure = null;
    }
}
