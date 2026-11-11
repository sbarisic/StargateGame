using System.Numerics;
using System.Text.Json;
using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    ShaderProgram realisticWaterShader = null!, legacyWaterShader = null!;
    Texture? waterNormals;
    long waterTextureBytes;
    public long WaterTextureBytes => waterTextureBytes;
    public bool WaterNormalsReady => waterNormals is not null;
    public double WaterCpuMilliseconds { get; private set; }
    public long WaterAllocatedBytes { get; private set; }
    public bool RealisticWater { get; set; } = true;
    public WaterDiagnostic WaterDebug { get; set; }
    WaterAppearanceSettings waterAppearance = new();
    public WaterAppearanceSettings WaterAppearance
    {
        get => waterAppearance;
        set { ArgumentNullException.ThrowIfNull(value); value.Validate(); waterAppearance = value; }
    }
    void WaterUniforms(ShaderProgram program)
    {
        program.SetUniform("uRealisticWater", RealisticWater ? 1 : 0);
        program.SetUniform("uWaterAbsorption", WaterAppearance.Absorption);
        program.SetUniform("uWaterScattering", WaterAppearance.Scattering);
        program.SetUniform("uWaterBiome", World.Definition.Preset == "icy" ? 2 : World.Definition.Preset == "desert" ? 1 : 0);
        program.SetUniform("uWaterIor", WaterAppearance.RefractiveIndex);
        program.SetUniform("uWaterRoughness", WaterAppearance.Roughness);
        program.SetUniform("uWaterReflectionStrength", WaterAppearance.ReflectionStrength);
        program.SetUniform("uRippleMetres", WaterAppearance.RippleMetres);
        program.SetUniform("uRippleStrength", WaterAppearance.RippleStrength);
        program.SetUniform("uFoamStrength", WaterAppearance.FoamStrength);
        program.SetUniform("uWaterWind", weatherFrame.Wind.LengthSquared() > .001f ? weatherFrame.Wind : new Vector2(.7f, .25f));
        program.SetUniform("uWaterNormals", 8);
        program.SetUniform("uWaterTraceSteps", Settings.Quality == GraphicsQuality.High ? 24 : 12);
        program.SetUniform("uWaterRefineSteps", Settings.Quality == GraphicsQuality.High ? 4 : 3);
        program.SetUniform("uWaterDiagnostic", (int)WaterDebug);
    }

    sealed record WaterMip(int Size, byte[] Pixels);
    WaterMip[] ReadWaterNormals()
    {
        using var doc = JsonDocument.Parse(ReadRuntimeAsset("terrain/water/ripples.json"));
        var root = doc.RootElement;
        int size = root.GetProperty("size").GetInt32();
        if (root.GetProperty("version").GetInt32() != 1 || size < 16 || size > 512 || (size & (size - 1)) != 0)
            throw new InvalidDataException("Invalid water normal descriptor; regenerate the water assets.");
        var mips = new List<WaterMip>();
        foreach (var mip in root.GetProperty("mips").EnumerateArray())
        {
            if (mips.Count >= 1 + (int)Math.Log2(size)) throw new InvalidDataException("Too many water mip levels.");
            int width = Math.Max(1, size >> mips.Count);
            var data = ReadRuntimeAsset("terrain/water/" + mip.GetString());
            if (data.Length != width * width * 4) throw new InvalidDataException("Incomplete water normal mip.");
            mips.Add(new(width, data));
        }
        if (mips.Count != 1 + (int)Math.Log2(size)) throw new InvalidDataException("Incomplete water normal chain.");
        return mips.ToArray();
    }

    IEnumerable<bool> PrepareWaterResources()
    {
        var work = modelAssets.PrepareCpuAsync(ReadWaterNormals, startupStop.Token);
        while (!work.IsCompleted) yield return false;
        // No runtime image conversion or mip generation. A flat normal remains
        // usable until the complete replacement has been uploaded.
        var mips = work.GetAwaiter().GetResult();
        var texture = Own(graphics.CreateTexture(new TextureDescriptor(mips[0].Size, mips[0].Size, TextureFormat.RGBA8Unorm,
            mipLevels: mips.Length, sampling: new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear, TextureWrap.Repeat, TextureWrap.Repeat))));
        for (int level = 0; level < mips.Length; level++)
            foreach (bool step in UploadTextureRows(texture, mips[level].Pixels, mips[level].Size, mips[level].Size, level))
                yield return step;
        waterNormals = texture;
        waterTextureBytes = mips.Sum(m => (long)m.Pixels.Length);
        StartupTrace.Mark("water-normal-detail-ready", detail: $"{mips[0].Size}px/{mips.Length} mips/{waterTextureBytes} bytes");
        yield return true;
    }
}
