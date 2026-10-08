using TrackGeometry;
using System.Numerics;

namespace RaceAiPlugin.Core;

/// <summary>
/// The drivable surface of a track (the physics meshes "1ROAD_xx" etc. whose surface is valid track in surfaces.ini), for the height of the
/// road under a car. The AI line only knows a flat, tilted road across its width; a crowned or uneven road is higher or lower beside the line.
/// </summary>
public sealed class RoadSurface
{
    /// <summary>A surface of surfaces.ini: grip (FRICTION, 1 = road) and the speed it eats (DAMPING, gravel and sand).</summary>
    public readonly record struct Surface(string Key, float Friction, float Damping, bool Valid);

    private const float Cell = 4f;
    private readonly List<float> _tri = new(); // 9 floats per triangle
    private readonly List<short> _surf = new(); // surface per triangle
    private readonly List<Surface> _surfaces = new();
    private readonly Dictionary<long, List<int>> _grid = new();

    public int Triangles => _tri.Count / 9;

    /// <summary>
    /// Loads the physics meshes of a layout: the valid track (<paramref name="valid"/>) or everything beside it (grass, gravel, sand),
    /// null when there are none (track not on this machine, no physics meshes).
    /// </summary>
    public static RoadSurface? Load(string trackRoot, string? layout, bool valid = true)
    {
        var surfaces = Surfaces(trackRoot, layout).Where(x => x.Valid == valid).OrderByDescending(x => x.Key.Length).ToList();
        if (surfaces.Count == 0) return null;
        var road = new RoadSurface();
        road._surfaces.AddRange(surfaces);
        foreach (var file in Kn5Reader.TrackFiles(trackRoot, layout))
        {
            short current = -1;
            Kn5Reader.ReadMeshes(file, name => (current = road.SurfaceOf(name)) >= 0, (a, b, c) => road.Add(a, b, c, current));
        }
        return road.Triangles > 0 ? road : null;
    }

    /// <summary>Physics mesh names: a digit, then the surface key ("1ASPH-NURB_12", "2ROAD"); the longest matching key wins.</summary>
    private short SurfaceOf(string name)
    {
        if (name.Length < 2 || !char.IsDigit(name[0])) return -1;
        int i = 0;
        while (i < name.Length && char.IsDigit(name[i])) i++;
        for (short k = 0; k < _surfaces.Count; k++)
            if (name.AsSpan(i).StartsWith(_surfaces[k].Key, StringComparison.OrdinalIgnoreCase)) return k;
        return -1;
    }

    /// <summary>surfaces.ini of the game (system/data: ROAD, GRASS, SAND ...), then the track's and the layout's (they override).</summary>
    private static List<Surface> Surfaces(string trackRoot, string? layout)
    {
        var byKey = new Dictionary<string, Surface>(StringComparer.OrdinalIgnoreCase);
        string contentRoot = Path.GetFullPath(Path.Join(trackRoot, "..", "..", ".."));
        foreach (var path in new[]
                 {
                     Path.Join(contentRoot, "system", "data", "surfaces.ini"), Path.Join("system", "data", "surfaces.ini"),
                     Path.Join(trackRoot, "data", "surfaces.ini"), string.IsNullOrEmpty(layout) ? null : Path.Join(trackRoot, layout, "data", "surfaces.ini")
                 })
        {
            if (path == null || !File.Exists(path)) continue;
            var ini = IniFile.Load(path);
            foreach (var sec in ini.Sections)
                if (ini.Get(sec, "KEY") is { Length: > 0 } key)
                    byKey[key.Trim()] = new Surface(key.Trim(), ini.GetFloat(sec, "FRICTION", 1), ini.GetFloat(sec, "DAMPING", 0), ini.Get(sec, "IS_VALID_TRACK")?.Trim() == "1");
        }
        return byKey.Values.ToList();
    }

    private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

    private void Add(Vector3 a, Vector3 b, Vector3 c, short surface)
    {
        // skip walls and other steep faces
        var n = Vector3.Cross(b - a, c - a);
        if (n.LengthSquared() < 1e-8f || MathF.Abs(n.Y) / n.Length() < 0.5f) return;
        int id = _tri.Count / 9;
        _tri.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z]);
        _surf.Add(surface);
        int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Cell), x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Cell);
        int z0 = (int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / Cell), z1 = (int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / Cell);
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
        {
            if (!_grid.TryGetValue(Key(x, z), out var list)) _grid[Key(x, z)] = list = new List<int>(8);
            list.Add(id);
        }
    }

    /// <summary>Height of the road at (x, z), the surface closest to <paramref name="nearY"/> (bridges, tunnels) within <paramref name="range"/> m, else null.</summary>
    public float? HeightAt(float x, float z, float nearY, float range = 1.5f) => Find(x, z, nearY, range, out _);

    /// <summary>The surface at (x, z) closest to <paramref name="nearY"/>, null when there is none (beyond the meshes).</summary>
    public Surface? SurfaceAt(float x, float z, float nearY, float range = 2f)
        => Find(x, z, nearY, range, out int id) != null ? _surfaces[_surf[id]] : null;

    private float? Find(float x, float z, float nearY, float range, out int bestId)
    {
        bestId = -1;
        if (!_grid.TryGetValue(Key((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)), out var list)) return null;
        float? best = null;
        foreach (int id in list)
        {
            int o = id * 9;
            float ax = _tri[o], ay = _tri[o + 1], az = _tri[o + 2], bx = _tri[o + 3], by = _tri[o + 4], bz = _tri[o + 5], cx = _tri[o + 6], cy = _tri[o + 7], cz = _tri[o + 8];
            float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (MathF.Abs(d) < 1e-9f) continue;
            float u = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / d;
            float v = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / d;
            if (u < -1e-4f || v < -1e-4f || u + v > 1.0001f) continue;
            float y = u * ay + v * by + (1 - u - v) * cy;
            if (MathF.Abs(y - nearY) > range) continue;
            if (best == null || MathF.Abs(y - nearY) < MathF.Abs(best.Value - nearY)) { best = y; bestId = id; }
        }
        return best;
    }
}

/// <summary>
/// For every point of the AI line: how much higher (+) or lower the real road is beside the line than the line's flat road, from -10 to +10 m
/// in 1 m steps. On the line itself it is 0, so a car on the line sits where it always did.
/// </summary>
public sealed class LineHeights
{
    private const int Side = 10, Cols = 2 * Side + 1;
    private readonly RacingLine _line;
    private readonly float[] _d;

    private LineHeights(RacingLine line, float[] d)
    {
        _line = line;
        _d = d;
    }

    public static LineHeights Build(RacingLine line, RoadSurface road)
    {
        var d = new float[line.Count * Cols];
        Parallel.For(0, line.Count, i =>
        {
            var p0 = line.Position[i];
            var lat = line.Lateral[i];
            var row = new float?[Cols];
            float? baseH = road.HeightAt(p0.X, p0.Z, p0.Y);
            for (int k = 0; k < Cols; k++)
            {
                var p = p0 + lat * (k - Side);
                if (baseH != null && road.HeightAt(p.X, p.Z, p.Y + baseH.Value - p0.Y) is { } h)
                    row[k] = Math.Clamp(h - p.Y - (baseH.Value - p0.Y), -0.8f, 0.8f);
            }
            // beside the road (grass, walls) or no mesh: the nearest value towards the line
            for (int k = Side; k < Cols; k++) d[i * Cols + k] = row[k] ?? (k == Side ? 0 : d[i * Cols + k - 1]);
            for (int k = Side - 1; k >= 0; k--) d[i * Cols + k] = row[k] ?? d[i * Cols + k + 1];
        });
        return new LineHeights(line, d);
    }

    /// <summary>Height correction (m) at distance <paramref name="s"/> and lateral <paramref name="offset"/> from the line.</summary>
    public float At(float s, float offset)
    {
        _line.Interp(s, out int a, out int b, out float t);
        float x = Math.Clamp(offset + Side, 0, Cols - 1.001f);
        int k = (int)x;
        float f = x - k;
        float Row(int i) => _d[i * Cols + k] * (1 - f) + _d[i * Cols + k + 1] * f;
        return Row(a) * (1 - t) + Row(b) * t;
    }

    /// <summary>Largest correction anywhere (for the log).</summary>
    public float Max => _d.Max(MathF.Abs);
}
