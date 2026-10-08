using System.Numerics;

namespace TrackGeometry;

/// <summary>
/// The pit lane of a track (Kunos pit_lane.ai): an open path that leaves the racing line before the pits and joins it again after.
/// Resampled to 1 m. Every point knows where it lies relative to the racing line, so cars in the pit lane can still be seen by the others.
/// </summary>
public sealed class PitLane
{
    public int Count { get; }
    public float Length { get; }
    public float Spacing { get; }
    public readonly Vector3[] Position;
    public readonly Vector3[] Forward;
    public readonly Vector3[] Lateral;
    public readonly float[] Curvature;
    /// <summary>Projection of each point onto the racing line.</summary>
    public readonly float[] TrackS;
    public readonly float[] TrackOffset;

    /// <summary>Racing line distance where cars turn into the pit lane / where they are back on the racing line.</summary>
    public float EntryTrackS { get; }
    public float ExitTrackS { get; }
    /// <summary>Pit lane distance where the speed limit starts / ends (where the pit lane leaves / rejoins the track).</summary>
    public float LimiterStart { get; }
    public float LimiterEnd { get; }

    public PitLane(FastLanePoint[] raw, RacingLine line, float separation = 2.5f, float limiterMargin = 30f)
    {
        if (raw.Length < 10) throw new ArgumentException("Pit lane needs at least 10 points");
        var rawS = new float[raw.Length];
        for (int i = 1; i < raw.Length; i++)
            rawS[i] = rawS[i - 1] + Vector3.Distance(raw[i - 1].Position, raw[i].Position);
        Length = rawS[^1];
        Count = Math.Max(10, (int)MathF.Round(Length)) + 1;
        Spacing = Length / (Count - 1);

        Position = new Vector3[Count];
        Forward = new Vector3[Count];
        Lateral = new Vector3[Count];
        Curvature = new float[Count];
        TrackS = new float[Count];
        TrackOffset = new float[Count];

        int j = 0;
        for (int i = 0; i < Count; i++)
        {
            float s = i * Spacing;
            while (j < raw.Length - 2 && rawS[j + 1] < s) j++;
            float seg = rawS[j + 1] - rawS[j];
            float t = seg > 1e-4f ? Math.Clamp((s - rawS[j]) / seg, 0, 1) : 0;
            Position[i] = Vector3.Lerp(raw[j].Position, raw[j + 1].Position, t);
        }

        for (int i = 0; i < Count; i++)
        {
            var f = Position[Math.Min(Count - 1, i + 2)] - Position[Math.Max(0, i - 2)];
            Forward[i] = f.LengthSquared() > 1e-6f ? Vector3.Normalize(f) : Vector3.UnitZ;
            var lat = Vector3.Cross(Forward[i], Vector3.UnitY);
            Lateral[i] = lat.LengthSquared() > 1e-6f ? Vector3.Normalize(lat) : Vector3.UnitX;
        }

        for (int i = 0; i < Count; i++)
        {
            int a = Math.Max(0, i - 5), b = Math.Min(Count - 1, i + 5);
            var d1 = Position[i] - Position[a];
            var d2 = Position[b] - Position[i];
            d1.Y = 0;
            d2.Y = 0;
            if (d1.LengthSquared() < 1 || d2.LengthSquared() < 1) continue;
            float cross = d1.X * d2.Z - d1.Z * d2.X;
            float angle = MathF.Asin(Math.Clamp(cross / (d1.Length() * d2.Length()), -1, 1));
            Curvature[i] = angle / (0.5f * (d1.Length() + d2.Length()));
        }

        int hint = -1;
        for (int i = 0; i < Count; i++)
        {
            var p = line.Project(Position[i], hint);
            hint = p.Index;
            TrackS[i] = p.S;
            TrackOffset[i] = p.Offset;
        }

        EntryTrackS = TrackS[0];
        ExitTrackS = TrackS[Count - 1];

        int first = Array.FindIndex(TrackOffset, o => MathF.Abs(o) > separation);
        int last = Array.FindLastIndex(TrackOffset, o => MathF.Abs(o) > separation);
        if (first < 0) { first = Count / 3; last = 2 * Count / 3; }
        LimiterStart = MathF.Max(0, first * Spacing - limiterMargin);
        LimiterEnd = MathF.Min(Length, last * Spacing + limiterMargin);
    }

    public int IndexAt(float s) => Math.Clamp((int)(s / Spacing), 0, Count - 1);

    public void Interp(float s, out int a, out int b, out float t)
    {
        float x = Math.Clamp(s / Spacing, 0, Count - 1.0001f);
        a = (int)x;
        b = Math.Min(Count - 1, a + 1);
        t = x - a;
    }

    public Vector3 PositionAt(float s, float offset)
    {
        Interp(s, out var a, out var b, out var t);
        return Vector3.Lerp(Position[a], Position[b], t) + Vector3.Normalize(Vector3.Lerp(Lateral[a], Lateral[b], t)) * offset;
    }

    public Vector3 ForwardAt(float s)
    {
        Interp(s, out var a, out var b, out var t);
        return Vector3.Normalize(Vector3.Lerp(Forward[a], Forward[b], t));
    }

    public float TrackSAt(float s, RacingLine line)
    {
        Interp(s, out var a, out var b, out var t);
        return line.WrapS(TrackS[a] + line.Delta(TrackS[a], TrackS[b]) * t);
    }

    public float TrackOffsetAt(float s)
    {
        Interp(s, out var a, out var b, out var t);
        return TrackOffset[a] + (TrackOffset[b] - TrackOffset[a]) * t;
    }

    /// <summary>Distance along the pit lane closest to a world position and the lateral offset of that position (e.g. a pit box).</summary>
    public (float S, float Offset) Project(Vector3 pos)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < Count; i++)
        {
            float d = Vector3.DistanceSquared(pos, Position[i]);
            if (d < bestD) { bestD = d; best = i; }
        }
        var rel = pos - Position[best];
        return (Math.Clamp(best * Spacing + Vector3.Dot(rel, Forward[best]), 0, Length), Vector3.Dot(rel, Lateral[best]));
    }
}
