using System.Numerics;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public bool WeatherDiagnostics
    {
        get;
        set;
    }
    public bool WeatherFixtureRoof
    {
        get;
        set;
    } = true;
    bool? weatherChecked;
    public bool WeatherChecksComplete => weatherChecked == WeatherFixtureRoof && mistGround is not null;
    void CheckWeatherShelter(RenderFrame frame, RenderView view)
    {
        if (!WeatherDiagnostics || weatherChecked == WeatherFixtureRoof || weatherShelter is null || PendingMeshes > 0 || ResidentMeshes < 256)
            return;
        weatherChecked = WeatherFixtureRoof;
        var probe = LoadProgram("fullscreen.vert", "precipitation_probe.frag");
        probe.SetUniform("uShelter", 0);
        probe.SetUniform("uShelterMatrix", shelterMatrix);
        probe.SetUniform("uShelterInverse", shelterInverse);
        using var target = graphics.CreateRenderTarget(new(1, 1, [TextureFormat.RGBA8Unorm], null));
        bool Blocked(Vector3 point)
        {
            byte[] pixel = new byte[4];
            probe.SetUniform("uPoint", point);
            using var pass = frame.BeginPass(target, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare });
            Mesh(pass, screen, weatherShelter.DepthStencilAttachment, probe);
            pass.ReadColorRgba32(pixel);
            return pixel[0] > 127;
        }
        var room = VisualChecks.WinterRoom(World.Definition.Entities.Single(e => e.Model == "stargate"));
        bool inside = Blocked(room + new Vector3(1.5f, 1.5f, 1.5f)), above = Blocked(room + new Vector3(1.5f, 4, 1.5f)), below = Blocked(room + new Vector3(1.5f, -.1f, 1.5f));
        Console.WriteLine($"WEATHER GPU CHECK roof={WeatherFixtureRoof} insideBlocked={inside} aboveBlocked={above} floorBlocked={below} map={shelterResolution}");
        if (inside != WeatherFixtureRoof || above || !below)
            throw new InvalidOperationException("GPU precipitation shelter check failed.");
        var drift = LoadProgram("fullscreen.vert", "blowing_snow_probe.frag");
        drift.SetUniform("uShelter", 0);
        drift.SetUniform("uShelterMatrix", shelterMatrix);
        drift.SetUniform("uShelterInverse", shelterInverse);
        float Density(Vector3 point, float travel, float strength = .18f)
        {
            byte[] pixel = new byte[4];
            drift.SetUniform("uPoint", point);
            drift.SetUniform("uSnowTravel", travel);
            drift.SetUniform("uBlowingSnowDensity", strength);
            using var pass = frame.BeginPass(target, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare });
            Mesh(pass, screen, weatherShelter.DepthStencilAttachment, drift);
            pass.ReadColorRgba32(pixel);
            return pixel[0];
        }
        var nearFloor = room + new Vector3(1.5f, .5f, 1.5f);
        var nearRoof = room + new Vector3(1.5f, 3.3f, 1.5f);
        var exposed = WeatherFixtureRoof ? nearRoof : nearFloor;
        var samples = Enumerable.Range(0, 8).Select(i => Density(exposed, i * 5)).ToArray();
        bool sheltered = !WeatherFixtureRoof || Density(nearFloor, 0) == 0;
        bool animated = samples.Max() > 0 && samples.Distinct().Count() > 1;
        bool wrapped = Density(exposed, 0) == Density(exposed, 1024);
        bool bounded = Density(exposed + Vector3.UnitY * 4, 0) == 0 && Density(exposed, 0, 0) == 0;
        Console.WriteLine($"SNOW VOLUME GPU CHECK roof={WeatherFixtureRoof} sheltered={sheltered} animated={animated} windWrap={wrapped} heightAndOff={bounded}");
        if (!sheltered || !animated || !wrapped || !bounded)
            throw new InvalidOperationException("GPU blowing-snow volume check failed.");
        if (mistGround is not null)
            CheckGroundMist(frame, view, room);
    }

    void CheckGroundMist(RenderFrame frame, RenderView view, Vector3 room)
    {
        var probe = LoadProgram("fullscreen.vert", "ground_mist_probe.frag");
        using var target = graphics.CreateRenderTarget(new(1, 1, [TextureFormat.RGBA8Unorm], null));
        using var missingMap = graphics.CreateRenderTarget(new(mistGround!.Width, mistGround.Height, [], TextureFormat.Depth32Float));
        using (var clear = frame.BeginPass(missingMap, new RenderPassDescriptor { View = view, State = OpaqueState, DepthLoadAction = RenderLoadAction.Clear }))
        {
        }
        InteriorUniforms(probe, room);
        byte[] Sample(Vector3 point, float height, float travel = 0, float? strength = null, Vector3? eye = null, bool missing = false)
        {
            GroundMistUniforms(probe, 0);
            probe.SetUniform("uPoint", point);
            probe.SetUniform("uRelativeHeight", height);
            probe.SetUniform("uMistTravel", travel);
            if (strength is { } densityOverride)
                probe.SetUniform("uGroundMistDensity", densityOverride);
            probe.SetUniform("uEye", eye ?? point);
            byte[] pixel = new byte[4];
            using var pass = frame.BeginPass(target, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare });
            Mesh(pass, screen, missing ? missingMap.DepthStencilAttachment : mistGround!.DepthStencilAttachment, probe);
            pass.ReadColorRgba32(pixel);
            return pixel;
        }
        var point = room + new Vector3(1.5f, .5f, 1.5f);
        // Raw terrain-anchored density must remain below the roof in BOTH fixture states.
        var low = Sample(point, .25f);
        float density = low[0] / (255f * 4);
        float halfDistance = MathF.Log(2) / density;
        bool blanket = density > 0 && halfDistance >= 5 && halfDistance <= 8;
        bool heights = Sample(point, 0)[0] == 0 && Sample(point, 1.2f)[0] == 0 && Sample(point, 1.65f)[0] == 0;
        bool smooth = Sample(point, .59f)[0] <= Sample(point, .3f)[0] && Sample(point, 1.19f)[0] <= Sample(point, .59f)[0];
        var moving = Enumerable.Range(0, 8).Select(i => Sample(point, .25f, i * 8)[0]).ToArray();
        bool animated = moving.Distinct().Count() > 1;
        bool wraps = Sample(point, .25f, 0)[0] == Sample(point, .25f, 4096)[0];
        bool continuous = Math.Abs(Sample(point, .25f, 4095.99f)[0] - Sample(point, .25f, 0)[0]) <= 1;
        bool disabled = Sample(point, .25f, strength: 0)[0] == 0;
        bool missing = Sample(point, .25f, missing: true)[0] == 0 && Sample(point + new Vector3(300, 0, 0), .25f)[0] == 0;
        int near = Sample(point, .25f, eye: point + new Vector3(95, 0, 0))[0], edge = Sample(point, .25f, eye: point + new Vector3(108, 0, 0))[0];
        bool fades = near > edge && edge > 0 && Sample(point, .25f, eye: point + new Vector3(120, 0, 0))[0] == 0;
        bool interior = Sample(point, -1)[2] == (WeatherFixtureRoof ? 0 : 255) &&
            Sample(room + new Vector3(1.5f, .02f, 1.5f), -1)[2] == (WeatherFixtureRoof ? 0 : 255);
        bool noRoofMist = Sample(room + new Vector3(1.5f, 3.25f, 1.5f), -1)[0] == 0;
        Console.WriteLine($"GROUND MIST GPU CHECK roof={WeatherFixtureRoof} weather={WeatherSettings.Mode} blanket={blanket} halfDistance={halfDistance:F2} heights={heights} smooth={smooth} animated={animated} wrap={wraps && continuous} off={disabled} missing={missing} fade={fades} room={interior} noRoofMist={noRoofMist} map={mistGround!.Width} bytes={GroundMistBytes}");
        if (!blanket || !heights || !smooth || !animated || !wraps || !continuous || !disabled || !missing || !fades || !interior || !noRoofMist)
            throw new InvalidOperationException("GPU ground-mist check failed.");
    }
}
