namespace RaceAiPlugin.Core;

/// <summary>
/// The track's second line (data/ideal_line.ai, the line drawn in the game's driving aid) as a lateral offset from the AI line at every
/// point of it. Bots drive their own mix of both. Many tracks ship a copy of the AI line: then there is nothing to mix.
/// </summary>
public sealed class IdealLine
{
    private readonly RacingLine _line;
    private readonly float[] _offset;

    private IdealLine(RacingLine line, float[] offset)
    {
        _line = line;
        _offset = offset;
    }

    public float At(float s)
    {
        _line.Interp(s, out int a, out int b, out float t);
        return _offset[a] * (1 - t) + _offset[b] * t;
    }

    /// <summary>Null when the file is missing or the same line as the AI line.</summary>
    public static IdealLine? Load(string path, RacingLine line)
    {
        if (!File.Exists(path)) return null;
        var pts = FastLaneFile.Read(path);
        var sum = new float[line.Count];
        var cnt = new int[line.Count];
        int hint = -1;
        foreach (var p in pts)
        {
            var pr = line.Project(p.Position, hint);
            hint = pr.Index;
            // a few metres at most (crossovers, pit entries in the file are no line to follow)
            if (MathF.Abs(pr.Offset) > 4) continue;
            sum[pr.Index] += pr.Offset;
            cnt[pr.Index]++;
        }
        if (cnt.Sum() < line.Count / 4) return null;
        // fill the gaps between the points of the file, then smooth over ~20 m and limit to 2.5 m
        var raw = new float[line.Count];
        int last = -1;
        for (int i = 0; i < line.Count; i++)
            if (cnt[i] > 0) raw[i] = sum[i] / cnt[i];
        var known = Enumerable.Range(0, line.Count).Where(i => cnt[i] > 0).ToList();
        for (int k = 0; k < known.Count; k++)
        {
            int i0 = known[k], i1 = known[(k + 1) % known.Count];
            int gap = (i1 - i0 + line.Count) % line.Count;
            for (int g = 1; g < gap; g++) raw[(i0 + g) % line.Count] = raw[i0] + (raw[i1] - raw[i0]) * g / gap;
            last = i1;
        }
        _ = last;
        int w = Math.Max(1, (int)MathF.Round(10f / line.Spacing));
        var off = new float[line.Count];
        for (int i = 0; i < line.Count; i++)
        {
            float a = 0;
            for (int d = -w; d <= w; d++) a += raw[(i + d + line.Count) % line.Count];
            off[i] = Math.Clamp(a / (2 * w + 1), -2.5f, 2.5f);
        }
        return off.Max(MathF.Abs) < 0.05f ? null : new IdealLine(line, off);
    }
}
