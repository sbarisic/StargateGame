using System.Diagnostics;
using System.Security.Cryptography;
using StargateGame.Persistence;
using StargateGame.Shared;
using StargateGame.Physics;
namespace StargateGame.Server;

public sealed partial class GameServer
{
    readonly bool allowTestIdentities;
    readonly Guid transientUniverse = Guid.NewGuid();
    readonly Dictionary<string, SavedPlayer> transientPlayers = [];
    bool HandleIdentity(Session s, Message m)
    {
        if (m.Kind == "identity-proof")
        {
            var challenge = s.Challenge;
            s.Challenge = null;
            if (challenge is null || s.IdentityKey is null || m.IdentitySignature is null || Stopwatch.GetElapsedTime(s.ChallengeStarted).TotalSeconds > 10 || !PlayerIdentity.Verify(s.IdentityKey, challenge, m.IdentitySignature))
                return Reject(s, "Invalid or expired device proof.");
            string key = Convert.ToHexString(SHA256.HashData(s.IdentityKey));
            if (AllSessions.Any(other => other != s && !other.Peer.Closed && other.DeviceKey == key) || travels.Values.Any(t => t.Record.Player?.Key == key))
                return Reject(s, "This player identity is already connected.");
            s.DeviceKey = key;
            s.IdentityWork = repository is null ? Task.FromResult(transientPlayers.GetValueOrDefault(key) ?? new SavedPlayer(Guid.NewGuid(), key, physics.World.Definition.LocationId, new(0, s.JoinName!, physics.World.Spawn(0), default, 0, 0, false, 0), null)) : LoadIdentity(key, s.JoinName!);
            s.Peer.Send(new()
            {
                Kind = "terrain-wait",
                Reason = "Preparing saved player location."
            });
            return true;
        }
        if (m.Kind == "terrain-progress")
            return true;
        if (m.Kind != "hello" || s.IdentityKey is not null || string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 24 || m.Name.Any(char.IsControl) || m.IdentityKey is not { Length: > 0 and <= 128 })
            return Reject(s, "Invalid identity greeting.");
        if (m.IdentityProfile is not null && (!allowTestIdentities || !s.Loopback || m.IdentityProfile.Length is < 1 or > 40 || m.IdentityProfile.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
            return Reject(s, "Test identities require --allow-test-identities and a loopback connection.");
        if (m.PhysicsHash != PhysicsWorld.PhysicsHash || m.ContentHash != PhysicsWorld.ContentHash)
            return Reject(s, "Physics or world content mismatch. Install the same game build as the server.");
        if (AllSessions.Count(x => x.PlayerId != 0) >= maxPlayers)
            return Reject(s, "Server is full.");
        s.JoinName = m.Name.Trim();
        s.IdentityKey = m.IdentityKey;
        s.Challenge = new(repository?.UniverseId ?? transientUniverse, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), Protocol.Version);
        s.ChallengeStarted = Stopwatch.GetTimestamp();
        s.Peer.Send(new()
        {
            Kind = "identity-challenge",
            Challenge = s.Challenge
        });
        return true;
    }
    async Task<SavedPlayer> LoadIdentity(string key, string name)
    {
        if (await repository!.DeviceRevokedAsync(key))
            throw new InvalidOperationException("Device key revoked; contact the server administrator.");
        return await repository.AuthenticatePlayerAsync(key, homeContext.Physics.World.Definition.LocationId, name);
    }
    void TickIdentity()
    {
        foreach (var source in contexts.ToArray())
            foreach (var s in source.Sessions.ToArray())
            {
                if (s.Peer.Closed || s.RejectAt != 0)
                    continue;
                context = source;
                if (s.PlayerWrite is { IsCompleted: true } write)
                {
                    if (write.IsFaulted)
                        Console.Error.WriteLine("Player checkpoint failed: " + write.Exception!.GetBaseException().Message);
                    s.PlayerWrite = null;
                }
                if (HomeDebugBusy) continue;
                if (s.IdentityWork is not { IsCompleted: true } work)
                    continue;
                if (!work.IsCompletedSuccessfully)
                {
                    Reject(s, work.Exception?.GetBaseException().Message ?? "Player load failed.");
                    s.IdentityWork = null;
                    continue;
                }
                s.SavedPlayer ??= work.Result;
                var target = contexts.FirstOrDefault(c => c.Physics?.World.Definition.LocationId == s.SavedPlayer.Location);
                if (target is null)
                {
                    s.ResumeAssociation ??= repository!.AssociationForLocationAsync(s.SavedPlayer.Location);
                    if (!s.ResumeAssociation.IsCompleted)
                        continue;
                    if (!s.ResumeAssociation.IsCompletedSuccessfully || s.ResumeAssociation.Result is not { } association)
                    {
                        Reject(s, "Saved location is unavailable; contact the server administrator.");
                        s.IdentityWork = null;
                        continue;
                    }
                    try
                    {
                        BeginLoad(association);
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }
                    continue;
                }
                if (!target.CollisionReady || target.Transition is not null || target.Preparing is not null || target.Edit is not null)
                    continue;
                if (target.Runtime is { } runtime)
                {
                    s.ResumeChunks ??= PhysicsWorld.IncludeBuildingOwners(runtime.Changes, target.Physics.TerrainCoordinates.Concat(ChunkCoordinate.At(s.SavedPlayer.State.Position.X, s.SavedPlayer.State.Position.Z).Around(1)));
                    s.ResumeTerrain ??= Task.WhenAll(s.ResumeChunks.Select(c => runtime.Generation.RequestAsync(c, stop.Token)));
                    if (!s.ResumeTerrain.IsCompleted)
                        continue;
                    if (!s.ResumeTerrain.IsCompletedSuccessfully)
                    {
                        Reject(s, "Saved player terrain preparation failed.");
                        s.IdentityWork = null;
                        continue;
                    }
                    if (!target.Physics.PrepareTerrainBatch(s.ResumeChunks, 1))
                        continue;
                    target.Physics.SetTerrain(s.ResumeChunks);
                }
                source.Sessions.Remove(s);
                target.Sessions.Add(s);
                context = target;
                s.IdentityWork = null;
                if (target.Runtime is { } destination)
                {
                    s.JoinTransfer = ++nextTransfer;
                    SendPreparation(s, s.JoinTransfer, destination, target.Physics.TerrainCoordinates.ToArray(), false);
                }
                else
                {
                    Join(s, s.JoinName!);
                    s.JoinName = null;
                }
            }
        context = homeContext;
    }
    void SavePlayer(Session s)
    {
        if (homeRegeneration?.Save is not null) return;
        if (s.SavedPlayer is not { } saved || !physics.Players.Any(p => p.Id == s.PlayerId))
            return;
        var player = physics.Player(s.PlayerId);
        if (player.TransitFrozen)
            return;
        saved = saved with
        {
            Location = physics.World.Definition.LocationId,
            State = player,
            Appearance = characterStates.GetValueOrDefault(s.PlayerId)
        };
        s.SavedPlayer = saved;
        if (repository is null)
        {
            transientPlayers[saved.Key] = saved;
            return;
        }
        // Repository serialization preserves checkpoint ordering, including shutdown.
        s.PlayerWrite = repository.SavePlayerAsync(saved);
    }
}
