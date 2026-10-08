using TrackGeometry;
namespace BotDriverPlugin.Core;

/// <summary>
/// Quick theoretical lap time of a car on a line: corner speed limits from the grip, then accelerating forwards and braking backwards
/// from them (milliseconds even for the Nordschleife). For comparing setups, not for the bots' pace.
/// </summary>
public static class LapEstimator
{
    public static float Estimate(CarSpec car, RacingLine line)
    {
        int n = line.Count;
        float ds = line.Spacing;
        var v = new float[n];
        for (int i = 0; i < n; i++) v[i] = MathF.Min(car.TopSpeed, car.CornerLimit(line.Curvature[i], line.VerticalCurvature[i], 1f));
        // two rounds so the lap wraps around
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                float vi = v[i];
                v[j] = MathF.Min(v[j], MathF.Sqrt(vi * vi + 2 * MathF.Max(0, car.AccelAt(vi, 1f)) * ds));
            }
        for (int pass = 0; pass < 2; pass++)
            for (int i = n - 1; i >= 0; i--)
            {
                int j = (i + 1) % n;
                v[i] = MathF.Min(v[i], MathF.Sqrt(v[j] * v[j] + 2 * car.BrakeAt(v[j], 1f) * ds));
            }
        float t = 0;
        for (int i = 0; i < n; i++) t += ds / MathF.Max(1f, v[i]);
        return t;
    }
}
