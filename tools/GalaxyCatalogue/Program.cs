using System.Diagnostics;
using StargateGame.Galaxy;
using StargateGame.Persistence;
using StargateGame.Shared;

using var stop=new CancellationTokenSource();
Console.CancelKeyPress+=(_,e)=>{
e.Cancel=true;
stop.Cancel();
};
try
{
    if(args.Length==0)throw new ArgumentException("Usage: GalaxyCatalogue build PATH | verify PATH | benchmark PATH | resolve GLYPH-GLYPH-GLYPH-GLYPH-GLYPH-GLYPH | explain PATH SAVE_DIRECTORY UNIVERSE GLYPHS | named");
    var timer=Stopwatch.StartNew();
    switch(args[0])
    {
        case "build":Catalogue.Build(args[1],Console.WriteLine,stop.Token);
break;
        case "verify":using(var catalogue=new Catalogue(args[1]))Console.WriteLine($"Verified {catalogue.Count:N0} entries; home {string.Join('-',Address.Symbols(catalogue.HomeKey()))}; {catalogue.Bytes:N0} bytes; {catalogue.Checksum}");
break;
        case "resolve":var result=Address.Resolve(args[1].Split('-').Select(int.Parse).ToArray());
Console.WriteLine($"{result.Key:x9}: {result.Position} ly; |det|={Math.Abs(result.Determinant):G17}; DHD={Address.DhdCompatible(result.Key)}");
PrintSymbols(Address.Symbols(result.Key));
break;
        case "explain":
            using(var catalogue=new Catalogue(args[1]))
            await using(var repository=new WorldRepository(args[2],args[3]))
            {
                var galaxy=await repository.ReadGalaxyAsync()??throw new InvalidOperationException("This universe has not adopted a galaxy yet.");
                var solution=Address.Resolve(args[4].Split('-').Select(int.Parse).ToArray());
                var target=await repository.ResolveGalaxyAsync(galaxy,catalogue,solution.Key);
                Console.WriteLine($"Key {solution.Key:x9}; corrected position {target.Position} ly; correction revision {target.CorrectionRevision}; standard DHD={Address.DhdCompatible(solution.Key)}");
                Console.WriteLine($"Universe {galaxy.UniverseId}; location {target.Association.LocationId}; gate {target.Association.GateId}; DHD {target.Association.DhdId}; powered={target.Association.Powered}; available={target.Association.Available}; ordinary={target.Association.SupportsOrdinary}");
                PrintSymbols(Address.Symbols(solution.Key));
}
            break;
        case "benchmark":
            using(var catalogue=new Catalogue(args[1]))
            {
                var keys=Enumerable.Range(0,10000).Select(i=>catalogue.At(i*1901).Key).ToArray();
                var samples=new double[keys.Length];
                for(int i=0;i<keys.Length;i++){
long start=Stopwatch.GetTimestamp();
_ = catalogue.Find(keys[i]);
samples[i]=Stopwatch.GetElapsedTime(start).TotalMicroseconds;
}
                Array.Sort(samples);
Console.WriteLine($"10,000 exact lookups: mean {samples.Average():F3} us; p95 {samples[9500]:F3} us; max {samples[^1]:F3} us");
                var center=catalogue.Find(keys[5000]).Position;
var lookup=Stopwatch.StartNew();
for(int i=0;i<10000;i++)_ = catalogue.Nearby(center).Count();
                Console.WriteLine($"10,000 acquisition queries: mean {lookup.Elapsed.TotalMicroseconds/10000:F3} us");
}
            break;
        case "named":foreach(var route in Address.Data.Routes){
Console.WriteLine($"{route.Record} {route.Name}: {string.Join('-',route.GameSequence)}; {(route.Intergalactic?"unsupported intergalactic":route.DhdCompatible?"standard DHD":"requires unavailable glyph 13")}{(route.GameAuthored?"; game-authored completion":"")}");
PrintSymbols(route.GameSequence);
}
break;
        default:throw new ArgumentException("Unknown catalogue command.");
    }
    Console.WriteLine($"Elapsed {timer.Elapsed.TotalSeconds:F3}s; peak working set {Process.GetCurrentProcess().PeakWorkingSet64/1048576.0:F1} MiB");
}
catch(Exception ex){
Console.Error.WriteLine(ex.Message);
Environment.ExitCode=1;
}
static void PrintSymbols(int[] symbols)=>Console.WriteLine(string.Join(" / ",symbols.Select(s=>$"{s:00} {DhdInteraction.Name(s)}")));
