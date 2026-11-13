using System.Numerics;
using System.Text.Json;
using StargateGame.Physics;
using StargateGame.Shared;

static class AudioEventChecks
{
    public static void Run()
    {
        RifleAudioChecks.Run();
        var wall = new StargateGame.Client.AudioOcclusion(new[] { new StargateGame.Building.BuildTriangle(new(-2, -2, 0), new(2, -2, 0), new(2, 2, 0)), new StargateGame.Building.BuildTriangle(new(-2, -2, 0), new(2, 2, 0), new(-2, 2, 0)) });
        Verify.Check(wall.Blocked(new(0, 0, -2), new(0, 0, 2)), "solid wall obstructs audio");
        Verify.Check(wall.Blocked(new(0, 0, 2), new(0, 0, -2)), "audio obstruction is two-sided");
        Verify.Check(!wall.Blocked(new(3, 0, -2), new(3, 0, 2)), "ray beyond wall remains audible");
        Verify.Check(!new StargateGame.Client.AudioOcclusion([]).Blocked(new(0, 0, -2), new(0, 0, 2)), "open doorway has no obstruction");
        using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            listener.Start();
            using var client = new System.Net.Sockets.TcpClient();
            var connecting = client.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
            using var remote = listener.AcceptTcpClient();
            connecting.GetAwaiter().GetResult();
            using var peer = new Peer(client);
            int dropped = 0;
            for (int i = 0; i < 10000; i++)
                if (!peer.TrySendCosmetic(new()
                {
                    Kind = "world-sound"
                }))
                    dropped++;
            Verify.Check(dropped > 0 && !peer.Closed, "cosmetic overflow drops sounds without disconnecting");
            peer.Close("test completed");
            peer.Completion.GetAwaiter().GetResult();
            Verify.Check(peer.Closed, "socket writer exits cleanly during concurrent close");
        }
        var history = new SoundEventHistory();
        var location = Guid.NewGuid();
        var sound = new WorldSoundEvent(1, 10, WorldSoundKind.Impact, 1, new(1, 2, 3), .5f);
        Verify.Check(history.Accept(location, 1, sound), "sound accepts first event");
        Verify.Check(!history.Accept(location, 1, sound), "sound duplicate is silent");
        Verify.Check(history.Accept(location, 2, sound), "sound epoch resets deduplication");
        Verify.Check(!history.Accept(location, 2, sound with
        {
            Id = 2,
            Strength = float.NaN
        }), "sound rejects nonfinite strength");
        Verify.Check(!history.Accept(location, 2, sound with
        {
            Id = 2,
            Kind = (WorldSoundKind)200
        }), "sound rejects unknown cue kind");
        var message = new Message { Kind = "world-sound", LocationId = location, Epoch = 2, Sounds = [sound] };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Protocol.Json);
        Verify.Check(bytes.Length < Protocol.MaxFrameBytes && JsonSerializer.Deserialize<Message>(bytes, Protocol.Json)!.Sounds![0] == sound, "sound event bounded wire round trip");
        using var observed = new PhysicsWorld(PlanetDefinition.Default);
        using var quiet = new PhysicsWorld(PlanetDefinition.Default);
        observed.EnableSoundObservations();
        int impacts = 0;
        for (int i = 0; i < 180; i++)
        {
            observed.Step();
            quiet.Step();
            var contacts = observed.DrainSoundContacts();
            impacts += contacts.Count(c => c.Approach > .65f);
            Verify.Check(observed.Bodies().SequenceEqual(quiet.Bodies()), "audio contact observation leaves physics identical " + i);
        }
        Verify.Check(impacts > 0, "falling boxes produce observed contacts");
        Verify.Check(observed.DrainSoundContacts().Length == 0, "sound contact drain does not replay impacts");
    }
}
