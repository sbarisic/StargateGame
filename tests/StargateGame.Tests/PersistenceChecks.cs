using System.Diagnostics;
using System.Reflection;
using System.Numerics;
using Microsoft.Data.Sqlite;
using StargateGame.Persistence;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class PersistenceChecks
{
    public static async Task CrashWorker(string path)
    {
        using var gen = new GenerationService(7319, "temperate");
        var plan = await gen.Plan;
        await using var db = new WorldRepository(path);
        var location = await db.ResolveAsync(plan.Definition);
        var structure = new StructureRecord(Guid.NewGuid(), new(45, .5f, 45), 0);
        var state = location.Changes with
        {
            Revision = 1,
            Structures = [structure],
            Pieces = [new(Guid.NewGuid(), structure.Id, PieceKind.SquareFoundation, default, 0, 0, false, -.15f)]
        };
        await db.SaveAsync(state, 0);
        Console.WriteLine("DURABLE " + location.Id);
        Console.Out.Flush();
        // Start repeated transactions. Parent kills this process without Dispose/checkpointing.
        for (int i = 2; i < 10000; i++)
        {
            state = state with
            {
                Revision = i,
                Pieces = state.Pieces.Select(p => p with { FoundationBottom = -.15f - i * .000001f }).ToArray()
            };
            await db.SaveAsync(state, i - 1);
        }
    }
    public static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "stargate-durability-" + Guid.NewGuid().ToString("N"));
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--persistence-crash-worker");
        info.ArgumentList.Add(root);
        using (var worker = Process.Start(info)!)
        {
            string line = await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) ?? throw new Exception(await worker.StandardError.ReadToEndAsync());
            Verify.Check(line.StartsWith("DURABLE "), "crash worker confirmed a durable edit");
            worker.Kill(true);
            await worker.WaitForExitAsync();
        }
        using var gen = new GenerationService(7319, "temperate");
        var plan = await gen.Plan;
        SavedLocation saved;
        await using (var db = new WorldRepository(root))
        {
            saved = await db.ResolveAsync(plan.Definition);
            Verify.Check(saved.Changes.Revision >= 1 && saved.Changes.Pieces.Length == 1, "confirmed edit survives process termination and WAL recovery");
            await db.AddAliasAsync("AB-CD-EF", saved.Id);
            await db.AddAliasAsync("GH-IJ-KL", saved.Id);
            Verify.Check((await db.ResolveAliasAsync("AB-CD-EF")).Id == (await db.ResolveAliasAsync("GH-IJ-KL")).Id, "address aliases share one saved location");
            bool locked = false;
            try
            {
                await using var duplicate = new WorldRepository(root);
                await duplicate.ResolveAsync(plan.Definition);
            }
            catch (IOException)
            {
                locked = true;
            }
            Verify.Check(locked, "second server cannot open the same universe");
        }
        string file = Path.Combine(root, "default", "universe.sqlite");
        using (var db = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            db.Open();
            using var c = db.CreateCommand();
            c.CommandText = "CREATE TRIGGER reject_write BEFORE INSERT ON pieces BEGIN SELECT RAISE(ABORT,'injected storage failure'); END;";
            c.ExecuteNonQuery();
        }
        await using (var db = new WorldRepository(root))
        {
            bool failed = false;
            try
            {
                await db.SaveAsync(saved.Changes with
                {
                    Revision = saved.Changes.Revision + 1
                }, saved.Changes.Revision);
            }
            catch (SqliteException)
            {
                failed = true;
            }
            var after = await db.LoadAsync(saved.Id);
            Verify.Check(failed && after.Changes.Revision == saved.Changes.Revision && after.Changes.Pieces.Length == 1, "storage failure rolls back deletes and revision together");
        }
        using (var db = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            db.Open();
            using var c = db.CreateCommand();
            c.CommandText = "DROP TRIGGER reject_write; DROP TABLE aliases; DROP TABLE receipts; DROP TABLE galaxy_associations; DROP TABLE galaxy_aliases; DROP TABLE galaxy_corrections; DROP TABLE players; DROP TABLE revoked_devices; DROP TABLE transits; DELETE FROM migrations WHERE version>=2; PRAGMA user_version=1;";
            c.ExecuteNonQuery();
        }
        await using (var db = new WorldRepository(root))
        {
            Verify.Check((await db.LoadAsync(saved.Id)).Changes.Pieces.Length == 1, "schema migration retains building data");
        }
        var backups = Directory.GetFiles(Path.Combine(root, "default"), "universe.sqlite.backup-*").OrderDescending().ToArray();
        Verify.Check(backups.Length is >= 1 and <= 3, "migration retains at most three consistent backups");
        string restored = Path.Combine(root, "restored");
        Directory.CreateDirectory(restored);
        File.Copy(backups[0], Path.Combine(restored, "universe.sqlite"));
        await using (var db = new WorldRepository(root, "restored"))
        {
            Verify.Check((await db.LoadAsync(saved.Id)).Changes.Pieces.Length == 1, "backup restores a usable persistent world");
        }
        using (var db = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            db.Open();
            using var c = db.CreateCommand();
            c.CommandText = "PRAGMA user_version=999;";
            c.ExecuteNonQuery();
        }
        bool incompatible = false;
        try
        {
            await using var db = new WorldRepository(root);
            await db.LoadAsync(saved.Id);
        }
        catch (InvalidDataException)
        {
            incompatible = true;
        }
        Verify.Check(incompatible, "future save schema rejected without resetting worlds");
    }
}
