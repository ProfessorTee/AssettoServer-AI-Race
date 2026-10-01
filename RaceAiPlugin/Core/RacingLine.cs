using System.Numerics;

namespace RaceAiPlugin.Core;

/// <summary>
/// Closed racing line resampled to (nearly) uniform spacing so that lookups by distance are O(1).
/// Coordinates: AC world space, Y is up. "Offset" is the lateral distance from the line in the direction of <see cref="Lateral"/>.
/// </summary>
public sealed class RacingLine
{
    public int Count { get; }
    public float Length { get; }
    public float Spacing { get; }

    public readonly Vector3[] Position;
    public readonly Vector3[] Forward;
    /// <summary>Unit vector along the road surface, perpendicular to Forward. Positive offsets go this way.</summary>
    public readonly Vector3[] Lateral;
    public readonly Vector3[] Normal;
    /// <summary>Usable track width from the line towards +Lateral (meters).</summary>
    public readonly float[] RoomPlus;
    /// <summary>Usable track width from the line towards -Lateral (meters, positive number).</summary>
    public readonly float[] RoomMinus;
    /// <summary>Signed horizontal curvature (1/m). Positive = the line turns towards +Lateral.</summary>
    public readonly float[] Curvature;
    /// <summary>Vertical curvature (1/m). Negative = crest (car gets light), positive = compression.</summary>
    public readonly float[] VerticalCurvature;
    public readonly float[] Camber;
    /// <summary>Speed recorded by Kunos when the line was made (m/s), 0 if unknown.</summary>
    public readonly float[] SpeedHint;
    /// <summary>Speed multiplier from the track's ai_hints.ini (1 = no hint).</summary>
    public readonly float[] HintFactor;
    /// <summary>Speed cap from ai_hints.ini MAXSPEED entries (m/s, +inf = none).</summary>
    public readonly float[] MaxSpeed;

    /// <summary>
    /// True if +Lateral points to the driver's right. In AC world coordinates (Y up) cross(forward, up) is the right-hand side
    /// (verified with the clockwise Nordschleife, whose line turns towards +Lateral overall).
    /// </summary>
    public bool RightIsPlus { get; set; } = true;

    /// <summary>True if the file's SideLeft was found to be on the +Lateral side.</summary>
    public bool SideLeftIsPlus { get; }

    public RacingLine(FastLanePoint[] raw, float spacing = 1.0f)
    {
        if (raw.Length < 10) throw new ArgumentException("Racing line needs at least 10 points");

        // cumulative length of the raw polyline (closed)
        var rawS = new float[raw.Length + 1];
        for (int i = 0; i < raw.Length; i++)
            rawS[i + 1] = rawS[i] + Vector3.Distance(raw[i].Position, raw[(i + 1) % raw.Length].Position);

        Length = rawS[^1];
        Count = Math.Max(10, (int)MathF.Round(Length / spacing));
        Spacing = Length / Count;

        Position = new Vector3[Count];
        Forward = new Vector3[Count];
        Lateral = new Vector3[Count];
        Normal = new Vector3[Count];
        RoomPlus = new float[Count];
        RoomMinus = new float[Count];
        Curvature = new float[Count];
        VerticalCurvature = new float[Count];
        Camber = new float[Count];
        SpeedHint = new float[Count];
        HintFactor = new float[Count];
        MaxSpeed = new float[Count];
        Array.Fill(HintFactor, 1f);
        Array.Fill(MaxSpeed, float.PositiveInfinity);
        var sideA = new float[Count];
        var sideB = new float[Count];

        // resample
        int j = 0;
        for (int i = 0; i < Count; i++)
        {
            float s = i * Spacing;
            while (j < raw.Length - 1 && rawS[j + 1] < s) j++;
            float segLen = rawS[j + 1] - rawS[j];
            float t = segLen > 1e-4f ? (s - rawS[j]) / segLen : 0;
            ref readonly var a = ref raw[j];
            ref readonly var b = ref raw[(j + 1) % raw.Length];
            Position[i] = Vector3.Lerp(a.Position, b.Position, t);
            var n = Vector3.Lerp(a.Normal, b.Normal, t);
            Normal[i] = n.LengthSquared() > 0.25f ? Vector3.Normalize(n) : Vector3.UnitY;
            sideA[i] = Lerp(a.SideLeft, b.SideLeft, t);
            sideB[i] = Lerp(a.SideRight, b.SideRight, t);
            Camber[i] = Lerp(a.Camber, b.Camber, t);
            SpeedHint[i] = Lerp(a.Speed, b.Speed, t);
        }

        // forward / lateral
        for (int i = 0; i < Count; i++)
        {
            var f = Position[Wrap(i + 2)] - Position[Wrap(i - 2)];
            Forward[i] = Vector3.Normalize(f);
            var lat = Vector3.Cross(Forward[i], Normal[i]);
            if (lat.LengthSquared() < 1e-6f) lat = Vector3.Cross(Forward[i], Vector3.UnitY);
            Lateral[i] = Vector3.Normalize(lat);
        }

        // curvature over a ~10m baseline, then smoothed
        int k = Math.Max(1, (int)MathF.Round(5f / Spacing));
        var rawCurv = new float[Count];
        var rawVert = new float[Count];
        for (int i = 0; i < Count; i++)
        {
            var p0 = Position[Wrap(i - k)];
            var p1 = Position[i];
            var p2 = Position[Wrap(i + k)];
            var d1 = p1 - p0;
            var d2 = p2 - p1;
            // heading change projected on the lateral axis
            var h1 = Vector3.Normalize(d1 - Normal[i] * Vector3.Dot(d1, Normal[i]));
            var h2 = Vector3.Normalize(d2 - Normal[i] * Vector3.Dot(d2, Normal[i]));
            float dist = 0.5f * (d1.Length() + d2.Length());
            rawCurv[i] = Vector3.Dot(h2 - h1, Lateral[i]) / MathF.Max(dist, 0.1f);

            // vertical: change of slope
            float slope1 = d1.Y / MathF.Max(0.1f, new Vector2(d1.X, d1.Z).Length());
            float slope2 = d2.Y / MathF.Max(0.1f, new Vector2(d2.X, d2.Z).Length());
            rawVert[i] = (slope2 - slope1) / MathF.Max(dist, 0.1f);
        }
        Smooth(rawCurv, Curvature, Math.Max(1, (int)MathF.Round(6f / Spacing)));
        Smooth(rawVert, VerticalCurvature, Math.Max(1, (int)MathF.Round(10f / Spacing)));

        // Work out which file side (SideLeft/SideRight) lies towards +Lateral:
        // around apexes the line hugs the inside, so the inside distance is the smaller one.
        double score = 0;
        for (int i = 0; i < Count; i++)
        {
            float c = Curvature[i];
            if (MathF.Abs(c) < 1f / 200f) continue;
            // if c > 0 the inside is +Lateral
            score += (sideA[i] - sideB[i]) * MathF.Sign(c) * MathF.Abs(c);
        }
        SideLeftIsPlus = score <= 0;
        for (int i = 0; i < Count; i++)
        {
            RoomPlus[i] = SideLeftIsPlus ? sideA[i] : sideB[i];
            RoomMinus[i] = SideLeftIsPlus ? sideB[i] : sideA[i];
            if (RoomPlus[i] <= 0.5f && RoomMinus[i] <= 0.5f)
            {
                // missing width data: assume a 10m road centered on the line
                RoomPlus[i] = RoomMinus[i] = 5f;
            }
        }
    }

    /// <summary>Applies Kunos ai_hints.ini ranges (normalized positions along this line).</summary>
    public void ApplyHints(IEnumerable<TrackRange> speedHints, IEnumerable<TrackRange> maxSpeedsKmh)
    {
        for (int i = 0; i < Count; i++)
        {
            float n = i / (float)Count;
            foreach (var h in speedHints)
                if (h.Contains(n)) HintFactor[i] = MathF.Min(HintFactor[i], h.Value);
            foreach (var m in maxSpeedsKmh)
                if (m.Contains(n)) MaxSpeed[i] = MathF.Min(MaxSpeed[i], m.Value / 3.6f);
        }
    }

    public int Wrap(int i) => ((i % Count) + Count) % Count;

    public float WrapS(float s)
    {
        s %= Length;
        return s < 0 ? s + Length : s;
    }

    /// <summary>Shortest signed distance along the line from a to b, in (-L/2, L/2].</summary>
    public float Delta(float fromS, float toS)
    {
        float d = WrapS(toS - fromS);
        return d > Length / 2 ? d - Length : d;
    }

    public int IndexAt(float s) => Wrap((int)MathF.Floor(WrapS(s) / Spacing));

    public void Interp(float s, out int i0, out int i1, out float t)
    {
        float x = WrapS(s) / Spacing;
        i0 = Wrap((int)MathF.Floor(x));
        i1 = Wrap(i0 + 1);
        t = x - MathF.Floor(x);
    }

    public Vector3 PositionAt(float s, float offset)
    {
        Interp(s, out var a, out var b, out var t);
        var p = Vector3.Lerp(Position[a], Position[b], t);
        var l = Vector3.Normalize(Vector3.Lerp(Lateral[a], Lateral[b], t));
        return p + l * offset;
    }

    public Vector3 ForwardAt(float s)
    {
        Interp(s, out var a, out var b, out var t);
        return Vector3.Normalize(Vector3.Lerp(Forward[a], Forward[b], t));
    }

    public Vector3 LateralAt(float s)
    {
        Interp(s, out var a, out var b, out var t);
        return Vector3.Normalize(Vector3.Lerp(Lateral[a], Lateral[b], t));
    }

    public float CurvatureAt(float s)
    {
        Interp(s, out var a, out var b, out var t);
        return Lerp(Curvature[a], Curvature[b], t);
    }

    public float CamberAt(float s)
    {
        Interp(s, out var a, out var b, out var t);
        return Lerp(Camber[a], Camber[b], t);
    }

    /// <summary>Room to each side at <paramref name="s"/>, interpolated between the points (no steps when a car drives along an edge).</summary>
    public float RoomPlusAt(float s)
    {
        Interp(s, out var a, out var b, out var t);
        return Lerp(RoomPlus[a], RoomPlus[b], t);
    }

    public float RoomMinusAt(float s)
    {
        Interp(s, out var a, out var b, out var t);
        return Lerp(RoomMinus[a], RoomMinus[b], t);
    }

    /// <summary>Smallest room on each side over [s, s+distance].</summary>
    public (float Minus, float Plus) MinRoom(float s, float distance)
    {
        float minus = float.MaxValue, plus = float.MaxValue;
        int start = IndexAt(s);
        int n = Math.Max(1, (int)(distance / Spacing));
        for (int k = 0; k <= n; k++)
        {
            int i = Wrap(start + k);
            minus = MathF.Min(minus, RoomMinus[i]);
            plus = MathF.Min(plus, RoomPlus[i]);
        }
        return (minus, plus);
    }

    /// <summary>
    /// Projects a world position onto the line. <paramref name="hintIndex"/> (from the previous call) makes this a cheap local search;
    /// pass -1 to do a full search.
    /// </summary>
    public (float S, float Offset, int Index, float Height) Project(Vector3 pos, int hintIndex = -1)
    {
        int best = -1;
        float bestD = float.MaxValue;

        if (hintIndex >= 0)
        {
            int window = (int)(150f / Spacing);
            for (int k = -window; k <= window; k++)
            {
                int i = Wrap(hintIndex + k);
                float d = Vector3.DistanceSquared(pos, Position[i]);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (bestD > 40 * 40) best = -1; // lost it (teleport to pits etc.)
        }

        if (best < 0)
        {
            bestD = float.MaxValue;
            int step = Math.Max(1, (int)(10f / Spacing));
            for (int i = 0; i < Count; i += step)
            {
                float d = Vector3.DistanceSquared(pos, Position[i]);
                if (d < bestD) { bestD = d; best = i; }
            }
            int coarse = best;
            for (int k = -step; k <= step; k++)
            {
                int i = Wrap(coarse + k);
                float d = Vector3.DistanceSquared(pos, Position[i]);
                if (d < bestD) { bestD = d; best = i; }
            }
        }

        var rel = pos - Position[best];
        float along = Vector3.Dot(rel, Forward[best]);
        float s = WrapS(best * Spacing + along);
        float off = Vector3.Dot(rel, Lateral[best]);
        float h = Vector3.Dot(rel, Normal[best]);
        return (s, off, best, h);
    }

    private void Smooth(float[] src, float[] dst, int half)
    {
        double sum = 0;
        int w = 2 * half + 1;
        for (int k = -half; k <= half; k++) sum += src[Wrap(k)];
        for (int i = 0; i < Count; i++)
        {
            dst[i] = (float)(sum / w);
            sum += src[Wrap(i + half + 1)] - src[Wrap(i - half)];
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
