using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;

namespace StargateGame.Client;

// One reusable mesh and fixed vertex buffers: menu use never allocates GPU resources.
sealed class UiPainter(GraphicsContext graphics) : IDisposable
{
    readonly Mesh2D fill = graphics.CreateMesh2D(BufferUsage.Stream);
    readonly Vertex2[] triangles = new Vertex2[18];
    readonly Vector2[] corners = new Vector2[8];
    float height;
    Vector2 scale = Vector2.One;
    public void Viewport(float logicalHeight, Vector2 pixelScale)
    {
        height = logicalHeight;
        scale = pixelScale;
    }
    Vector2 Point(float x, float y) => new(MathF.Round(x * scale.X) / scale.X, MathF.Round((height - y) * scale.Y) / scale.Y);
    public void Line(RenderPass pass, float x, float y, float xx, float yy, Color color, float thickness = 1)
        => pass.DrawLine(new Vertex2(Point(x, y), color), new Vertex2(Point(xx, yy), color), thickness);
    public void Frame(RenderPass pass, UiRect r, Color? color = null, bool opaque = false, bool selected = false)
    {
        float c = Math.Min(10, Math.Min(r.W, r.H) / 4);
        corners[0] = Point(r.X + c, r.Y);
        corners[1] = Point(r.Right - c, r.Y);
        corners[2] = Point(r.Right, r.Y + c);
        corners[3] = Point(r.Right, r.Bottom - c);
        corners[4] = Point(r.Right - c, r.Bottom);
        corners[5] = Point(r.X + c, r.Bottom);
        corners[6] = Point(r.X, r.Bottom - c);
        corners[7] = Point(r.X, r.Y + c);
        Color body = color ?? (opaque ? UiTheme.Menu : UiTheme.Panel);
        for (int i = 0; i < 6; i++)
        {
            triangles[i * 3] = new(corners[0], body);
            triangles[i * 3 + 1] = new(corners[i + 1], body);
            triangles[i * 3 + 2] = new(corners[i + 2], body);
        }
        fill.SetVertices(triangles);
        pass.DrawMesh(fill);
        Color edge = selected ? UiTheme.Amber : UiTheme.Line;
        for (int i = 0; i < 8; i++)
            pass.DrawLine(new Vertex2(corners[i], edge), new Vertex2(corners[(i + 1) % 8], edge), 1);
        // Localized glow, never a full-screen effect.
        Line(pass, r.X, r.Y + c, r.X, r.Y + c + 14, new Color(244, 181, 68, 24), 6);
        Line(pass, r.X, r.Y + c, r.X, r.Y + c + 14, UiTheme.Amber, 1.5f);
        Line(pass, r.Right - c - 18, r.Bottom, r.Right - c, r.Bottom, UiTheme.Amber, 1.5f);
    }
    public void Dispose() => fill.Dispose();
}
