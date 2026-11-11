using System.Diagnostics;
using System.Numerics;
using StargateGame.Physics;
using StargateGame.Shared;

static class PropExitChecks
{
    public static void Run()
    {
        var definition = PlanetDefinition.Default with
        {
            Props = [],
            Entities = []
        };
        var gate = new GateFrame(new(0, 3, 0), Quaternion.Identity, 2.3f);
        var high = new BodyState(1, new(0, 8, 1.2f), Quaternion.Identity, default, default, false);
        using var blocked = new PhysicsWorld(definition);
        blocked.AddStaticBox(high.Position, new(6));
        foreach (var prop in new[] { new PropDefinition(1, high.Position, new(.5f), 10), PropDefinition.Barrel(1, high.Position, false) })
        {
            var times = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var watch = Stopwatch.StartNew();
                Verify.Check(!blocked.TryFindPropExit(gate, prop, high, out _), "blocked exit remains rejected");
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"PROP EXIT full-search {prop.Kind} mean_ms={times.Average():F3} max_ms={times.Max():F3}");
            var search = blocked.BeginPropExitSearch(gate, prop, high);
            var slices = new List<double>();
            int calls = 0;
            while (!search.Complete)
            {
                int before = search.CandidatesChecked;
                var watch = Stopwatch.StartNew();
                bool found = blocked.ContinuePropExitSearch(search, out _, 4, 1);
                slices.Add(watch.Elapsed.TotalMilliseconds);
                Verify.Check(!found && search.CandidatesChecked > before && search.CandidatesChecked - before <= 4, "bounded search advances without exceeding candidate cap");
                if (++calls > 200)
                    throw new Exception("Blocked exit search failed to terminate");
                blocked.Step(); // The world remains available between query slices.
            }
            int completeCount = search.CandidatesChecked;
            Verify.Check(!blocked.ContinuePropExitSearch(search, out _) && search.CandidatesChecked == completeCount, "completed blocked search does no more work");
            var ordered = slices.Order().ToArray();
            Console.WriteLine($"PROP EXIT sliced {prop.Kind} slices={calls} candidates={completeCount} p95_ms={ordered[(int)(ordered.Length * .95)]:F3} max_ms={ordered[^1]:F3}");
            using var clear = new PhysicsWorld(definition);
            foreach (var desired in new[] { high, high with { Position = new(0, 1.8f, 1.2f) }, high with { Position = new(0, .2f, 1.2f) } })
            {
                Verify.Check(clear.TryFindPropExit(gate, prop, desired, out var expected), "clear receiver has a safe result");
                var incremental = clear.BeginPropExitSearch(gate, prop, desired);
                BodyState actual = default;
                bool found = false;
                while (!incremental.Complete)
                    found = clear.ContinuePropExitSearch(incremental, out actual, 1, 1);
                Verify.Check(found && actual == expected, "sliced placement preserves grounded priority, nearest pose, orientation and momentum");
                Verify.Check(clear.CanPlaceProp(prop, actual) && GateTravel.Extents(gate, actual, prop).Min >= .05f, "sliced result clears full hull and stays in front");
            }
            var arrival = clear.BeginPropExitSearch(gate, prop, high);
            clear.AddStaticBox(high.Position, new(6)); // Obstruction arrives after cursor creation.
            bool unsafeResult = false;
            while (!arrival.Complete)
                unsafeResult |= clear.ContinuePropExitSearch(arrival, out _, 4, 1);
            Verify.Check(!unsafeResult, "search rechecks current collision instead of caching clear poses");
            var restart = clear.BeginPropExitSearch(gate, prop, high);
            Verify.Check(!clear.ContinuePropExitSearch(restart, out _, 0, 1) && restart.CandidatesChecked == 0, "exhausted shared budget defers all placement work");
        }
        Console.WriteLine($"PROP EXIT {Verify.Count} CHECKS PASSED");
    }
}
