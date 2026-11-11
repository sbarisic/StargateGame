using StargateGame.Physics;
using StargateGame.Shared;

namespace StargateGame.Server;

public sealed partial class GameServer
{
    // Only the server tick thread mutates runtime contexts. Background generation returns data;
    // it never changes the selected context or a live physics world.
    sealed class LocationContext
    {
        public PhysicsWorld Physics = null!;
        public PlanetRuntime? Runtime;
        public readonly List<Session> Sessions = [];
        public long LastCancelledTransfer;
        public Task<Prepared>? Preparing;
        public bool CollisionPreparation;
        public CancellationTokenSource? DestinationStop;
        public Transition? Transition;
        public bool CommitThisTick, CommitWasSwitch, FreezeTick;
        public readonly Dictionary<string, BodyState[]> SavedBodies = [];
        public PendingEdit? Edit;
        public Task? SavingEdit, CrateSave, SwitchSave;
        public StargateGame.Persistence.GateAssociation? Association;
        public GateState? Gate;
        public DialPrefetch? Prefetch;
        public long LastDialTick, OwnerAwayTick, UnusedTick;
        public Task? UnloadSave;
        public bool CollisionReady = true;
        public long LoadStarted;
        public long SoundEpoch = -1, SoundWarmup;
        public readonly Dictionary<(int, int, bool), long> SoundCooldown = [];
    }
}
