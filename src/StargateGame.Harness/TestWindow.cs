using System.Diagnostics;
using System.Runtime.InteropServices;
using StargateGame.Testing;

namespace StargateGame.Harness;

static class TestWindow
{
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    public static async Task<object> Apply(int pid, string state, CancellationToken token)
    {
        if (state is not ("minimize" or "restore")) throw new ArgumentException("Window state must be minimize or restore.");
        using var process = Process.GetProcessById(pid); process.Refresh();
        nint handle = process.MainWindowHandle;
        if (handle == 0) throw new InvalidOperationException("Selected test client has no window.");
        ShowWindowAsync(handle, state == "minimize" ? 6 : 4); // Restore without activation.
        var watch = Stopwatch.StartNew();
        while (IsIconic(handle) != (state == "minimize"))
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed.TotalSeconds > 5) throw new TimeoutException("Window did not reach " + state);
            await Task.Delay(20, token);
        }
        return new { state, pid };
    }
}
