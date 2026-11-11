using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using StargateGame.Building;
using StargateGame.Shared;

namespace StargateGame.Persistence;

public sealed record SavedLocation(Guid Id, PlanetDefinition Definition, WorldChanges Changes, BodyState[] Bodies);

/// <summary>One connection owned by one worker. Physics/native checkpoints never enter the save file.</summary>
public sealed partial class WorldRepository : IAsyncDisposable
{
    readonly Channel<Action<SqliteConnection>> queue = Channel.CreateBounded<Action<SqliteConnection>>(128);
    readonly Task worker;
    readonly FileStream ownership;
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int pending;
    public int Pending => Volatile.Read(ref pending);
    public double LastWriteMilliseconds
    {
        get;
        private set;
    }
    public Guid UniverseId
    {
        get;
        private set;
    }
    public string DatabasePath
    {
        get;
    }
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StargateGame", "Server", "saves");
    readonly long? deterministicSeed;
    Guid InitialIdentity(string label) => deterministicSeed is { } seed ? new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"test:{seed}:{label}"))[..16]) : Guid.NewGuid();
    public WorldRepository(string directory, string universe = "default", long? deterministicSeed = null)
    {
        if (string.IsNullOrEmpty(universe) || universe.Length > 64 || universe.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Universe must contain only letters, digits, '-' and '_'.");
        this.deterministicSeed = deterministicSeed;
        string path = Path.Combine(Path.GetFullPath(directory), universe);
        Directory.CreateDirectory(path);
        DatabasePath = Path.Combine(path, "universe.sqlite");
        ownership = new FileStream(Path.Combine(path, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        worker = Task.Run(async () =>
        {
            try
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 5 }.ToString());
                db.Open();
                Execute(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
                int version = Convert.ToInt32(Scalar(db, "PRAGMA user_version;"));
                if (version > 10)
                    throw new InvalidDataException("Save schema is newer than this server.");
                if (version == 0)
                {
                    if (Convert.ToInt64(Scalar(db, "SELECT count(*) FROM sqlite_master WHERE type='table';")) > 0)
                        throw new InvalidDataException("Unrecognized save schema; refusing to overwrite it.");
                    Backup(db);
                    Execute(db, "BEGIN IMMEDIATE; CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL); CREATE TABLE migrations(version INTEGER PRIMARY KEY, applied TEXT NOT NULL); CREATE TABLE locations(id TEXT PRIMARY KEY, selector TEXT UNIQUE, definition TEXT NOT NULL, generator INTEGER NOT NULL, scenery INTEGER NOT NULL, kit INTEGER NOT NULL, revision INTEGER NOT NULL); CREATE TABLE structures(location TEXT NOT NULL REFERENCES locations(id),id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(location,id)); CREATE TABLE pieces(location TEXT NOT NULL REFERENCES locations(id),id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(location,id)); CREATE TABLE removals(location TEXT NOT NULL REFERENCES locations(id),category TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(location,category,id)); CREATE TABLE crates(location TEXT NOT NULL REFERENCES locations(id),id INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(location,id)); INSERT INTO migrations VALUES(1,datetime('now')); PRAGMA user_version=1;");
                    Execute(db, "INSERT INTO metadata VALUES('universe',$value);", ("$value", InitialIdentity("universe").ToString("D")));
                    Execute(db, "COMMIT;");
                    version = 1;
                }
                if (version == 1)
                {
                    Backup(db);
                    Execute(db, "BEGIN IMMEDIATE; CREATE TABLE aliases(alias TEXT PRIMARY KEY,location TEXT NOT NULL REFERENCES locations(id)); INSERT INTO migrations VALUES(2,datetime('now')); PRAGMA user_version=2; COMMIT;");
                    version = 2;
                }
                if (version == 2)
                {
                    Backup(db);
                    Execute(db, "BEGIN IMMEDIATE; CREATE TABLE receipts(id TEXT PRIMARY KEY,location TEXT NOT NULL REFERENCES locations(id),result TEXT NOT NULL); INSERT INTO migrations VALUES(3,datetime('now')); PRAGMA user_version=3; COMMIT;");
                    version = 3;
                }
                if (version == 3)
                {
                    Backup(db);
                    Execute(db, "BEGIN IMMEDIATE;");
                    var definitions = new List<(string Id, PlanetDefinition Definition)>();
                    using (var command = Command(db, "SELECT id,definition FROM locations;"))
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            definitions.Add((reader.GetString(0), JsonSerializer.Deserialize<PlanetDefinition>(reader.GetString(1), Protocol.Json)!));
                    foreach (var (id, d) in definitions)
                    {
                        var updated = d with
                        {
                            Environment = PlanetEnvironmentDefinition.Create(Guid.Parse(id), d.Seed, d.Preset)
                        };
                        Execute(db, "UPDATE locations SET definition=$definition WHERE id=$id;", ("$definition", JsonSerializer.Serialize(updated, Protocol.Json)), ("$id", id));
                    }
                    Execute(db, "INSERT INTO migrations VALUES(4,datetime('now')); PRAGMA user_version=4; COMMIT;");
                    version = 4;
                }
                if (version == 4)
                {
                    Backup(db);
                    Execute(db, "BEGIN IMMEDIATE; CREATE TABLE galaxy_associations(address INTEGER PRIMARY KEY,location TEXT NOT NULL UNIQUE,gate TEXT NOT NULL UNIQUE,dhd TEXT NOT NULL UNIQUE,powered INTEGER NOT NULL DEFAULT 1,available INTEGER NOT NULL DEFAULT 1); CREATE TABLE galaxy_aliases(address INTEGER PRIMARY KEY,target INTEGER NOT NULL); CREATE TABLE galaxy_corrections(address INTEGER PRIMARY KEY,revision INTEGER NOT NULL,x REAL NOT NULL,y REAL NOT NULL,z REAL NOT NULL); INSERT INTO migrations VALUES(5,datetime('now')); PRAGMA user_version=5; COMMIT;");
                    version = 5;
                }
                if (version == 5)
                {
                    Backup(db);
                    Execute(db, "BEGIN IMMEDIATE; ALTER TABLE galaxy_associations ADD COLUMN ordinary INTEGER NOT NULL DEFAULT 1; INSERT INTO migrations VALUES(6,datetime('now')); PRAGMA user_version=6; COMMIT;");
                    version = 6;
                }
                if (version == 6)
                {
                    Backup(db);
                    Execute(db, "BEGIN IMMEDIATE; CREATE TABLE players(id TEXT PRIMARY KEY,device TEXT NOT NULL UNIQUE,revoked INTEGER NOT NULL DEFAULT 0,json TEXT NOT NULL); CREATE TABLE revoked_devices(device TEXT PRIMARY KEY); CREATE TABLE transits(id TEXT PRIMARY KEY,entity TEXT NOT NULL,phase TEXT NOT NULL,json TEXT NOT NULL); INSERT INTO migrations VALUES(7,datetime('now')); PRAGMA user_version=7; COMMIT;");
                    version = 7;
                }
                if (version == 7)
                {
                    Backup(db);
                    using var migration = db.BeginTransaction();
                    var definitions = new List<PlanetDefinition>();
                    using (var cmd = Command(db, "SELECT definition FROM locations;"))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                            definitions.Add(JsonSerializer.Deserialize<PlanetDefinition>(r.GetString(0), Protocol.Json)!);
                    foreach (var old in definitions)
                    {
                        var d = IdentifyProps(old);
                        var bodies = Read<BodyState>(db, "crates", d.LocationId);
                        if (bodies.Length == 0)
                            bodies = d.Props.Select(p => new BodyState(p.Id, p.Position, System.Numerics.Quaternion.Identity, default, default, false)).ToArray();
                        WriteBodies(db, d.LocationId, bodies, d.Props);
                    }
                    Execute(db, "INSERT INTO migrations VALUES(8,datetime('now')); PRAGMA user_version=8;");
                    migration.Commit();
                    version = 8;
                }
                if (version == 8)
                {
                    Backup(db);
                    using var migration = db.BeginTransaction();
                    MigrateWeapons(db);
                    migration.Commit();
                }
                Execute(db, "CREATE TABLE IF NOT EXISTS cables(location TEXT PRIMARY KEY,revision INTEGER NOT NULL,json TEXT NOT NULL); PRAGMA user_version=10;");
                RecoverTransits(db);
                UniverseId = Guid.Parse((string)Scalar(db, "SELECT value FROM metadata WHERE key='universe';")!);
                ready.SetResult();
                await foreach (var action in queue.Reader.ReadAllAsync())
                {
                    Interlocked.Decrement(ref pending);
                    action(db);
                }
                Execute(db, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
                queue.Writer.TryComplete(ex);
                throw;
            }
            finally
            {
                ownership.Dispose();
            }
        });
    }
    void Backup(SqliteConnection db)
    {
        string target = DatabasePath + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff");
        using (var backup = new SqliteConnection($"Data Source={target};Pooling=False"))
        {
            backup.Open();
            db.BackupDatabase(backup);
        }
        foreach (var old in Directory.GetFiles(Path.GetDirectoryName(DatabasePath)!, "universe.sqlite.backup-*").OrderDescending().Skip(3))
            File.Delete(old);
    }
    async Task<T> Run<T>(Func<SqliteConnection, T> action)
    {
        await ready.Task;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref pending);
        try
        {
            await queue.Writer.WriteAsync(db =>
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    completion.SetResult(action(db));
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
                finally
                {
                    LastWriteMilliseconds = timer.Elapsed.TotalMilliseconds;
                }
            });
        }
        catch
        {
            Interlocked.Decrement(ref pending);
            throw;
        }
        return await completion.Task;
    }
    public Task<SavedLocation> ResolveAsync(PlanetDefinition definition, string? selector = null) => Run(db =>
    {
        selector ??= $"developer:{definition.Preset}:{definition.Seed}";
        using (var command = Command(db, "SELECT id FROM locations WHERE selector=$selector;", ("$selector", selector)))
            if (command.ExecuteScalar() is string id)
                return Load(db, Guid.Parse(id));
        return Create(db, definition, selector);
    });
    public Task AddAliasAsync(string alias, Guid location) => Run(db =>
    {
        if (string.IsNullOrWhiteSpace(alias) || alias.Length > 128)
            throw new ArgumentException("Invalid alias.");
        Execute(db, "INSERT INTO aliases VALUES($alias,$location);", ("$alias", alias), ("$location", location.ToString("D")));
        return true;
    });
    public Task<SavedLocation> ResolveAliasAsync(string alias) => Run(db => Load(db, Guid.Parse((string?)Scalar(db, "SELECT location FROM aliases WHERE alias=$alias;", ("$alias", alias)) ?? throw new InvalidDataException("Unknown location alias."))));
    public Task<SavedLocation> CreateAsync(PlanetDefinition definition) => Run(db => Create(db, definition, null));
    SavedLocation Create(SqliteConnection db, PlanetDefinition definition, string? selector)
    {
        PlanetSettings.Validate(definition);
        Guid id = InitialIdentity(selector ?? definition.Id);
        definition = definition with
        {
            UniverseId = UniverseId,
            LocationId = id,
            Environment = PlanetEnvironmentDefinition.Create(id, definition.Seed, definition.Preset)
        };
        definition = IdentifyProps(definition);
        using var tx = db.BeginTransaction();
        Execute(db, "INSERT INTO locations VALUES($id,$selector,$definition,$generator,$scenery,$kit,0);", ("$id", id.ToString("D")), ("$selector", selector), ("$definition", JsonSerializer.Serialize(definition, Protocol.Json)), ("$generator", definition.GeneratorVersion), ("$scenery", PlanetSettings.SceneryVersion), ("$kit", BuildingGeometry.KitVersion));
        var generated = WriteGenerated(db, definition);
        tx.Commit();
        return new(id, definition, generated, []);
    }
    static WorldChanges WriteGenerated(SqliteConnection db, PlanetDefinition definition)
    {
        var state = GeneratedBuildings.Create(definition);
        string location = definition.LocationId.ToString("D");
        foreach (var s in state.Structures)
            Execute(db, "INSERT INTO structures VALUES($location,$id,$json);", ("$location", location), ("$id", s.Id.ToString("D")), ("$json", JsonSerializer.Serialize(s, Protocol.Json)));
        foreach (var p in state.Pieces)
            Execute(db, "INSERT INTO pieces VALUES($location,$id,$json);", ("$location", location), ("$id", p.Id.ToString("D")), ("$json", JsonSerializer.Serialize(p, Protocol.Json)));
        return state;
    }
    public Task<SavedLocation> LoadAsync(Guid id) => Run(db => Load(db, id));
    SavedLocation Load(SqliteConnection db, Guid id)
    {
        PlanetDefinition definition;
        long revision;
        using (var c = Command(db, "SELECT definition,generator,scenery,kit,revision FROM locations WHERE id=$id;", ("$id", id.ToString("D"))))
        using (var r = c.ExecuteReader())
        {
            if (!r.Read())
                throw new InvalidDataException("Saved location does not exist.");
            definition = JsonSerializer.Deserialize<PlanetDefinition>(r.GetString(0), Protocol.Json)!;
            revision = r.GetInt64(4);
            if (!PlanetSettings.Supports(r.GetInt32(1)) || r.GetInt32(1) != definition.GeneratorVersion || definition.SettingsHash != PlanetSettings.Hash(definition.Seed, definition.Preset, definition.GeneratorVersion, definition.SettlementGeneration, definition.Generation) || r.GetInt32(2) != PlanetSettings.SceneryVersion || r.GetInt32(3) != BuildingGeometry.KitVersion)
                throw new InvalidDataException("This world requires regeneration for the current terrain and scenery. Start a new universe with --universe NAME; the old save has been preserved.");
        }
        PlanetSettings.Validate(definition);
        if (definition.UniverseId != UniverseId || definition.LocationId != id)
            throw new InvalidDataException("Saved location identity mismatch.");
        var state = new WorldChanges(UniverseId, id, revision, Read<StructureRecord>(db, "structures", id), Read<BuildingPiece>(db, "pieces", id), Read<SceneryRemoval>(db, "removals", id));
        BuildingRules.ValidateState(state);
        return new(id, definition, state, Read<BodyState>(db, "crates", id));
    }
    public Task<BuildResult?> FindReceiptAsync(Guid request) => Run(db => Scalar(db, "SELECT result FROM receipts WHERE id=$id;", ("$id", request.ToString("D"))) is string json ? JsonSerializer.Deserialize<BuildResult>(json, Protocol.Json) : null);
    public Task SaveAsync(WorldChanges state, long expectedRevision, BuildResult? receipt = null) => SaveDestructionAsync(state, expectedRevision, [], receipt);
    public Task SaveDestructionAsync(WorldChanges state, long expectedRevision, Guid[] destroyedProps, BuildResult? receipt = null) => Run(db =>
    {
        BuildingRules.ValidateState(state);
        using var tx = db.BeginTransaction();
        long revision = Convert.ToInt64(Scalar(db, "SELECT revision FROM locations WHERE id=$id;", ("$id", state.LocationId.ToString("D"))));
        if (state.UniverseId != UniverseId || revision != expectedRevision || state.Revision != expectedRevision + 1)
            throw new InvalidOperationException("Save revision conflict.");
        string location = state.LocationId.ToString("D");
        RemoveEmptyWeaponContainers(db, state.LocationId, state.Pieces.Where(p => WeaponCatalog.IsContainer(p.Prefab)).Select(p => p.Id).ToHashSet());
        foreach (string table in new[] { "structures", "pieces", "removals" })
            Execute(db, $"DELETE FROM {table} WHERE location=$location;", ("$location", location));
        foreach (var s in state.Structures)
            Execute(db, "INSERT INTO structures VALUES($location,$id,$json);", ("$location", location), ("$id", s.Id.ToString("D")), ("$json", JsonSerializer.Serialize(s, Protocol.Json)));
        foreach (var p in state.Pieces)
            Execute(db, "INSERT INTO pieces VALUES($location,$id,$json);", ("$location", location), ("$id", p.Id.ToString("D")), ("$json", JsonSerializer.Serialize(p, Protocol.Json)));
        foreach (var r in state.Removed)
            Execute(db, "INSERT INTO removals VALUES($location,$category,$id,$json);", ("$location", location), ("$category", r.Category), ("$id", r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$json", JsonSerializer.Serialize(r, Protocol.Json)));
        Execute(db, "UPDATE locations SET revision=$revision WHERE id=$id;", ("$revision", state.Revision), ("$id", location));
        if (receipt is not null)
            Execute(db, "INSERT INTO receipts VALUES($id,$location,$result);", ("$id", receipt.RequestId.ToString("D")), ("$location", location), ("$result", JsonSerializer.Serialize(receipt, Protocol.Json)));
        if (destroyedProps.Length > 0)
        {
            var definition = JsonSerializer.Deserialize<PlanetDefinition>((string)Scalar(db, "SELECT definition FROM locations WHERE id=$id;", ("$id", location))!, Protocol.Json)!;
            var registry = definition.Props.Where(p => !destroyedProps.Contains(p.PersistentId)).ToArray();
            var poses = Read<BodyState>(db, "crates", state.LocationId).ToDictionary(p => p.Id);
            WriteBodies(db, state.LocationId, registry.Select(p => poses.GetValueOrDefault(p.Id, new(p.Id, p.Position, System.Numerics.Quaternion.Identity, default, default, false))).ToArray(), registry);
        }
        tx.Commit();
        return true;
    });
    public Task SaveBodiesAsync(Guid location, BodyState[] bodies, PropDefinition[]? registry = null) => Run(db =>
    {
        using var tx = db.BeginTransaction();
        WriteBodies(db, location, bodies, registry);
        tx.Commit();
        return true;
    });
    static T[] Read<T>(SqliteConnection db, string table, Guid id)
    {
        using var c = Command(db, $"SELECT json FROM {table} WHERE location=$id ORDER BY id;", ("$id", id.ToString("D")));
        using var r = c.ExecuteReader();
        List<T> values = [];
        while (r.Read())
            values.Add(JsonSerializer.Deserialize<T>(r.GetString(0), Protocol.Json)!);
        return values.ToArray();
    }
    static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        var c = db.CreateCommand();
        c.CommandText = sql;
        foreach (var p in parameters)
            c.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
        return c;
    }
    static void Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var c = Command(db, sql, parameters);
        c.ExecuteNonQuery();
    }
    static object? Scalar(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var c = Command(db, sql, parameters);
        return c.ExecuteScalar();
    }
    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        try
        {
            await worker;
        }
        finally
        {
            ownership.Dispose();
        }
    }
}
