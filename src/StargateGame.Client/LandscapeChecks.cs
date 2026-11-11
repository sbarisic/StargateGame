using System.Numerics;
using FishGfx.Graphics;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public bool LandscapeDiagnostics
    {
        get;
        set;
    }
    (string? Palette, int Size, int Revision) landscapeChecked;
    ShaderProgram? landscapeProbe;
    bool instancingChecked;
    void CheckInstancing(RenderFrame frame, RenderView view)
    {
        if (instancingChecked)
            return;
        instancingChecked = true;
        var program = LoadProgram("instancing_probe.vert", "instancing_probe.frag");
        using var target = graphics.CreateRenderTarget(new(2, 1, [TextureFormat.RGBA8Unorm], null));
        Vector3[] corners = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        bool valid = true;
        foreach (string mode in new[] { "arrays", "indexed", "streamed", "packed" })
        {
            using var mesh = graphics.CreateMesh3D();
            if (mode == "streamed")
            {
                foreach (bool _ in mesh.UploadGeometry(corners, Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(), new Vector2[4], indices, maximumWriteBytes: 32)) { }
            }
            else if (mode == "packed")
            {
                using var memory = new MemoryStream();
                using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
                    foreach (uint i in indices)
                    {
                        writer.Write(corners[i].X); writer.Write(corners[i].Y); writer.Write(corners[i].Z);
                        writer.Write(0f); writer.Write(0f); writer.Write(1f); writer.Write(0f); writer.Write(0f);
                    }
                foreach (bool _ in mesh.UploadPositionNormalUv(memory.ToArray(), 0, 6, 32)) { }
            }
            else
            {
                mesh.SetVertices(mode == "indexed" ? corners : indices.Select(i => corners[i]).ToArray());
                if (mode == "indexed") mesh.SetElements(indices);
            }
            using var pass = frame.BeginPass(target, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear });
            pass.DrawMeshInstances(mesh, 0, white, program);
            pass.DrawMeshInstances(mesh, 2, white, program);
            byte[] pixels = new byte[8];
            pass.ReadColorRgba32(pixels);
            valid &= pixels[0] == 255 && pixels[1] == 0 && pixels[4] == 0 && pixels[5] == 255;
        }
        Console.WriteLine($"INSTANCING GPU CHECK arraysElementsStreamedPacked={valid}");
        if (!valid)
            throw new InvalidOperationException("Shared mesh instancing check failed.");
        CheckCompressedTextures(frame, view);
    }
    void CheckCompressedTextures(RenderFrame frame, RenderView view)
    {
        using var vertex = graphics.CreateShaderStage(ShaderStageType.Vertex,
            "#version 400 core\nlayout(location=0) in vec3 p; void main(){gl_Position=vec4(p.xy,0,1);}");
        using var fragment = graphics.CreateShaderStage(ShaderStageType.Fragment,
            "#version 400 core\nuniform sampler2D uTexture; uniform float uLod; out vec4 color; void main(){color=textureLod(uTexture,vec2(.5),uLod);}");
        using var program = graphics.CreateShaderProgram(vertex, fragment);
        using var target = graphics.CreateRenderTarget(new(1, 1, [TextureFormat.RGBA8Unorm], null));
        foreach (var (format, supported, hex, red, green, blue) in new[] {
            (TextureFormat.BC7Unorm, graphics.Capabilities.SupportsBc7, "0880402010140a85c2e17038acaaaaaa", 64, 160, 224),
            (TextureFormat.BC5Unorm, graphics.Capabilities.SupportsBc5, "4040499224499224c0c0499224499224", 64, 192, 0) })
        {
            if (!supported) { Console.WriteLine($"TEXTURE GPU CHECK {format} unsupported; using RGBA compatibility path"); continue; }
            byte[] block = Convert.FromHexString(hex);
            using var texture = graphics.CreateTexture(new TextureDescriptor(7, 5, format, mipLevels: 3));
            for (int mip = 0; mip < 3; mip++)
            {
                int width = Math.Max(1, 7 >> mip), height = Math.Max(1, 5 >> mip);
                byte[] row = Enumerable.Range(0, (width + 3) / 4).SelectMany(_ => block).ToArray();
                for (int y = 0; y < height; y += 4)
                    texture.WriteCompressed(row, new TextureRegion(0, y, width, Math.Min(4, height - y)), mip);
            }
            bool valid = true;
            for (int mip = 0; mip < 3; mip++)
            {
                program.SetUniform("uLod", (float)mip);
                using var pass = frame.BeginPass(target, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear });
                Mesh(pass, screen, texture, program);
                byte[] pixel = new byte[4]; pass.ReadColorRgba32(pixel);
                valid &= Math.Abs(pixel[0] - red) <= 2 && Math.Abs(pixel[1] - green) <= 2 && Math.Abs(pixel[2] - blue) <= 2 && pixel[3] == 255;
            }
            Console.WriteLine($"TEXTURE GPU CHECK {format} oddExtentsAndSmallMips={valid}");
            if (!valid) throw new InvalidOperationException("Compressed texture GPU readback failed: " + format);
        }
    }
    public bool LandscapeChecksComplete => landscapeChecked == (terrainPalette, terrainSize, surfaceRevision) && terrainSize > 1;
    void CheckLandscape(RenderFrame frame, RenderView view)
    {
        var key = (terrainPalette, terrainSize, surfaceRevision);
        if (!LandscapeDiagnostics || landscapeChecked == key || surfaceMaps.Count == 0)
            return;
        CheckInstancing(frame, view);
        // Placeholder maps are usable while the final palette streams in.
        if (terrainSize <= 1 || terrainPalette is null) return;
        landscapeChecked = key;
        landscapeProbe ??= LoadProgram("fullscreen.vert", "landscape_probe.frag");
        var p = landscapeProbe;
        Lighting(p);
        using var target = graphics.CreateRenderTarget(new(1, 1, [TextureFormat.RGBA8Unorm], null));
        byte[] Sample(Vector3 point, int mode)
        {
            p.SetUniform("uPoint", point);
            p.SetUniform("uMode", mode);
            byte[] pixel = new byte[4];
            using var pass = frame.BeginPass(target, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear });
            Mesh(pass, screen, white, p);
            pass.ReadColorRgba32(pixel);
            return pixel;
        }
        bool materials = true;
        using (BindTerrainPbr(p))
            for (int i = 0; i < 32; i++)
            {
                var pixel = Sample(new(i * 13 - 200, i * .3f, 53 - i * 17), 0);
                materials &= pixel[0] == 255 && pixel[1] == 0;
                if (pixel[0] != 255 || pixel[1] != 0)
                    Console.WriteLine($"LANDSCAPE invalid sample={i} failureBits={pixel[2]} packedAlpha={pixel[1]}");
            }
        bool shelter = true, emission = true, off = true;
        if (World.Definition.Preset == "desert")
        {
            var room = VisualChecks.WinterRoom(World.Definition.Entities.Single(e => e.Model == "stargate"));
            var point = room + new Vector3(1.5f, 1.0f, 1.5f);
            InteriorUniforms(p, point);
            var inside = Sample(point, 1);
            // The fixture uses the same building planes as normal rendering.
            bool fixture = buildingState.Structures.Any(s => s.Id == Guid.Parse("798105bd-fd0c-4dca-ac7d-aad5edac143a"));
            shelter = !fixture || (WeatherFixtureRoof ? inside[0] == 0 && inside[1] == 0 : inside[0] > 0 && inside[1] == 255);
            using var field = graphics.CreateTexture(new TextureDescriptor(1, 1, TextureFormat.RGBA32Float));
            using (field.Bind(25))
            {
                p.SetUniform("uRoomCount", 0);
                p.SetUniform("uSurfaceEnabled", 1);
                field.Write<Vector4>(new[] { new Vector4(0, 1, point.Y - .3f, 0) }, TextureDataFormat.RGBA32Float);
                emission = Sample(point, 1)[2] == 0;
                field.Write<Vector4>(new[] { new Vector4(0, 0, point.Y - .3f, 1) }, TextureDataFormat.RGBA32Float);
                p.SetUniform("uDustMoving", 0f);
                p.SetUniform("uDustExtinction", .0012f);
                var baseline = Sample(point, 1);
                off = baseline[0] > 0;
                p.SetUniform("uDustExtinction", 0f);
                off &= Sample(point, 1)[0] == 0;
            }
        }
        Console.WriteLine($"LANDSCAPE GPU CHECK palette={terrainPalette} size={terrainSize} materialsAndPackedAlpha={materials} shelter={shelter} emissionExclusions={emission} baselineAndOff={off} revision={surfaceRevision}");
        if (!materials || !shelter || !emission || !off)
            throw new InvalidOperationException("Landscape GPU check failed.");
    }
}
