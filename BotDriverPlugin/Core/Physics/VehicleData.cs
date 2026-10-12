using System.Globalization;
using System.Text;
using TrackGeometry;

namespace BotDriverPlugin.Core.Physics;

/// <summary>A differential's locking (drivetrain.ini): torque it can hold against the wheels turning at different speeds.</summary>
public readonly record struct DiffData(float Power, float Coast, float Preload)
{
    public float Lock(float torqueIn) => Preload + (torqueIn >= 0 ? Power : Coast) * MathF.Abs(torqueIn);
}

/// <summary>
/// One wing of aero.ini (AC: Wing::step 0x1402b2bc0): lift and drag over the angle of attack (wing ANGLE + the body's pitch) and over the
/// height of the wing above the road (LUT_GH_CL/CD), at its position relative to the CG (x sideways, y up, z forward).
/// </summary>
public sealed class WingData
{
    public float Area, Angle, ClGain = 1, CdGain = 1, X, Y, Z;
    public Lut? AoaCl, AoaCd, GhCl, GhCd;

    /// <summary>Lift area (CL·A, + = downforce) and drag area (CD·A).</summary>
    public (float ClA, float CdA) Coefficients(float aoaDeg, float height)
    {
        float a = Angle + aoaDeg;
        float cl = (AoaCl is { X.Length: > 0 } ? AoaCl.At(a) : 0) * ClGain, cd = (AoaCd is { X.Length: > 0 } ? AoaCd.At(a) : 0) * CdGain;
        if (GhCl is { X.Length: > 0 }) cl *= GhCl.At(height);
        if (GhCd is { X.Length: > 0 }) cd *= GhCd.At(height);
        return (cl * Area, cd * Area);
    }
}

/// <summary>A turbo of engine.ini (AC: Turbo::step 0x1402ae7c0): spins towards (rpm·gas / REFERENCE_RPM)^GAMMA with LAG_UP / LAG_DN per step.</summary>
public sealed class TurboData
{
    public float MaxBoost, Wastegate, ReferenceRpm = 1, Gamma = 1, LagUp = 0.99f, LagDn = 0.99f;
    /// <summary>Wastegate by rpm (ctrl_turbo n .ini), null = the fixed WASTEGATE.</summary>
    public Func<float, float>? Control;
}

/// <summary>Everything the vehicle model needs about a car, from its data files.</summary>
public sealed class VehicleData
{
    public float Mass;
    /// <summary>Inertia (kg·m²) about the yaw, roll and pitch axes from car.ini INERTIA (box width, height, length).</summary>
    public float YawInertia, RollInertia, PitchInertia;
    public float Wheelbase;
    /// <summary>Distances CG → front / rear axle (CG_LOCATION is the front axle's share of the weight).</summary>
    public float A, B;
    /// <summary>Static state on a flat road at rest (<see cref="Vehicle.Settle"/>): CG height, pitch, hub heights.</summary>
    public float CgHeight = 0.45f, StaticPitch;
    public readonly float[] StaticHub = new float[4];
    public float TrackFront => FrontSusp.Track;
    public float TrackRear => RearSusp.Track;
    public TyreData Front = new(), Rear = new();
    public SuspensionAxle FrontSusp = new(), RearSusp = new();
    public List<WingData> Wings = [];
    public float AirDensityHalf = 0.5f * 1.225f;
    /// <summary>Scale on the wings' drag (<see cref="CarDataLoader"/>: recorded laps pull harder than aero.ini's drag).</summary>
    public float DragScale = 1;
    /// <summary>Engine torque (Nm) every 100 rpm without boost (restrictor included).</summary>
    public float[] Torque = [];
    public List<TurboData> Turbos = [];
    public float Limiter, LimiterHz = 16, IdleRpm;
    public float CoastTorque, CoastRpm;
    /// <summary>Rotating inertia of engine (engine.ini INERTIA) and gearbox (drivetrain.ini GEARBOX INERTIA), and the clutch's torque.</summary>
    public float EngineInertia = 0.15f, ClutchTorque = 600;
    /// <summary>
    /// drivetrain.ini AUTOCLUTCH profiles used on gear changes (seconds from the start of the change: clutch fully in, starts coming out,
    /// fully out), null = the clutch closes at once; AUTOBLIP on downshifts (gas LEVEL reached, held until, back to 0).
    /// </summary>
    public float[]? UpClutch, DownClutch, Blip;
    public float BlipLevel;
    public float[] Ratios = [];
    public float Final;
    public const float Efficiency = 0.85f;
    /// <summary>0 = rear drive, 1 = front drive, 2 = four wheel drive with a centre differential (AWD), 3 = rear drive with a coupling to the front (AWD2).</summary>
    public int Drive;
    public DiffData FrontDiff, RearDiff, CentreDiff;
    public float AwdFrontShare = 0.4f, CouplingRamp = 100, CouplingMax = 1000;
    public float BrakeTorque, BrakeFront;
    /// <summary>Wheel angle at full lock, steering ratio (car.ini, signed) and steering rack travel (m) per degree of commanded wheel angle.</summary>
    public float MaxSteer, SteerRatio = 14, RackPerDeg = 0.002f;
    /// <summary>Rack direction that turns the wheels towards +y (found from the geometry).</summary>
    public float RackSign = 1;
    public bool Abs, Tc;
    /// <summary>electronics.ini: slip ratio limits, how often they act (RATE_HZ), TC's minimum speed.</summary>
    public float AbsSlip = 0.12f, AbsRateHz = 100, TcSlip = 0.1f, TcRateHz = 100, TcMinSpeed;
    /// <summary>ai.ini of the car: ULTRA_GRIP (AI's extra lateral grip, AC default 1.2), STEER_GAIN, LOOKAHEAD BASE, BRAKE_HINT, UNDERSTEER hint.</summary>
    public float AiUltraGrip = 1.2f, AiSteerGain = 0.05f, AiLookahead = 12, AiBrakeHint = 1, AiUndersteer = 1;
    /// <summary>ai.ini GEARS: upshift rpm (AC default 6000) and downshift point as a share of it (DOWN/100 from ai.ini version 4, else 0.8).</summary>
    public float AiUpRpm = 6000, AiDownFactor = 0.8f;

    public float SprungMass => Mass - 2 * FrontSusp.HubMass - 2 * RearSusp.HubMass;

    public float TorqueAt(float rpm)
    {
        if (Torque.Length == 0) return 0;
        float x = Math.Clamp(rpm / 100f, 0, Torque.Length - 1.001f);
        int i = (int)x;
        return Torque[i] + (Torque[i + 1] - Torque[i]) * (x - i);
    }

    /// <summary>Spool level a turbo heads for: (rpm·gas / REFERENCE_RPM)^GAMMA, 0..1.</summary>
    public static float TurboTarget(TurboData t, float rpm, float gas)
    {
        float x = rpm * gas / MathF.Max(1, t.ReferenceRpm);
        return x <= 0 ? 0 : MathF.Pow(Math.Clamp(x, 0, 1), t.Gamma);
    }

    /// <summary>Boost of a turbo at a spool level, capped by the wastegate (or its controller at this rpm).</summary>
    public static float TurboBoost(TurboData t, float level, float rpm)
    {
        float boost = level * t.MaxBoost;
        float gate = t.Control?.Invoke(rpm) ?? t.Wastegate;
        return gate > 0 ? MathF.Min(boost, gate) : boost;
    }

    /// <summary>Torque at full throttle with the turbos spooled up (planning, envelopes).</summary>
    public float FullTorqueAt(float rpm)
    {
        float b = 0;
        foreach (var t in Turbos) b += TurboBoost(t, TurboTarget(t, rpm, 1), rpm);
        return TorqueAt(rpm) * (1 + b);
    }

    private static IniFile? IniOf(Dictionary<string, byte[]> files, string name)
        => files.TryGetValue(name, out var b) ? IniFile.Parse(Encoding.UTF8.GetString(b)) : null;

    private static Lut? LutOf(Dictionary<string, byte[]> files, string? name)
        => !string.IsNullOrWhiteSpace(name) && files.TryGetValue(name.Split(';')[0].Trim(), out var b) ? Lut.Parse(Encoding.UTF8.GetString(b)) : null;

    internal static VehicleData Read(Dictionary<string, byte[]> files, IniFile car, IniFile tyres, string tyreF, string tyreR, CarSpec spec,
        float mass, float wheelbase, float cgFront, string traction, List<float> ratios, float final, int limiter, Func<float, float> torque,
        List<TurboData> turbos, Func<string, float>? wingAngle, float dragScale, float airDensityHalf)
    {
        var susp = IniOf(files, "suspensions.ini");
        var d = new VehicleData
        {
            Mass = mass,
            Wheelbase = wheelbase,
            A = wheelbase * (1 - cgFront),
            B = wheelbase * cgFront,
            Ratios = ratios.ToArray(),
            Final = final,
            Limiter = limiter,
            IdleRpm = spec.IdleRpm,
            CoastTorque = spec.CoastTorque,
            CoastRpm = spec.CoastRpm,
            Turbos = turbos,
            DragScale = dragScale,
            AirDensityHalf = airDensityHalf,
            Abs = spec.HasAbs,
            Tc = spec.HasTc,
            BrakeFront = spec.BrakeFront,
            Front = TyreData.From(tyres, tyreF, spec.LateralGrip),
            Rear = TyreData.From(tyres, tyreR, spec.LateralGrip),
        };
        // car.ini INERTIA is the box the inertia is computed from (width, height, length)
        var box = (car.Get("BASIC", "INERTIA") ?? "").Split(';')[0].Split(',').Select(x => float.TryParse(x.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var f) ? f : 0).ToArray();
        float w = box.Length > 0 && box[0] > 0 ? box[0] : spec.Width, h = box.Length > 1 && box[1] > 0 ? box[1] : 1.2f, l = box.Length > 2 && box[2] > 0 ? box[2] : spec.Length;
        d.YawInertia = mass / 12f * (w * w + l * l);
        d.RollInertia = mass / 12f * (w * w + h * h);
        d.PitchInertia = mass / 12f * (h * h + l * l);

        // steering: wheel angle at full lock, and the rack (AC: SteeringSystem moves the rod by LINEAR_STEER_ROD_RATIO per degree)
        d.SteerRatio = spec.SteerRatio;
        d.MaxSteer = Math.Clamp(spec.SteerLock / MathF.Max(1, MathF.Abs(spec.SteerRatio)), 10, 50) * MathF.PI / 180;
        d.RackPerDeg = MathF.Abs(car.GetFloat("CONTROLS", "LINEAR_STEER_ROD_RATIO", 0.003f));
        if (susp != null)
        {
            d.FrontSusp = SuspensionAxle.Read(susp, "FRONT", susp.GetFloat("ARB", "FRONT", 0), d.Front.Radius, true, d.RackPerDeg * 180 / MathF.PI);
            d.RearSusp = SuspensionAxle.Read(susp, "REAR", susp.GetFloat("ARB", "REAR", 0), d.Rear.Radius, false, 0);
        }

        if (IniOf(files, "brakes.ini") is { } brakes) d.BrakeTorque = brakes.GetFloat("DATA", "MAX_TORQUE", 0);
        if (d.BrakeTorque <= 0) d.BrakeTorque = 0.7f * mass * CarSpec.G * d.Rear.Radius;

        d.Torque = Enumerable.Range(0, Math.Max(2, limiter / 100 + 2)).Select(i => MathF.Max(0, torque(i * 100f))).ToArray();
        var engine = IniOf(files, "engine.ini");
        var drivetrain = IniOf(files, "drivetrain.ini");
        d.EngineInertia = (engine?.GetFloat("ENGINE_DATA", "INERTIA", 0.12f) ?? 0.12f) + (drivetrain?.GetFloat("GEARBOX", "INERTIA", 0.02f) ?? 0.02f);
        d.LimiterHz = MathF.Max(1, engine?.GetFloat("ENGINE_DATA", "LIMITER_HZ", 16) ?? 16);
        d.ClutchTorque = drivetrain?.GetFloat("CLUTCH", "MAX_TORQUE", 600) ?? 600;
        if (drivetrain != null)
        {
            float[]? Profile(string key)
            {
                string? name = drivetrain.Get("AUTOCLUTCH", key)?.Split(';')[0].Trim();
                if (string.IsNullOrEmpty(name) || name.Equals("NONE", StringComparison.OrdinalIgnoreCase) || !drivetrain.HasSection(name)) return null;
                return [drivetrain.GetFloat(name, "POINT_0", 0) / 1000, drivetrain.GetFloat(name, "POINT_1", 0) / 1000, drivetrain.GetFloat(name, "POINT_2", 0) / 1000];
            }
            d.UpClutch = Profile("UPSHIFT_PROFILE");
            d.DownClutch = Profile("DOWNSHIFT_PROFILE");
            if (drivetrain.HasSection("AUTOBLIP"))
            {
                d.Blip = [drivetrain.GetFloat("AUTOBLIP", "POINT_0", 0) / 1000, drivetrain.GetFloat("AUTOBLIP", "POINT_1", 0) / 1000, drivetrain.GetFloat("AUTOBLIP", "POINT_2", 0) / 1000];
                d.BlipLevel = drivetrain.GetFloat("AUTOBLIP", "LEVEL", 0);
            }
        }
        string tr = traction.Trim().ToUpperInvariant();
        d.Drive = tr switch { "FWD" => 1, "AWD" => 2, "AWD2" => 3, _ => 0 };
        if (drivetrain != null)
        {
            var main = new DiffData(drivetrain.GetFloat("DIFFERENTIAL", "POWER", 0), drivetrain.GetFloat("DIFFERENTIAL", "COAST", 0), drivetrain.GetFloat("DIFFERENTIAL", "PRELOAD", 0));
            d.FrontDiff = d.RearDiff = main;
            if (d.Drive == 2)
            {
                d.AwdFrontShare = Math.Clamp(drivetrain.GetFloat("AWD", "FRONT_SHARE", 0.4f), 0.05f, 0.95f);
                d.FrontDiff = new(drivetrain.GetFloat("AWD", "FRONT_DIFF_POWER", 0), drivetrain.GetFloat("AWD", "FRONT_DIFF_COAST", 0), drivetrain.GetFloat("AWD", "FRONT_DIFF_PRELOAD", 0));
                d.CentreDiff = new(drivetrain.GetFloat("AWD", "CENTRE_DIFF_POWER", 0), drivetrain.GetFloat("AWD", "CENTRE_DIFF_COAST", 0), drivetrain.GetFloat("AWD", "CENTRE_DIFF_PRELOAD", 0));
                d.RearDiff = new(drivetrain.GetFloat("AWD", "REAR_DIFF_POWER", main.Power), drivetrain.GetFloat("AWD", "REAR_DIFF_COAST", main.Coast), drivetrain.GetFloat("AWD", "REAR_DIFF_PRELOAD", main.Preload));
            }
            else if (d.Drive == 3)
            {
                d.FrontDiff = new(drivetrain.GetFloat("AWD2", "FRONT_DIFF_POWER", 0), drivetrain.GetFloat("AWD2", "FRONT_DIFF_COAST", 0), drivetrain.GetFloat("AWD2", "FRONT_DIFF_PRELOAD", 0));
                d.RearDiff = new(drivetrain.GetFloat("AWD2", "REAR_DIFF_POWER", main.Power), drivetrain.GetFloat("AWD2", "REAR_DIFF_COAST", main.Coast), drivetrain.GetFloat("AWD2", "REAR_DIFF_PRELOAD", main.Preload));
                d.CouplingRamp = drivetrain.GetFloat("AWD2", "CENTRE_RAMP_TORQUE", 100);
                d.CouplingMax = drivetrain.GetFloat("AWD2", "CENTRE_MAX_TORQUE", 1000);
            }
        }
        if (IniOf(files, "electronics.ini") is { } el)
        {
            d.AbsSlip = Math.Clamp(el.GetFloat("ABS", "SLIP_RATIO_LIMIT", 0.12f), 0.03f, 0.4f);
            d.AbsRateHz = Math.Clamp(el.GetFloat("ABS", "RATE_HZ", 100), 5, 1000);
            d.TcSlip = Math.Clamp(el.GetFloat("TRACTION_CONTROL", "SLIP_RATIO_LIMIT", 0.1f), 0.03f, 0.4f);
            d.TcRateHz = Math.Clamp(el.GetFloat("TRACTION_CONTROL", "RATE_HZ", 100), 5, 1000);
            d.TcMinSpeed = el.GetFloat("TRACTION_CONTROL", "MIN_SPEED_KMH", 0) / 3.6f;
        }
        if (IniOf(files, "ai.ini") is { } ai)
        {
            d.AiUltraGrip = Math.Clamp(ai.GetFloat("ULTRA_GRIP", "VALUE", 1.2f), 1f, 2f);
            d.AiSteerGain = ai.GetFloat("STEER", "STEER_GAIN", 0.05f);
            d.AiLookahead = ai.GetFloat("LOOKAHEAD", "BASE", 12);
            d.AiBrakeHint = Pos(ai.GetFloat("PEDALS", "BRAKE_HINT", 1), 1);
            d.AiUndersteer = Pos(ai.GetFloat("UNDERSTEER", "HINT", 1), 1);
            d.AiUpRpm = Pos(ai.GetFloat("GEARS", "UP", 6000), 6000);
            if (ai.GetInt("HEADER", "VERSION", 0) >= 4 && ai.GetFloat("GEARS", "DOWN", 0) > 0) d.AiDownFactor = ai.GetFloat("GEARS", "DOWN", 80) * 0.01f;
        }
        if (d.AiLookahead <= 0) d.AiLookahead = 12;

        if (IniOf(files, "aero.ini") is { } aero)
        {
            foreach (var sec in aero.Sections.Where(s => s.StartsWith("WING_", StringComparison.OrdinalIgnoreCase)))
            {
                var pos = (aero.Get(sec, "POSITION") ?? "0,0,0").Split(';')[0].Split(',').Select(x => float.TryParse(x.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var f) ? f : 0).ToArray();
                d.Wings.Add(new WingData
                {
                    Area = aero.GetFloat(sec, "CHORD", 0) * aero.GetFloat(sec, "SPAN", 0),
                    Angle = wingAngle?.Invoke(sec) ?? aero.GetFloat(sec, "ANGLE", 0),
                    ClGain = aero.GetFloat(sec, "CL_GAIN", 1),
                    CdGain = aero.GetFloat(sec, "CD_GAIN", 1),
                    AoaCl = LutOf(files, aero.Get(sec, "LUT_AOA_CL")),
                    AoaCd = LutOf(files, aero.Get(sec, "LUT_AOA_CD")),
                    GhCl = LutOf(files, aero.Get(sec, "LUT_GH_CL")),
                    GhCd = LutOf(files, aero.Get(sec, "LUT_GH_CD")),
                    X = pos.Length > 0 ? pos[0] : 0, Y = pos.Length > 1 ? pos[1] : 0, Z = pos.Length > 2 ? pos[2] : 0,
                });
            }
        }
        Vehicle.Settle(d);
        return d;
    }

    private static float Pos(float v, float d) => v > 0 ? v : d;
}
