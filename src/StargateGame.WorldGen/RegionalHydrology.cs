namespace StargateGame.WorldGen;

/// <summary>Metre-aware deterministic drainage on a regular grid.</summary>
// Adapted from the D8 analysis and priority-flood routing in SLHydrology
// by Michael Tuck (2024), MIT. See thirdparty/slterraingeneration/LICENSE.
// RegionalErosion supplies USPED, distributed runoff and channel shaping.
public static class RegionalHydrology
{
    static readonly (int X, int Z)[] Neighbors =
        [(-1,-1),(0,-1),(1,-1),(-1,0),(1,0),(-1,1),(0,1),(1,1)];

    public static float Distance(int a, int b, int width, float cellSize)
    {
        int dx = a % width - b % width, dz = a / width - b / width;
        return cellSize * MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <returns>Outlet-first topological order, suitable for reverse accumulation.</returns>
    public static int[] Route(float[] heights, int width, float cellSize,
        int[] downstream, int[] accumulation, CancellationToken token = default, float[]? drainageSurface = null)
    {
        int count = heights.Length, rows = count / width;
        if (width < 2 || rows < 2 || count % width != 0 || !float.IsFinite(cellSize) || cellSize <= 0 ||
            downstream.Length != count || accumulation.Length != count || drainageSurface is not null && drainageSurface.Length != count)
            throw new ArgumentException("Invalid drainage grid.");
        token.ThrowIfCancellationRequested();
        var filled = new float[count];
        var rank = new int[count]; Array.Fill(rank, -1);
        var visited = new bool[count];
        var order = new int[count]; int used = 0;
        var queue = new PriorityQueue<int, (float Height, int Index)>();
        Array.Fill(downstream, -1); Array.Fill(accumulation, 1);
        for (int z = 0; z < rows; z++) for (int x = 0; x < width; x++)
            if (x == 0 || z == 0 || x == width - 1 || z == rows - 1)
            {
                int i = z * width + x;
                visited[i] = true; filled[i] = heights[i]; queue.Enqueue(i, (filled[i], i));
            }
        while (queue.TryDequeue(out int i, out _))
        {
            if ((used & 4095) == 0) token.ThrowIfCancellationRequested();
            rank[i] = used; order[used++] = i;
            int x = i % width, z = i / width;
            foreach (var (dx, dz) in Neighbors)
            {
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= width || nz >= rows) continue;
                int j = nz * width + nx;
                if (visited[j]) continue;
                visited[j] = true; downstream[j] = i;
                // Only the routing surface fills sinks; actual lakes retain their basins.
                filled[j] = Math.Max(heights[j], MathF.BitIncrement(filled[i]));
                queue.Enqueue(j, (filled[j], j));
            }
        }
        for (int k = used - 1; k >= 0; k--)
        {
            if ((k & 4095) == 0) token.ThrowIfCancellationRequested();
            int i = order[k], x = i % width, z = i / width;
            if (downstream[i] < 0) continue; // Boundary outlets stay outlets.
            float best = -1;
            foreach (var (dx, dz) in Neighbors)
            {
                int j = (z + dz) * width + x + dx; // Interior cells only.
                if (rank[j] >= rank[i]) continue;
                float slope = (filled[i] - filled[j]) / Distance(i, j, width, cellSize);
                if (slope > best) { best = slope; downstream[i] = j; }
            }
            accumulation[downstream[i]] += accumulation[i];
        }
        if (drainageSurface is not null) Array.Copy(filled, drainageSurface, count);
        return order;
    }

}
