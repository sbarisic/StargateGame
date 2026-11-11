using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using StargateGame.Audio;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed class WorldAudioController : IDisposable
{
    sealed class Voice
    {
        public AudioVoiceHandle Handle;
        public string Cue = "", Loop = "";
        public Vector3 Position;
        public int Priority, Body;
        public float Gain = 1, Occlusion, MaterialCutoff = 20000;
        public double Started, Expires = double.PositiveInfinity;
    }
    AudioSystem audio;
    AudioRecording? recording;
    double nextDeviceCheck, nextDrain;
    readonly HashSet<string> diagnostics = [];
    void Log(string message)
    {
        if (diagnostics.Add(message))
            Console.WriteLine("AUDIO " + message);
    }
    readonly Dictionary<string, AudioCueDefinition> cues;
    readonly Dictionary<string, float> duration = [];
    readonly List<Voice> voices = [];
    readonly Dictionary<int, (PlayerState State, float Distance, double Stroke)> players = [];
    readonly EventHorizonState contacts = new();
    readonly HashSet<(Guid, Guid, long, string, int)> scheduled = [];
    readonly Dictionary<Guid, GateState> fixtureAudioStates = [];
    Guid location;
    long epoch = -1;
    GateState? lastGate;
    Vector3 eye, cacheCentre;
    StaticEntity? gate, dhd;
    double now, lastTime, nextQueries;
    long cacheEpoch = -1;
    int cacheTerrainCount;
    Task<AudioOcclusion>? preparingOcclusion;
    AudioOcclusion? occlusion;
    float shelter, underwater;
    int worldGeneration, cacheGeneration;
    public AudioSettings Settings
    {
        get;
    }
    public double UpdateMilliseconds
    {
        get;
        private set;
    }
    long playbackDropped, queueDropped;
    public long Dropped => playbackDropped + queueDropped;
    public int Queries
    {
        get;
        private set;
    }
    Vector3[] chevrons = [];
    public long AssetBytes
    {
        get;
        private set;
    }
    public long MemoryBytes => AssetBytes + (occlusion?.Bytes ?? 0) + 3072000;
    public double MixerCpuMilliseconds => audio.MixerCpuMilliseconds;
    public int Active => voices.Count;
    public int Streams => voices.Count(v => cues[v.Cue].Streamed);
    public AudioSystemStats Stats => audio.Stats;
    public string Status => audio.DeviceRunning ? "active" : "unavailable (silent)";
    public bool Trace
    {
        get;
        init;
    }
    public WorldAudioController(bool noDevice = false, AudioSettings? settings = null, CancellationToken cancellation = default)
    {
        Settings = settings ?? AudioSettings.Load();
        cancellation.ThrowIfCancellationRequested();
        audio = new(new()
        {
            NoDevice = noDevice,
            SampleRate = 48000,
            Channels = 2,
            MaxVoices = 128,
            Log = Log
        });
        try
        {
            try
            {
                var bank = AudioCueBank.LoadDefault();
                foreach (var cue in bank.Cues.OrderBy(c => c.Bus == AudioBus.Ui ? 0 : 1))
                {
                    cancellation.ThrowIfCancellationRequested();
                    audio.RegisterCue(cue);
                }
                cues = bank.Cues.ToDictionary(c => c.CueId);
            }
            catch (Exception e) when (e is IOException or FormatException or JsonException)
            {
                Console.WriteLine("AUDIO bank unavailable: " + e.Message);
                cues = [];
            }
            string provenance = Path.Combine(AppContext.BaseDirectory, "assets/audio/provenance.json");
            if (File.Exists(provenance))
            {
                using var data = JsonDocument.Parse(File.ReadAllText(provenance));
                foreach (var file in data.RootElement.GetProperty("files").EnumerateArray())
                {
                    duration[file.GetProperty("file").GetString()!] = file.GetProperty("duration").GetSingle();
                    AssetBytes += file.GetProperty("loop").GetBoolean() ? 384000 : file.GetProperty("frames").GetInt64() * file.GetProperty("channels").GetInt32() * 4;
                }
            }
            string model = Path.Combine(AppContext.BaseDirectory, "assets/models/stargate.json");
            if (File.Exists(model))
            {
                using var data = JsonDocument.Parse(File.ReadAllText(model));
                int[] order = [2, 3, 4, 7, 8, 9, 1];
                var positions = new Dictionary<int, Vector3>();
                foreach (var part in data.RootElement.GetProperty("parts").EnumerateArray())
                    if (part.TryGetProperty("lightPosition", out var light))
                    {
                        string name = part.GetProperty("name").GetString()!;
                        if (int.TryParse(name.Split('_')[^1], out int index))
                            positions[index] = new(light[0].GetSingle(), light[1].GetSingle(), light[2].GetSingle());
                    }
                chevrons = order.Select(i => positions.GetValueOrDefault(i, new(0, 5.6f, 0))).ToArray();
            }
            Settings.Apply(audio);
            contacts.Entered = p =>
            {
                if (gate is not null)
                    Play("gate.contact", EventHorizonState.Centre(gate) + Vector3.Transform(new Vector3(p, 0), gate.Rotation), 2);
            };
        }
        catch
        {
            audio.Dispose();
            throw;
        }
    }
    public void Record(string path) { StopRecording(); recording = new(audio, path); }
    public void StopRecording() { recording?.Dispose(); recording = null; }
    public readonly Dictionary<string, int> TestCueCounts = [];
    public void ApplySettings(bool save = true)
    {
        Settings.Apply(audio);
        if (!save)
            return;
        try
        {
            Settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log("settings: " + e.Message);
        }
    }
    public void Clear(bool preserveGateHistory = false)
    {
        audio.StopAll(fadeSeconds: .2f);
        voices.Clear();
        players.Clear();
        contacts.Clear();
        // Physics epochs also change when props are destroyed by the kawoosh. Keep
        // gate one-shot history within the same location so incoming locks do not replay.
        if (!preserveGateHistory) { scheduled.Clear(); fixtureAudioStates.Clear(); lastGate = null; }
        location = Guid.Empty;
        epoch = -1;
        occlusion = null;
        cacheEpoch = -1;
        worldGeneration++;
        shelter = underwater = 0;
    }
    public void Dispose()
    {
        recording?.Dispose();
        recording = null;
        Clear();
        audio.Dispose();
    }
    Voice? Play(string cue, Vector3 position, int priority = 1, float gain = 1, string loop = "", int body = 0, float offset = 0)
    {
        if (!cues.TryGetValue(cue, out var definition) || !audio.IsAvailable)
            return null;
        float distance = Vector3.Distance(position, eye);
        if (definition.SpatialMode == AudioSpatialMode.ThreeDimensional && distance >= definition.MaxDistance)
            return null;
        if (voices.Count >= 112)
        {
            var worst = voices.OrderBy(v => v.Priority).ThenByDescending(v => Vector3.DistanceSquared(v.Position, eye)).First();
            if (worst.Priority > priority)
            {
                playbackDropped++;
                return null;
            }
            audio.Stop(worst.Handle, .03f);
            voices.Remove(worst);
        }
        var handle = audio.PlayCue(cue, new(position, Vector3.Zero, loop.Length > 0 ? 0 : gain));
        if (!handle.IsValid)
        {
            playbackDropped++;
            return null;
        }
        if (offset > 0)
            audio.Seek(handle, offset);
        var voice = new Voice { Handle = handle, Cue = cue, Position = position, Priority = priority, Gain = gain, Loop = loop, Body = body, Started = now };
        voices.Add(voice);
        TestCueCounts[cue] = TestCueCounts.GetValueOrDefault(cue) + 1;
        if (Trace)
            Console.WriteLine($"AUDIO play {cue} location={location} offset={offset:F3} body={body}");
        return voice;
    }
    void Loop(string key, string cue, Vector3 position, float gain, int priority = 0, int body = 0, double expires = double.PositiveInfinity)
    {
        var voice = voices.FirstOrDefault(v => v.Loop == key);
        if (gain <= .001f)
        {
            if (voice is not null)
            {
                audio.Stop(voice.Handle, .35f);
                voices.Remove(voice);
            }
            return;
        }
        voice ??= Play(cue, position, priority, gain, key, body);
        if (voice is null)
            return;
        voice.Position = position;
        voice.Gain = gain;
        voice.Expires = expires;
    }
    void Timed(string cue, GateState state, double tick, int index = 0, Vector3? position = null)
    {
        if (now < 0 || currentGateTick < tick)
            return;
        var key = (state.GateId, state.ConnectionId, state.PhaseTick, cue, index);
        if (!scheduled.Add(key))
            return;
        float age = (float)((currentGateTick - tick) / 60);
        float length = cues.TryGetValue(cue, out var d) ? d.Variants.Select(v => duration.GetValueOrDefault(Path.GetFileName(v.Path), 1)).Max() : 0;
        if (age < length)
            Play(cue, position ?? EventHorizonState.Centre(gate!), 3, offset: age);
    }
    double currentGateTick;
    void GateEvents(ClientSession session, SceneRenderer scene)
    {
        if (session.World.Definition.Fixture == "gate-pair")
        {
            FixtureGateAudio(session);
            return;
        }
        while (session.AudioPresses.TryDequeue(out int key))
            Play(key == 0 ? "dhd.orb" : "dhd.press", dhd!.Position + Vector3.UnitY, 4);
        while (session.AudioGates.TryDequeue(out var entry))
        {
            var state = entry.Gate;
            if (state.LocationId != location)
                continue;
            if (entry.Baseline && state.Incoming && state.Phase == GatePhase.Opening)
                for (int i = 0; i < 7; i++)
                    if (state.PhaseTick - GateEffectTimeline.IncomingLeadTicks + (i + 1) * 6 <= session.GateTime)
                        scheduled.Add((state.GateId, state.ConnectionId, state.PhaseTick, "gate.incoming", i));
            if (!entry.Baseline && lastGate is { } before)
            {
                if (state.Symbols.Length > before.Symbols.Length && !state.Incoming)
                {
                    if (state.Owner != session.Local.Id)
                        Play("dhd.press", dhd!.Position + Vector3.UnitY, 3);
                    Play("dhd.glyph", dhd!.Position + Vector3.UnitY, 3);
                    Play("gate.chevron", ChevronPosition(state.Symbols.Length - 1), 3);
                }
                if (state.Phase == GatePhase.Preparing && before.Phase != GatePhase.Preparing && !state.Incoming && state.Owner != session.Local.Id)
                    Play("dhd.orb", dhd!.Position + Vector3.UnitY, 3);
                if (state.Phase == GatePhase.Idle && state.Status.Length > 0 && (before.Status != state.Status || before.Phase != GatePhase.Idle))
                    Play("gate.abort", EventHorizonState.Centre(gate!), 3);
            }
            lastGate = state;
        }
        var g = session.Gate;
        if (g is null)
        {
            Loop("power", "gate.power", Vector3.Zero, 0);
            Loop("hum", "gate.hum", Vector3.Zero, 0);
            return;
        }
        currentGateTick = session.GateTime;
        if (g.Phase == GatePhase.Opening)
        {
            if (g.Incoming)
                for (int i = 0; i < 7; i++)
                    Timed("gate.incoming", g, g.PhaseTick - GateEffectTimeline.IncomingLeadTicks + (i + 1) * 6, i, ChevronPosition(i));
            Timed("gate.open", g, g.PhaseTick);
        }
        if (g.Phase == GatePhase.Closing)
            Timed("gate.close", g, g.PhaseTick);
        var effect = scene.CurrentGateEffect;
        Loop("power", "gate.power", EventHorizonState.Centre(gate!), effect.Power, 3);
        Loop("hum", "gate.hum", EventHorizonState.Centre(gate!), effect.Hum, 3);
        if (g.Phase == GatePhase.Idle)
            scheduled.Clear();
    }
    void FixtureGateAudio(ClientSession session)
    {
        var oldGate = gate;
        var oldDhd = dhd;
        currentGateTick = session.GateTime;
        var world = session.World.Definition;
        StaticEntity Entity(Guid id) => world.Entities.Single(e => GraphicsFixture.EntityId(world, e.Id) == id);
        while (session.AudioPresses.TryDequeue(out int key))
            if (session.Gate is { } selected)
                Play(key == 0 ? "dhd.orb" : "dhd.press", Entity(selected.DhdId).Position + Vector3.UnitY, 4);
        while (session.AudioGates.TryDequeue(out var entry))
        {
            var g = entry.Gate;
            gate = Entity(g.GateId);
            dhd = Entity(g.DhdId);
            if (!entry.Baseline && fixtureAudioStates.TryGetValue(g.GateId, out var before) && g.Symbols.Length > before.Symbols.Length && !g.Incoming)
            {
                Play("dhd.glyph", dhd.Position + Vector3.UnitY, 3);
                Play("gate.chevron", ChevronPosition(g.Symbols.Length - 1), 3);
            }
            fixtureAudioStates[g.GateId] = g;
        }
        foreach (var g in session.Gates.Values)
        {
            gate = Entity(g.GateId);
            if (g.Phase == GatePhase.Opening)
            {
                if (g.Incoming)
                    for (int i = 0; i < 7; i++)
                        Timed("gate.incoming", g, g.PhaseTick - GateEffectTimeline.IncomingLeadTicks + (i + 1) * 6, i, ChevronPosition(i));
                Timed("gate.open", g, g.PhaseTick);
            }
            if (g.Phase == GatePhase.Closing)
                Timed("gate.close", g, g.PhaseTick);
            var effect = GateEffectTimeline.Evaluate(g.Phase, (session.GateTime - g.PhaseTick) / 60);
            Loop("power-" + g.GateId, "gate.power", EventHorizonState.Centre(gate), effect.Power, 3);
            Loop("hum-" + g.GateId, "gate.hum", EventHorizonState.Centre(gate), effect.Hum, 3);
        }
        if (session.Gates.Values.All(g => g.Phase == GatePhase.Idle))
            scheduled.Clear();
        gate = oldGate;
        dhd = oldDhd;
    }
    Vector3 ChevronPosition(int index)
    {
        return Vector3.Transform(chevrons.Length == 7 ? chevrons[Math.Clamp(index, 0, 6)] : new Vector3(0, 5.6f, 0), gate!.Transform);
    }
    public void Update(ClientSession session, SceneRenderer scene, Vector3 listener, Vector3 forward, PlayerState local, PlayerState[] remotes, BodyState[] bodies, double time)
    {
        long started = Stopwatch.GetTimestamp();
        queueDropped = session.AudioDropped;
        now = time;
        float dt = (float)Math.Clamp(now - lastTime, 0, .1);
        lastTime = now;
        Queries = 0;
        eye = listener;
        if (location != session.World.Definition.LocationId || epoch != session.Physics.Epoch)
        {
            Clear(preserveGateHistory: location == session.World.Definition.LocationId);
            location = session.World.Definition.LocationId;
            epoch = session.Physics.Epoch;
        }
        gate = session.World.Definition.Entities.FirstOrDefault(e => e.Model == "stargate");
        dhd = session.World.Definition.Entities.FirstOrDefault(e => e.Model == "dhd");
        audio.SetListener(new(eye, forward, Vector3.UnitY, local.Velocity));
        audio.Update(dt);
        voices.RemoveAll(v => !audio.IsActive(v.Handle));
        if (gate is not null && dhd is not null)
            GateEvents(session, scene);
        while (session.AudioSounds.TryDequeue(out var sound))
        {
            if (sound.Tick < session.ServerTick - 60)
                continue;
            if (sound.Kind == WorldSoundKind.Scrape)
                Loop("scrape:" + sound.BodyId, "scrape", sound.Position, sound.Strength, 1, sound.BodyId, now + .25);
            else
                Play(sound.Kind switch
                {
                    WorldSoundKind.Impact => "impact",
                    WorldSoundKind.Door => "door",
                    WorldSoundKind.Place => "build.place",
                    WorldSoundKind.GateArrival => "gate.contact",
                    _ => "build.remove"
                }, sound.Position, 2, sound.Strength, body: sound.BodyId);
        }
        if (session.AudioErrors > 0)
        {
            Play("ui.error", eye, 4);
            session.AudioErrors = 0;
        }
        foreach (var p in remotes.Append(local))
            Movement(p, p.Id == local.Id, session);
        foreach (int id in players.Keys.Where(id => id != local.Id && !remotes.Any(p => p.Id == id)).ToArray())
            players.Remove(id);
        if (gate is not null && session.Gate?.Settled == true)
            contacts.Update(gate, remotes.Append(local), time, location, epoch, bodies, session.World.Definition.Props);
        else
            contacts.Clear();
        PrepareOcclusion(session);
        if (now >= nextQueries)
        {
            nextQueries = now + .1;
            UpdateObstruction(bodies, session);
        }
        bool submerged = session.Terrain is not null && WaterSurfaceData.TrySample(session.Terrain, eye.X, eye.Z, out var water) && eye.Y < water.Height;
        underwater += ((submerged ? 1 : 0) - underwater) * Math.Min(1, dt * 5);
        float outdoor = 1 - shelter * .8f;
        string preset = session.World.Definition.Preset;
        var weather = scene.AudioWeather;
        Loop("wind", "ambience.wind", eye, preset == "temperate" ? outdoor * (1 - underwater) : 0);
        Loop("desert", "ambience.desert", eye, preset == "desert" ? outdoor * (1 - underwater) : 0);
        Loop("snow", "ambience.snow", eye, preset == "icy" ? outdoor * (.5f + weather.Gust) * (1 - underwater) : 0);
        Loop("birds", "ambience.birds", eye, preset == "temperate" ? outdoor * Math.Clamp(scene.SunElevation / 15, 0, 1) * (1 - underwater) : 0);
        Loop("underwater", "ambience.underwater", eye, underwater);
        UpdateWater(session);
        foreach (var voice in voices.ToArray())
        {
            if (voice.Expires < now)
            {
                audio.Stop(voice.Handle, .15f);
                voices.Remove(voice);
                continue;
            }
            var definition = cues[voice.Cue];
            if (voice.Body != 0 && bodies.FirstOrDefault(b => b.Id == voice.Body) is { Id: > 0 } body)
                voice.Position = body.Position;
            audio.SetEmitter(voice.Handle, new(voice.Position, Vector3.Zero));
            float distance = definition.SpatialMode == AudioSpatialMode.ThreeDimensional ? Vector3.Distance(eye, voice.Position) : 0;
            float cutoff = Math.Clamp((definition.MaxDistance - distance) / Math.Max(1, definition.MaxDistance * .2f), 0, 1);
            float obstruction = voice.Occlusion;
            audio.SetGain(voice.Handle, voice.Gain * cutoff * (1 - obstruction * .684f), voice.Loop.Length > 0 ? 1 : .2f);
            audio.SetCutoff(voice.Handle, Math.Min(voice.MaterialCutoff, MathF.Exp(MathF.Log(20000) * (1 - Math.Max(obstruction, underwater)) + MathF.Log(1500) * Math.Max(obstruction, underwater))));
        }
        DeviceMaintenance();
        UpdateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    void Movement(PlayerState p, bool local, ClientSession session)
    {
        var old = players.GetValueOrDefault(p.Id, (p, 0, now));
        float distance = old.Item2;
        double stroke = old.Item3;
        float delta = Vector3.Distance(p.Position, old.Item1.Position);
        if (delta > .75f)
        {
            players[p.Id] = (p, 0, now);
            return;
        }
        var relative = p.Position - old.Item1.Position - p.GroundVelocity * (float)Math.Clamp(now - previousMovementTime, 0, .1);
        float step = new Vector2(relative.X, relative.Z).Length();
        int priority = local ? 4 : 1;
        if (p.Grounded && old.Item1.Grounded && !p.Swimming)
        {
            distance += step;
            float stride = p.Crouched ? 1.05f : (p.Velocity - p.GroundVelocity).Length() > 3.5f ? 1.8f : 1.35f;
            if (distance >= stride)
            {
                distance %= stride;
                float gain = 1, filter = 20000;
                string cue = p.Surface switch
                {
                    GroundSurface.Wood => "step.wood",
                    GroundSurface.Grass => "step.leaves",
                    GroundSurface.Mud => "step.mud",
                    GroundSurface.Gravel => "step.gravel",
                    GroundSurface.Stone => "step.stone",
                    GroundSurface.Water => "swim",
                    _ => "step"
                };
                if (p.Surface == GroundSurface.Snow)
                {
                    gain = .65f;
                    filter = 3200;
                }
                else if (p.Surface == GroundSurface.Sand)
                {
                    gain = .7f;
                    filter = 4500;
                }
                if (p.Crouched) gain *= .35f;
                else if ((p.Velocity - p.GroundVelocity).Length() <= StargateGame.Shared.Movement.WalkSpeed + .1f) gain *= .6f;
                var foot = Play(p.Submersion > .08f ? "swim" : cue, p.Position, priority, gain);
                if (foot is not null)
                    foot.MaterialCutoff = filter;
            }
        }
        else
            distance = 0;
        if (old.Item1.Grounded && !p.Grounded && p.Velocity.Y > 2)
            Play("jump", p.Position, priority);
        if (!old.Item1.Grounded && p.Grounded && old.Item1.Velocity.Y < -1)
            Play("land", p.Position, priority, Math.Clamp(-old.Item1.Velocity.Y / 8, .15f, .8f));
        if (p.Swimming != old.Item1.Swimming)
            Play("swim", p.Position, priority);
        if (p.Swimming && p.Velocity.Length() > .5f && now - stroke > .8)
        {
            Play("swim", p.Position, priority, .35f);
            stroke = now;
        }
        players[p.Id] = (p, distance, stroke);
        if (local)
            previousMovementTime = now;
    }
    double previousMovementTime, nextWater;
    Vector3? waterSource;
    void UpdateWater(ClientSession session)
    {
        if (now >= nextWater)
        {
            nextWater = now + .5;
            waterSource = null;
            float closest = 32 * 32;
            if (session.Terrain is not null)
                for (int x = -24; x <= 24; x += 8)
                    for (int z = -24; z <= 24; z += 8)
                        if (WaterSurfaceData.TrySample(session.Terrain, eye.X + x, eye.Z + z, out var w))
                        {
                            var point = new Vector3(eye.X + x, w.Height, eye.Z + z);
                            float d = Vector3.DistanceSquared(point, eye);
                            if (d < closest)
                            {
                                closest = d;
                                waterSource = point;
                            }
                        }
        }
        Loop("water", "water", waterSource ?? eye, waterSource is null ? 0 : (1 - underwater) * .7f);
    }
    void PrepareOcclusion(ClientSession session)
    {
        if (preparingOcclusion?.IsCompleted == true)
        {
            if (preparingOcclusion.IsCompletedSuccessfully && cacheGeneration == worldGeneration)
                occlusion = preparingOcclusion.Result;
            else
                _ = preparingOcclusion.Exception;
            preparingOcclusion = null;
        }
        if (preparingOcclusion is not null || cacheEpoch == epoch && cacheTerrainCount == (session.Terrain?.Count ?? 0) && Vector3.DistanceSquared(eye, cacheCentre) < 16 * 16)
            return;
        cacheEpoch = epoch;
        cacheTerrainCount = session.Terrain?.Count ?? 0;
        cacheCentre = eye;
        cacheGeneration = worldGeneration;
        var physics = session.Physics;
        var centre = eye;
        string? ignore = dhd?.Id;
        preparingOcclusion = Task.Run(() => new AudioOcclusion(physics.ObstacleTriangles(centre, 96, ignoreEntity: ignore, includeTrees: false)));
    }
    void UpdateObstruction(BodyState[] bodies, ClientSession session)
    {
        if (occlusion is null)
            return;
        shelter = occlusion.Blocked(eye, eye + Vector3.UnitY * 30) ? 1 : 0;
        Queries++;
        foreach (var voice in voices.Where(v => cues[v.Cue].SpatialMode == AudioSpatialMode.ThreeDimensional)
            .OrderByDescending(v => v.Priority).ThenBy(v => Vector3.DistanceSquared(v.Position, eye)).Take(16))
        {
            int hits = 0;
            for (int i = -1; i <= 1; i++)
            {
                var target = voice.Position + Vector3.UnitX * (i * .15f);
                bool hit = occlusion.Blocked(eye, target);
                Queries++;
                if (!hit)
                {
                    float distance = Vector3.Distance(eye, target);
                    if (distance > .3f)
                        foreach (var b in bodies)
                        {
                            if (b.Id == voice.Body)
                                continue;
                            var def = session.World.Definition.Props.FirstOrDefault(p => p.Id == b.Id);
                            if (def is null)
                                continue;
                            if (PropGeometry.RayHit(eye, Vector3.Normalize(target - eye), b, def, distance - .2f))
                            {
                                hit = true;
                                break;
                            }
                        }
                }
                if (hit)
                    hits++;
            }
            voice.Occlusion += (hits / 3f - voice.Occlusion) * .4f;
        }
    }
    void DeviceMaintenance()
    {
        if (now >= nextDrain)
        {
            nextDrain = now + .1;
            recording?.Drain();
        }
        if (now < nextDeviceCheck)
            return;
        nextDeviceCheck = now + 5;
        if (audio.DeviceRunning)
            return;
        if (audio.IsAvailable && audio.RestartDevice())
            return;
        if (recording is not null)
            return; // Keep the diagnostic recording's engine owner stable.
        audio.Dispose();
        voices.Clear();
        audio = new(new()
        {
            SampleRate = 48000,
            Channels = 2,
            MaxVoices = 128,
            Log = Log
        });
        if (audio.IsAvailable)
            foreach (var cue in cues.Values)
                audio.RegisterCue(cue);
        Settings.Apply(audio);
    }
    public void Idle(double time)
    {
        now = time;
        audio.Update(.016f);
        voices.RemoveAll(v => !audio.IsActive(v.Handle));
        DeviceMaintenance();
    }
    public void Preview(string cue)
    {
        now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        eye = Vector3.Zero;
        Play(cue, new(0, 0, -2), 4);
    }
}
