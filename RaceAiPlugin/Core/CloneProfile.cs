using System.Numerics;

namespace RaceAiPlugin.Core;

/// <summary>One recorded sample of a player (from the DriverRecorder plugin).</summary>
public readonly record struct RecordedSample(Vector3 Position, float Speed, float Gas, float Brake);

/// <summary>One recorded lap.</summary>
public sealed class RecordedLap
{
    public required float LapTime { get; init; }
    public required bool Valid { get; init; }
    public required List<RecordedSample> Samples { get; init; }
}

/// <summary>
/// How a real player drives the track: speed, line (offset from the racing line) and its spread, throttle and brake,
/// every few metres, averaged over his clean laps. A bot with a clone profile drives like that player.
/// </summary>
public sealed class CloneProfile
{
    public const float BinSize = 4f;

    public string PlayerGuid { get; init; } = "";
    public string PlayerName { get; init; } = "";
    public string Car { get; init; } = "";
    /// <summary>Laps the profile is built from, and all clean laps recorded.</summary>
    public int LapsUsed { get; init; }
    public int CleanLaps { get; init; }
    public float BestLap { get; init; }
    public float AverageLap { get; init; }
    /// <summary>Standard deviation of the lap times of the laps used (s): how consistent the player is.</summary>
    public float LapSpread { get; init; }

    /// <summary>Per bin (track distance / BinSize along the racing line, from s = 0).</summary>
    public required float[] Speed { get; init; }
    public required float[] Offset { get; init; }
    public required float[] OffsetSpread { get; init; }
    public required float[] Gas { get; init; }
    public required float[] Brake { get; init; }
    /// <summary>Spread of the speed between laps (m/s): where the player isn't consistent.</summary>
    public required float[] SpeedSpread { get; init; }

    private float At(float[] a, float s, float length)
    {
        float x = (s % length + length) % length / BinSize;
        int i0 = (int)MathF.Floor(x) % a.Length;
        int i1 = (i0 + 1) % a.Length;
        float t = x - MathF.Floor(x);
        return a[i0] + (a[i1] - a[i0]) * t;
    }

    public float SpeedAt(float s, float length) => At(Speed, s, length);
    public float OffsetAt(float s, float length) => At(Offset, s, length);
    public float OffsetSpreadAt(float s, float length) => At(OffsetSpread, s, length);
    public float GasAt(float s, float length) => At(Gas, s, length);
    public float BrakeAt(float s, float length) => At(Brake, s, length);

    /// <summary>
    /// Builds a profile from recorded laps. Uses the clean laps within 4 % of the best one (outliers with a moment of hesitation
    /// or traffic are left out); returns null when there isn't at least one usable lap.
    /// </summary>
    public static CloneProfile? Build(RacingLine line, IReadOnlyList<RecordedLap> laps, string guid, string name, string car)
    {
        var clean = laps.Where(l => l.Valid && l.Samples.Count > 50 && l.LapTime > 10).ToList();
        if (clean.Count == 0) return null;
        float best = clean.Min(l => l.LapTime);
        var used = clean.Where(l => l.LapTime <= best * 1.04f).OrderBy(l => l.LapTime).Take(12).ToList();

        int bins = (int)MathF.Ceiling(line.Length / BinSize);
        var perLapSpeed = new List<float[]>();
        var perLapOffset = new List<float[]>();
        var perLapGas = new List<float[]>();
        var perLapBrake = new List<float[]>();

        foreach (var lap in used)
        {
            var sumV = new float[bins]; var sumO = new float[bins]; var sumG = new float[bins]; var sumB = new float[bins];
            var n = new int[bins];
            int hint = -1;
            foreach (var smp in lap.Samples)
            {
                var p = line.Project(smp.Position, hint);
                hint = p.Index;
                if (MathF.Abs(p.Offset) > 25) continue; // off in the pits or a glitch
                int b = (int)(line.WrapS(p.S) / BinSize) % bins;
                sumV[b] += smp.Speed; sumO[b] += p.Offset; sumG[b] += smp.Gas; sumB[b] += smp.Brake;
                n[b]++;
            }
            if (n.Count(c => c > 0) < bins * 0.3f) continue; // too sparse
            perLapSpeed.Add(Fill(sumV, n)); perLapOffset.Add(Fill(sumO, n)); perLapGas.Add(Fill(sumG, n)); perLapBrake.Add(Fill(sumB, n));
        }
        if (perLapSpeed.Count == 0) return null;

        float[] Mean(List<float[]> src) { var r = new float[bins]; foreach (var a in src) for (int i = 0; i < bins; i++) r[i] += a[i] / src.Count; return r; }
        float[] Spread(List<float[]> src, float[] mean)
        {
            var r = new float[bins];
            if (src.Count < 2) return r;
            foreach (var a in src) for (int i = 0; i < bins; i++) r[i] += (a[i] - mean[i]) * (a[i] - mean[i]) / (src.Count - 1);
            for (int i = 0; i < bins; i++) r[i] = MathF.Sqrt(r[i]);
            return r;
        }

        var speed = Smooth(Mean(perLapSpeed), 1);
        var offset = Smooth(Mean(perLapOffset), 3);
        var times = used.Select(l => l.LapTime).ToList();
        float avg = times.Average();
        return new CloneProfile
        {
            PlayerGuid = guid,
            PlayerName = name,
            Car = car,
            LapsUsed = perLapSpeed.Count,
            CleanLaps = clean.Count,
            BestLap = best,
            AverageLap = avg,
            LapSpread = times.Count > 1 ? MathF.Sqrt(times.Sum(t => (t - avg) * (t - avg)) / (times.Count - 1)) : 0,
            Speed = speed,
            SpeedSpread = Smooth(Spread(perLapSpeed, Mean(perLapSpeed)), 3),
            Offset = offset,
            OffsetSpread = Smooth(Spread(perLapOffset, Mean(perLapOffset)), 3),
            Gas = Smooth(Mean(perLapGas), 1),
            Brake = Smooth(Mean(perLapBrake), 1)
        };
    }

    /// <summary>Bin averages, gaps filled by linear interpolation (around the lap).</summary>
    private static float[] Fill(float[] sum, int[] n)
    {
        int bins = sum.Length;
        var r = new float[bins];
        var known = new List<int>();
        for (int i = 0; i < bins; i++)
            if (n[i] > 0) { r[i] = sum[i] / n[i]; known.Add(i); }
        if (known.Count == 0) return r;
        for (int k = 0; k < known.Count; k++)
        {
            int a = known[k], b = known[(k + 1) % known.Count];
            int gap = (b - a + bins) % bins;
            if (gap <= 1) continue;
            for (int j = 1; j < gap; j++)
                r[(a + j) % bins] = r[a] + (r[b] - r[a]) * j / gap;
        }
        return r;
    }

    private static float[] Smooth(float[] a, int radius)
    {
        if (radius <= 0) return a;
        int n = a.Length;
        var r = new float[n];
        for (int i = 0; i < n; i++)
        {
            float s = 0;
            for (int j = -radius; j <= radius; j++) s += a[((i + j) % n + n) % n];
            r[i] = s / (2 * radius + 1);
        }
        return r;
    }
}
