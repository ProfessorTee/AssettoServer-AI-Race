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
    /// <summary>Acceleration (m/s², by speed in m/s like <see cref="AccelTable"/>) that comes from the turbo boost: missing until the turbo has spooled up.</summary>
    public float[]? TurboAccel { get; set; }
    /// <summary>Turbo spool time constants in s (engine.ini LAG_UP / LAG_DN at AC's 333 Hz physics).</summary>
    public float SpoolUp { get; set; }
    /// <summary>Engine braking torque (Nm) at <see cref="CoastRpm"/> (engine.ini [COAST_REF]), 0 = unknown.</summary>
    public float CoastTorque { get; set; }
    public float CoastRpm { get; set; } = 7000;
    public float SpoolDown { get; set; }
    /// <summary>Hybrids: extra acceleration with full electric deploy, per 1 m/s (ers.ini). Used with the share the energy per lap allows on this track.</summary>
    public float[]? ErsGain { get; set; }
    /// <summary>Hybrids: electric power (W) with full deploy, per 1 m/s.</summary>
    public float[]? ErsPower { get; set; }
    /// <summary>Hybrids: ers.ini MAX_KJ_PER_LAP.</summary>
    public float ErsKjPerLap { get; set; }
    /// <summary>Share of full deploy the energy per lap pays for on the current track (0..1), set by <see cref="SetTrack"/>.</summary>
    public float ErsShare { get; private set; }
    private float _ersLapLength;
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
    /// <summary>Time an upshift takes in s (drivetrain.ini [GEARBOX] CHANGE_UP_TIME): no drive while the gear changes.</summary>
    public float ShiftUpTime { get; set; } = 0.15f;
    /// <summary>Time a downshift takes in s (CHANGE_DN_TIME).</summary>
    public float ShiftDownTime { get; set; } = 0.2f;
    /// <summary>Rpm below which the car shifts down (ai.ini [GEARS] DOWN), 0 = unknown.</summary>
    public int DownshiftRpm { get; set; }
    /// <summary>electronics.ini: the car has ABS (no locked wheels under braking) / traction control (less wheelspin).</summary>
    public bool HasAbs { get; set; }
    public bool HasTc { get; set; }
    /// <summary>Share of the brake torque on the front axle (brakes.ini FRONT_SHARE): a rear-biased car gets light at the back under braking.</summary>
    public float BrakeFront { get; set; } = 0.66f;
    /// <summary>
    /// How readily the car steps out on the power out of a corner, 1 = average: rear drive with a tight differential (drivetrain.ini
    /// [DIFFERENTIAL] POWER) and no traction control more, front drive / four wheel drive / traction control less.
    /// </summary>
    public float ExitSlide { get; set; } = 1f;
    /// <summary>Top speed of each gear in km/h (1st..nth) at the rev limiter.</summary>
    public float[] GearTopSpeedsKmh { get; set; } = [95, 130, 165, 200, 235, 280];
    /// <summary>Steering ratio (steering wheel angle / wheel angle) and lock, only used for the visual steering wheel.</summary>
    public float SteerRatio { get; set; } = 13f;
    public float SteerLock { get; set; } = 320f;

    // ---- endurance: fuel, tyres, pit stops (car.ini, fuel_cons.ini, tyres.ini)
    /// <summary>Mass the acceleration table / grip were computed for (kg, incl. driver and a light fuel load).</summary>
    public float ReferenceMass { get; set; } = 1350f;
    public float FuelCapacity { get; set; } = 120f;
    /// <summary>Default starting fuel from car.ini (litres).</summary>
    public float DefaultFuel { get; set; } = 30f;
    /// <summary>Distance per litre at racing speed (fuel_cons.ini KM_PER_LITER).</summary>
    public float KmPerLiter { get; set; } = 1.6f;
    /// <summary>Fuel per lap measured on the server's track by <see cref="StrengthCalibration"/> (litres at FUEL_RATE 100 %, 0 = unknown).</summary>
    public float CalibratedFuelPerLap { get; set; }
    /// <summary>Virtual tyre km per lap measured by the calibration (at wear rate 100 %).</summary>
    public float CalibratedTyreVkmPerLap { get; set; }
    /// <summary>Tyre grip over virtual km (0..1), default compound. Null = no wear data.</summary>
    public Lut? TyreWear { get; set; }
    /// <summary>Grip by tyre temperature of the default compound (tyres.ini [THERMAL_x] PERFORMANCE_CURVE, normalised to 1), null = a GT3 slick.</summary>
    public Lut? TyreTempCurve { get; set; }
    /// <summary>Middle of the compound's working window (°C).</summary>
    public float TyreOptimum { get; set; } = RaceWorld.TyreOptimum;
    public string TyreCompound { get; set; } = "";
    /// <summary>Time to change the tyres (car.ini TYRE_CHANGE_TIME_SEC) and to put one litre in (FUEL_LITER_TIME_SEC).</summary>
    public float TyreChangeTime { get; set; } = 20f;
    public float FuelLiterTime { get; set; } = 0.2f;
    /// <summary>car.ini [PIT_STOP]: seconds to repair 10 % body damage / 10 % suspension damage.</summary>
    public float BodyRepairTime { get; set; } = 20f;
    public float SuspRepairTime { get; set; } = 30f;

    /// <summary>Grip factor of the tyres after <paramref name="virtualKm"/> (1 = new).</summary>
    public float TyreGripAt(float virtualKm)
    {
        if (TyreWear is not { X.Length: > 0 } lut) return 1f;
        float max = lut.Max;
        return max <= 0 ? 1f : Math.Clamp(lut.At(virtualKm) / max, 0.5f, 1f);
    }

    /// <summary>Part of <see cref="AccelAt"/> that needs the turbo spooled up (0 for cars without turbo).</summary>
    public float TurboAccelAt(float v)
    {
        if (TurboAccel is not { Length: > 1 } t) return 0;
        float x = Math.Clamp(v, 0, t.Length - 1.001f);
        int i = (int)x;
        return t[i] + (t[i + 1] - t[i]) * (x - i);
    }

    public float AccelAt(float v, float pace)
    {
        // slower drivers also use less of the engine (early lift, short shifting, careful exits)
        float paceFactor = MathF.Pow(Math.Clamp(pace, 0.3f, 1.05f), 1.5f);
        if (AccelTable is { Length: > 1 } table)
        {
            float x = Math.Clamp(v, 0, table.Length - 1.001f);
            int i = (int)x;
            float t = x - i;
            float a = table[i] + (table[i + 1] - table[i]) * t;
            if (ErsShare > 0 && ErsGain is { Length: > 1 } eg)
            {
                float xe = Math.Clamp(v, 0, eg.Length - 1.001f);
                int j = (int)xe;
                a += ErsShare * (eg[j] + (eg[j + 1] - eg[j]) * (xe - j));
            }
            return MathF.Max(0, a) * paceFactor;
        }

        float r = Math.Clamp(v / TopSpeed, 0, 1);
        return Acceleration * paceFactor * (1 - r * r);
    }

    /// <summary>Deceleration (m/s²). A heavier car (fuel: <paramref name="massRatio"/> > 1) gets the same tyre grip per kg but less aero help per kg.</summary>
    public float BrakeAt(float v, float pace, float massRatio = 1f) => (BrakeGrip * G + AeroBrake * v * v / massRatio) * pace;

    /// <summary>Max speed through a point with the given curvatures.</summary>
    public float CornerLimit(float curvature, float verticalCurvature, float pace, float massRatio = 1f)
    {
        float grip = LateralGrip * pace;
        float aero = Downforce * pace / massRatio; // downforce per kg drops with a full tank
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

    /// <summary>
    /// Hybrids spread the energy per lap (MAX_KJ_PER_LAP) over the lap: on a short track it pays for nearly full deploy on every
    /// straight, on the Nordschleife only for a fraction. Estimate: deploy over ~45 % of the lap at ~80 % of the speed up to
    /// which the motors push.
    /// </summary>
    public void SetTrack(float lapLength)
    {
        if (ErsGain == null || ErsPower == null || ErsKjPerLap <= 0 || lapLength <= 0 || MathF.Abs(lapLength - _ersLapLength) < 1) return;
        _ersLapLength = lapLength;
        int last = Array.FindLastIndex(ErsPower, p => p > 1000);
        if (last < 10) { ErsShare = 0; return; }
        float vMax = MathF.Min(last, TopSpeed);
        float meanPower = 0;
        int n = 0;
        for (int v = 15; v <= last; v++) { meanPower += ErsPower[v]; n++; }
        meanPower /= MathF.Max(1, n);
        float deploySeconds = 0.45f * lapLength / MathF.Max(15, 0.8f * vMax);
        float needKj = meanPower * deploySeconds / 1000;
        ErsShare = needKj <= 0 ? 0 : Math.Clamp(ErsKjPerLap / needKj, 0, 1);
    }

    public CarSpec Clone()
    {
        var c = (CarSpec)MemberwiseClone();
        c.GearTopSpeedsKmh = (float[])GearTopSpeedsKmh.Clone();
        c.AccelTable = (float[]?)AccelTable?.Clone();
        c.TurboAccel = (float[]?)TurboAccel?.Clone();
        c.ErsGain = (float[]?)ErsGain?.Clone();
        c.ErsPower = (float[]?)ErsPower?.Clone();
        c.TyreWear = TyreWear;
        c.TyreTempCurve = TyreTempCurve;
        return c;
    }
}
