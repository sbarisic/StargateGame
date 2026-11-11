using System.Numerics;
using StargateGame.Shared;
using StargateGame.Physics;
using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public GateState? AuthoritativeGate
    {
        get;
        set;
    }
    public IReadOnlyDictionary<Guid, GateState>? EndpointGates
    {
        get;
        set;
    }
    GateState? StateFor(StaticEntity entity) => World.Definition.Fixture == "gate-pair" && EndpointGates is not null ?
        EndpointGates.Values.FirstOrDefault(g => entity.Model == "dhd" ? g.DhdId == GraphicsFixture.EntityId(World.Definition, entity.Id) : g.GateId == GraphicsFixture.EntityId(World.Definition, entity.Id)) : AuthoritativeGate;
    readonly struct GateScope(SceneRenderer scene, GateState? previous) : IDisposable
    {
        public void Dispose() => scene.AuthoritativeGate = previous;
    }
    GateScope SelectGate(StaticEntity entity)
    {
        var previous = AuthoritativeGate;
        AuthoritativeGate = StateFor(entity);
        return new(this, previous);
    }
    bool AnyGateSurface => InspectActiveGate || (World.Definition.Fixture == "gate-pair" ? EndpointGates?.Values.Any(g => g.Surface) == true : GateSurface);
    bool AnyGateSettled => InspectActiveGate || (World.Definition.Fixture == "gate-pair" ? EndpointGates?.Values.Any(g => g.Settled) == true : GateSettled);
    public double GateVisualTick
    {
        get;
        set;
    }
    public bool InspectActiveGate
    {
        get;
        set;
    }
    public int DhdHover
    {
        get;
        set;
    } = -1;
    int predictedKey = -1;
    double predictedAt;
    public void PredictDhdPress(int key)
    {
        predictedKey = key;
        predictedAt = GateVisualTick;
    }
    public long GateConnectionStartTick
    {
        get;
        set;
    } = -1;
    GateState? effectState;
    double effectTick = double.NaN;
    bool effectInspection;
    long effectStart = -1;
    GateEffectFrame cachedGateEffect;
    public GateEffectFrame CurrentGateEffect
    {
        get
        {
            if (effectState != AuthoritativeGate || effectTick != GateVisualTick || effectInspection != InspectActiveGate || effectStart != GateConnectionStartTick)
            {
                effectState = AuthoritativeGate;
                effectTick = GateVisualTick;
                effectInspection = InspectActiveGate;
                effectStart = GateConnectionStartTick;
                double interrupted = AuthoritativeGate is { Phase: GatePhase.Closing } closing && effectStart >= 0 ?
                    (closing.PhaseTick - effectStart - (closing.Incoming ? GateEffectTimeline.IncomingLeadTicks : 0)) / (double)GateEffectTimeline.TicksPerSecond : -1;
                cachedGateEffect = InspectActiveGate ? GateEffectTimeline.Evaluate(GatePhase.Open, 0) : AuthoritativeGate is { } g ?
                    GateEffectTimeline.Evaluate(g.Phase, (GateVisualTick - g.PhaseTick) / GateEffectTimeline.TicksPerSecond, interrupted) : default;
            }
            return cachedGateEffect;
        }
    }
    GateEffectFrame GateEffect => CurrentGateEffect;
    bool GateSurface => GateEffect.Visible;
    bool GateSettled => InspectActiveGate || AuthoritativeGate is { Settled: true };
    int ChevronCount
    {
        get
        {
            if (InspectActiveGate)
                return 7;
            if (AuthoritativeGate is null)
                return 0;
            var g = AuthoritativeGate;
            if (g.Incoming && g.Phase == GatePhase.Preparing)
                return 0;
            if (g.Incoming && g.Phase == GatePhase.Opening)
                return Math.Clamp((int)((GateVisualTick - (g.PhaseTick - GateEffectTimeline.IncomingLeadTicks)) / 6), 0, 7);
            if (g.Phase is GatePhase.Open or GatePhase.Closing)
                return 7;
            return g.AcceptedCount;
        }
    }
    // Source parts 05/06 are inactive. Part 01 is the exported top chevron.
    static readonly int[] ChevronOrder = [2, 3, 4, 7, 8, 9, 1];
    bool ChevronOn(ModelPart part) => ChevronOrder.Take(ChevronCount).Contains(part.Chevron);
    float GateEmissionScale => AuthoritativeGate is { Phase: GatePhase.Closing } && !InspectActiveGate ? GateEffect.Chevron : 1;
    float OrbStrength => (InspectActiveGate ? 1 : AuthoritativeGate is null ? 0 : AuthoritativeGate.Phase is GatePhase.Preparing or GatePhase.Opening or GatePhase.Open or GatePhase.Closing ? 1 : AuthoritativeGate.Symbols.Contains(1) ? .25f : 0) * GateEmissionScale;
    Matrix4x4 PressTransform(ModelPart part)
    {
        if (!part.DhdOrb && part.DhdKeyId == 0)
            return Matrix4x4.Identity;
        int key = part.DhdOrb ? 0 : part.DhdKeyId;
        double start = AuthoritativeGate?.PressKey == key ? AuthoritativeGate.PressTick : -1000;
        if (predictedKey == key)
            start = Math.Max(start, predictedAt);
        float age = (float)((GateVisualTick - start) / 60);
        float depression = age is >= 0 and < .12f ? MathF.Sin(age / .12f * MathF.PI) * .006f : 0;
        return Matrix4x4.CreateTranslation(0, -depression, 0);
    }
    void UpdateDialLights()
    {
        if (AuthoritativeGate is not { } gate || InspectActiveGate)
            return;
        foreach (var dhd in World.Definition.Entities.Where(e => e.Model == "dhd"))
        {
            using var endpoint = SelectGate(dhd);
            DhdGlyphs.SetGlyphs(dhd.Id, AuthoritativeGate?.Symbols ?? []);
            DhdGlyphs.SetOrb(dhd.Id, OrbStrength > 0);
        }
    }
}
