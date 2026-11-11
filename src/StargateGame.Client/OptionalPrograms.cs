using System.Diagnostics;
using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    readonly Queue<(string Vertex, string Fragment)> optionalPrograms = [];
    readonly Dictionary<(string, string), ShaderProgram?> optionalProgramResults = [];

    // Draws request optional effects and use their existing fallback until complete.
    // GL program creation stays on the context thread; a driver call is indivisible.
    ShaderProgram? RequestOptionalProgram(string vertex, string fragment)
    {
        var key = (vertex, fragment);
        if (optionalProgramResults.TryGetValue(key, out var result)) return result;
        optionalProgramResults.Add(key, null);
        optionalPrograms.Enqueue(key);
        return null;
    }

    void PumpOptionalProgram(double remainingMs)
    {
        if (remainingMs <= 0 || !optionalPrograms.TryDequeue(out var request)) return;
        var elapsed = Stopwatch.StartNew();
        optionalProgramResults[request] = LoadProgram(request.Vertex, request.Fragment);
        StartupTrace.Mark("optional-shader-ready", elapsed.Elapsed.TotalMilliseconds, request.Fragment);
        if (elapsed.Elapsed.TotalMilliseconds > remainingMs)
            StartupTrace.Mark("shader-admission-overrun", elapsed.Elapsed.TotalMilliseconds, request.Fragment);
    }
}
