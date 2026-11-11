using FishGfx;
using FishGfx.Graphics;
namespace StargateGame.Client;

// The IDs are DHD/RDA IDs, not zero-based reference-coordinate indices.
sealed class GlyphRenderer(GraphicsContext graphics) : IDisposable
{
    readonly Texture atlas = graphics.LoadTexture(Path.Combine(AppContext.BaseDirectory, "assets/gui/stargate-glyphs.png"), new TextureLoadOptions { MipLevels = 4, Sampling = new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear) });
    public void Draw(RenderPass pass, int id, float x, float y, float size, float viewportHeight, Color colour)
    {
        if (id is < 1 or > 39)
            return;
        int index = id - 1, col = index % 8, row = index / 8;
        pass.DrawTexturedRectangle(x, viewportHeight - y - size, size, size, col / 8f, 1 - (row + 1) / 5f, (col + 1) / 8f, 1 - row / 5f, colour, atlas);
    }
    public void Address(RenderPass pass, IReadOnlyList<int> symbols, float x, float y, float size, float viewportHeight, Color colour)
    {
        for (int i = 0; i < symbols.Count; i++)
            Draw(pass, symbols[i], x + i * (size + 6), y, size, viewportHeight, colour);
    }
    public void Dispose() => atlas.Dispose();
}
