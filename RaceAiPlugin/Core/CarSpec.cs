namespace RaceAiPlugin.Core;

/// <summary>
/// Simplified performance envelope of a car. Defaults are a generic modern GT3 car.
/// All accelerations in m/s², speeds in m/s.
/// </summary>
public sealed class CarSpec
{
    public const float G = 9.81f;

    public string Model { get; set; } = "generic_gt3";
    /// <summary>Where the numbers came from (for logging).</summary>
    public string Source { get; set; } = "defaults";

    public float TopSpeed { get; set; } = 280 / 3.6f;
    /// <summary>Acceleration at low speed; falls off quadratically towards top speed. Only used without <see cref="AccelTable"/>.</summary>
    public float Acceleration { get; set; } = 7.0f;
    /// <summary>Optional full-throttle acceleration per 1 m/s of speed (index = speed in m/s), computed from the real power curve and gearing.</summary>
    public float[]? AccelTable { get; set; }
    /// <summary>Mechanical grip coefficient (lateral, in g).</summary>
    public float LateralGrip { get; set; } = 1.55f;
    /// <summary>Mechanical braking grip (in g).</summary>
    public float BrakeGrip { get; set; } = 1.5f;
    /// <summary>Extra lateral accel from downforce per (m/s)²  (a_lat = grip*g + Downforce*v²).</summary>
    public float Downforce { get; set; } = 0.0017f;
    /// <summary>Extra braking decel from aero per (m/s)².</summary>
    public float AeroBrake { get; set; } = 0.0019f;
    /// <summary>Aero drag deceleration per (m/s)² (CdA·½ρ / mass). Used for the slipstream.</summary>
    public float DragCoefficient { get; set; } = 0.00039f;
    /// <summary>Crest limit: max speed = sqrt(g / |vertical curvature|) * this factor. GT3s fly over the Flugplatz, so > 1.</summary>
    public float CrestFactor { get; set; } = 1.35f;

    public float Length { get; set; } = 4.6f;
    public float Width { get; set; } = 2.0f;
    public float Wheelbase { get; set; } = 2.7f;
    public float TyreDiameter { get; set; } = 0.68f;
    public int IdleRpm { get; set; } = 2500;
    public int MaxRpm { get; set; } = 8500;
    /// <summary>Rpm at which the car shifts up (from ai.ini), 0 = shortly before <see cref="MaxRpm"/>.</summary>
    public int UpshiftRpm { get; set; }
    /// <summary>Top speed of each gear in km/h (1st..nth) at the rev limiter.</summary>
    public float[] GearTopSpeedsKmh { get; set; } = [95, 130, 165, 200, 235, 280];
    /// <summary>Steering ratio (steering wheel angle / wheel angle) and lock, only used for the visual steering wheel.</summary>
    public float SteerRatio { get; set; } = 13f;
    public float SteerLock { get; set; } = 320f;

    public float AccelAt(float v, float pace)
    {
        float paceFactor = 0.85f + 0.15f * Math.Clamp(pace, 0, 1);
        if (AccelTable is { Length: > 1 } table)
        {
            float x = Math.Clamp(v, 0, table.Length - 1.001f);
            int i = (int)x;
            float t = x - i;
            return MathF.Max(0, table[i] + (table[i + 1] - table[i]) * t) * paceFactor;
        }

        float r = Math.Clamp(v / TopSpeed, 0, 1);
        return Acceleration * paceFactor * (1 - r * r);
    }

    public float BrakeAt(float v, float pace) => (BrakeGrip * G + AeroBrake * v * v) * pace;

    /// <summary>Max speed through a point with the given curvatures.</summary>
    public float CornerLimit(float curvature, float verticalCurvature, float pace)
    {
        float grip = LateralGrip * pace;
        float aero = Downforce * pace;
        float r = 1f / MathF.Max(MathF.Abs(curvature), 1e-5f);
        // a_lat = grip*(g + v²*kv) + aero*v²  (kv<0 on crests reduces normal load)
        float denom = 1f - r * (aero + grip * verticalCurvature);
        float v = denom <= 0.02f ? TopSpeed : MathF.Sqrt(r * grip * G / denom);

        if (verticalCurvature < -1e-4f)
        {
            float crest = MathF.Sqrt(G / -verticalCurvature) * CrestFactor;
            v = MathF.Min(v, crest);
        }

        return MathF.Min(v, TopSpeed);
    }

    public CarSpec Clone()
    {
        var c = (CarSpec)MemberwiseClone();
        c.GearTopSpeedsKmh = (float[])GearTopSpeedsKmh.Clone();
        c.AccelTable = (float[]?)AccelTable?.Clone();
        return c;
    }
}
