using System.Security.Cryptography;
using System.Text.Json;
using StargateGame.Galaxy;
using StargateGame.Shared;
using StargateGame.Building;

namespace StargateGame.Persistence;

public sealed record UniverseGalaxy(Guid UniverseId, long Seed, long Epoch, ulong HomeKey, Guid HomeLocation, string CatalogueVersion, int ProvisioningVersion);
public sealed record GateAssociation(ulong Address, Guid LocationId, Guid GateId, Guid DhdId, bool Powered = true, bool Available = true, bool SupportsOrdinary = true);
public sealed record GalaxyTarget(GateAssociation Association, Position Position, long CorrectionRevision);
public sealed partial class WorldRepository
{
    public Task<PlanetDefinition> SetHomeTimeAsync(Guid home) => Run(db => SetHomeTime(db, home));
    PlanetDefinition SetHomeTime(Microsoft.Data.Sqlite.SqliteConnection db, Guid home)
    {
        var definition = Load(db, home).Definition;
        var environment = definition.Environment ?? PlanetEnvironmentDefinition.Create(home, definition.Seed, definition.Preset);
        if (environment.DebugConfigured || environment.FixedHour == 11)
            return definition;
        definition = definition with
        {
            Environment = environment with
            {
                FixedHour = 11
            }
        };
        Execute(db, "UPDATE locations SET definition=$definition WHERE id=$id;", ("$definition", JsonSerializer.Serialize(definition, Protocol.Json)), ("$id", home.ToString("D")));
        return definition;
    }
    public Task<UniverseGalaxy?> ReadGalaxyAsync() => Run(db => Scalar(db, "SELECT value FROM metadata WHERE key='galaxy';") is string json ? JsonSerializer.Deserialize<UniverseGalaxy>(json) : null);
    public Task<UniverseGalaxy> AdoptGalaxyAsync(Catalogue catalogue, Guid home) => Run(db =>
    {
        if (Scalar(db, "SELECT value FROM metadata WHERE key='galaxy';") is string json)
        {
            var retained = JsonSerializer.Deserialize<UniverseGalaxy>(json)!;
            if (retained.CatalogueVersion != Address.Version || retained.ProvisioningVersion != 1 || retained.UniverseId != UniverseId)
                throw new InvalidDataException("Saved galaxy version is incompatible; refusing to replace it.");
            catalogue.Find(retained.HomeKey);
            SetHomeTime(db, retained.HomeLocation);
            return retained;
        }
        Load(db, home);
        ulong key = catalogue.HomeKey();
        var state = new UniverseGalaxy(UniverseId, deterministicSeed ?? BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)), deterministicSeed.HasValue ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), key, home, Address.Version, 1);
        using var tx = db.BeginTransaction();
        SetHomeTime(db, home);
        Execute(db, "INSERT INTO metadata VALUES('galaxy',$json);", ("$json", JsonSerializer.Serialize(state)));
        Execute(db, "INSERT INTO galaxy_associations(address,location,gate,dhd) VALUES($key,$location,$gate,$dhd);", ("$key", (long)key), ("$location", home.ToString("D")), ("$gate", Address.Identity(UniverseId, key, "gate").ToString("D")), ("$dhd", Address.Identity(UniverseId, key, "dhd").ToString("D")));
        tx.Commit();
        return state;
    });
    public GateAssociation Association(UniverseGalaxy universe, ulong key) => new(key, key == universe.HomeKey ? universe.HomeLocation : Address.Identity(universe.UniverseId, key, "location"), Address.Identity(universe.UniverseId, key, "gate"), Address.Identity(universe.UniverseId, key, "dhd"));
    public Task<GalaxyTarget> ResolveGalaxyAsync(UniverseGalaxy universe, Catalogue catalogue, ulong key) => Run(db =>
    {
        var nominal = catalogue.Find(key);
        ulong target = key;
        if (Scalar(db, "SELECT target FROM galaxy_aliases WHERE address=$key;", ("$key", (long)key)) is long alias)
            target = (ulong)alias;
        var entry = catalogue.Find(target);
        var corrected = nominal.Position;
        long revision = 0;
        using (var c = Command(db, "SELECT revision,x,y,z FROM galaxy_corrections WHERE address=$key;", ("$key", (long)key)))
        using (var r = c.ExecuteReader())
            if (r.Read())
            {
                revision = r.GetInt64(0);
                corrected += new Position(r.GetDouble(1), r.GetDouble(2), r.GetDouble(3));
            }
        if (!Address.Acquired(corrected, entry.Position))
            throw new InvalidOperationException("The retained receiver is outside the one-light-year acquisition radius. Address will not redirect.");
        var association = Association(universe, target);
        using (var c = Command(db, "SELECT location,gate,dhd,powered,available,ordinary FROM galaxy_associations WHERE address=$key;", ("$key", (long)target)))
        using (var r = c.ExecuteReader())
            if (r.Read())
                association = new(target, Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetInt32(3) != 0, r.GetInt32(4) != 0, r.GetInt32(5) != 0);
        return new GalaxyTarget(association, corrected, revision);
    });
    public Task<SavedLocation?> TryLoadAsync(Guid id) => Run(db => Scalar(db, "SELECT id FROM locations WHERE id=$id;", ("$id", id.ToString("D"))) is null ? null : Load(db, id));
    public Task<SavedLocation> MaterializeGalaxyAsync(UniverseGalaxy universe, GateAssociation association, PlanetDefinition definition) => Run(db =>
    {
        var expected = Association(universe, association.Address);
        if (association.LocationId != expected.LocationId || association.GateId != expected.GateId || association.DhdId != expected.DhdId)
            throw new InvalidDataException("Galaxy association identity mismatch.");
        if (Scalar(db, "SELECT id FROM locations WHERE id=$id;", ("$id", association.LocationId.ToString("D"))) is not null)
            return Load(db, association.LocationId);
        PlanetSettings.Validate(definition);
        Guid id = association.LocationId;
        definition = definition with
        {
            UniverseId = UniverseId,
            LocationId = id,
            Environment = PlanetEnvironmentDefinition.Create(id, definition.Seed, definition.Preset) with
            {
                EpochUtcMilliseconds = universe.Epoch
            }
        };
        definition = IdentifyProps(definition);
        using var tx = db.BeginTransaction();
        Execute(db, "INSERT INTO locations VALUES($id,NULL,$definition,$generator,$scenery,$kit,0);", ("$id", id.ToString("D")), ("$definition", JsonSerializer.Serialize(definition, Protocol.Json)), ("$generator", definition.GeneratorVersion), ("$scenery", PlanetSettings.SceneryVersion), ("$kit", BuildingGeometry.KitVersion));
        Execute(db, "INSERT OR IGNORE INTO galaxy_associations(address,location,gate,dhd) VALUES($key,$location,$gate,$dhd);", ("$key", (long)association.Address), ("$location", id.ToString("D")), ("$gate", association.GateId.ToString("D")), ("$dhd", association.DhdId.ToString("D")));
        var generated = WriteGenerated(db, definition);
        tx.Commit();
        return new(id, definition, generated, []);
    });
    public Task SetGalaxyAliasAsync(Catalogue catalogue, ulong alias, ulong target) => Run(db =>
    {
        catalogue.Find(alias);
        catalogue.Find(target);
        if (alias == target || Scalar(db, "SELECT address FROM galaxy_associations WHERE address=$key;", ("$key", (long)alias)) is not null || Scalar(db, "SELECT target FROM galaxy_aliases WHERE address=$key;", ("$key", (long)target)) is not null)
            throw new InvalidOperationException("Alias would replace an established association or form a chain.");
        Execute(db, "INSERT INTO galaxy_aliases VALUES($alias,$target);", ("$alias", (long)alias), ("$target", (long)target));
        return true;
    });
    public Task SetGalaxyCorrectionAsync(Catalogue catalogue, ulong key, long expectedRevision, Position offset) => Run(db =>
    {
        catalogue.Find(key);
        if (!offset.Finite)
            throw new ArgumentException("Correction must be finite.");
        long revision = Convert.ToInt64(Scalar(db, "SELECT revision FROM galaxy_corrections WHERE address=$key;", ("$key", (long)key)) ?? 0L);
        if (revision != expectedRevision)
            throw new InvalidOperationException("Correction revision conflict.");
        Execute(db, "INSERT INTO galaxy_corrections VALUES($key,$revision,$x,$y,$z) ON CONFLICT(address) DO UPDATE SET revision=$revision,x=$x,y=$y,z=$z;", ("$key", (long)key), ("$revision", revision + 1), ("$x", offset.X), ("$y", offset.Y), ("$z", offset.Z));
        return true;
    });
    public Task SetGateAvailabilityAsync(UniverseGalaxy universe, Catalogue catalogue, ulong key, bool powered, bool available) => Run(db =>
    {
        catalogue.Find(key);
        var gate = Association(universe, key);
        Execute(db, "INSERT INTO galaxy_associations(address,location,gate,dhd,powered,available) VALUES($key,$location,$gate,$dhd,$powered,$available) ON CONFLICT(address) DO UPDATE SET powered=$powered,available=$available;", ("$key", (long)key), ("$location", gate.LocationId.ToString("D")), ("$gate", gate.GateId.ToString("D")), ("$dhd", gate.DhdId.ToString("D")), ("$powered", powered ? 1 : 0), ("$available", available ? 1 : 0));
        return true;
    });
    public Task SetGateRouteSupportAsync(UniverseGalaxy universe, Catalogue catalogue, ulong key, bool ordinary) => Run(db =>
    {
        catalogue.Find(key);
        var gate = Association(universe, key);
        Execute(db, "INSERT INTO galaxy_associations(address,location,gate,dhd,ordinary) VALUES($key,$location,$gate,$dhd,$ordinary) ON CONFLICT(address) DO UPDATE SET ordinary=$ordinary;", ("$key", (long)key), ("$location", gate.LocationId.ToString("D")), ("$gate", gate.GateId.ToString("D")), ("$dhd", gate.DhdId.ToString("D")), ("$ordinary", ordinary ? 1 : 0));
        return true;
    });
}
