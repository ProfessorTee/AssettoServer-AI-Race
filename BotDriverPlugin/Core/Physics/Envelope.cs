namespace BotDriverPlugin.Core.Physics;

/// <summary>
/// What the vehicle model can do, measured by driving it: steady cornering grip at a slow and a fast speed (mechanical grip and downforce)
/// and the hardest braking at two speeds. The racecraft plans its corner speeds and braking points with these numbers, so the plan fits
/// the car the physics drives.
/// </summary>
public static class Envelope
{
    public readonly record struct Result(float Grip, float Downforce, float BrakeGrip, float AeroBrake, float LatSlow, float LatFast);

    private const float SlowSpeed = 22f, FastSpeed = 55f, Dt = 1f / 300;

    /// <param name="lateralGrip">Lateral grip multiplier (AC AI: ultra grip).</param>
    /// <param name="ai">With the AC AI's help: no grip falloff past the peak, perfect ABS.</param>
    /// <param name="plan">Cornering grip as the AC AI plans it (<see cref="PlanLateral"/>) instead of driving the skid pad.</param>
    public static Result Measure(VehicleData d, float lateralGrip, bool ai = false, bool plan = false)
    {
        float aSlow = plan ? PlanLateral(d, SlowSpeed) : MaxLateral(d, SlowSpeed, lateralGrip, ai);
        float aFast = plan ? PlanLateral(d, FastSpeed) : MaxLateral(d, FastSpeed, lateralGrip, ai);
        float down = MathF.Max(0, (aFast - aSlow) / (FastSpeed * FastSpeed - SlowSpeed * SlowSpeed));
        float grip = (aSlow - down * SlowSpeed * SlowSpeed) / CarSpec.G;
        var (b1, v1) = MaxBraking(d, 30f, lateralGrip, ai);
        var (b2, v2) = MaxBraking(d, 65f, lateralGrip, ai);
        float aeroBrake = MathF.Max(0, (b2 - b1) / MathF.Max(1, v2 * v2 - v1 * v1));
        float brakeGrip = (b1 - aeroBrake * v1 * v1) / CarSpec.G;
        return new Result(grip, down, brakeGrip, aeroBrake, aSlow, aFast);
    }

    /// <summary>
    /// The grip the AC AI plans its corners with (AIDriver via 0x14027a510 / 0x14027a260): the tyres' plain friction (D at their loads, no
    /// camber, no slip losses) with the load moved to the outside wheels (CG height over track, with 0.9 of that grip), downforce on top;
    /// both axles limited to what the other can hold at the car's weight split, and never more than the car rolling over (track / 2 / CG height).
    /// Lateral acceleration in m/s² at <paramref name="speed"/>.
    /// </summary>
    public static float PlanLateral(VehicleData d, float speed)
    {
        float m = d.Mass, wf = d.B / d.Wheelbase;
        float down = 0;
        foreach (var w in d.Wings) down += d.AirDensityHalf * speed * speed * w.Coefficients(0, d.CgHeight + w.Y).ClA;
        float aeroFactor = down / (m * G) + 1;
        float weightF = m * G * wf, weightR = m * G * (1 - wf);
        float mu0 = d.Front.StaticDy(weightF * 0.5f * aeroFactor) * 0.9f * aeroFactor;
        Span<float> fz = stackalloc float[4];
        for (int axle = 0; axle < 2; axle++)
        {
            float track = axle == 0 ? d.TrackFront : d.TrackRear;
            float t = d.CgHeight / (track) * mu0;
            float w = axle == 0 ? weightF : weightR;
            float share = axle == 0 ? wf : 1 - wf;
            fz[axle * 2] = MathF.Max(0, w * Math.Clamp(0.5f + t, 0, 1) + down * share * 0.5f);
            fz[axle * 2 + 1] = MathF.Max(0, w * Math.Clamp(0.5f - t, 0, 1) + down * share * 0.5f);
        }
        float front = d.Front.StaticDy(fz[0]) * fz[0] + d.Front.StaticDy(fz[1]) * fz[1];
        float rear = d.Rear.StaticDy(fz[2]) * fz[2] + d.Rear.StaticDy(fz[3]) * fz[3];
        float f = MathF.Min(front, rear * wf / (1 - wf)), r = MathF.Min(rear, front * (1 - wf) / wf);
        float lat = (f + r) / m;
        float rollover = MathF.Min(d.TrackFront, d.TrackRear) * 0.5f / d.CgHeight * G * aeroFactor;
        return MathF.Min(lat, rollover);
    }

    private const float G = CarSpec.G;

    /// <summary>Gear (overall ratio) a driver would use at this speed: the lowest one that keeps the revs under 90 % of the limiter.</summary>
    public static float RatioFor(VehicleData d, float v)
    {
        float wheel = v / (d.Drive == 1 ? d.Front.Radius : d.Rear.Radius) * 60 / (2 * MathF.PI);
        foreach (var g in d.Ratios)
            if (wheel * g * d.Final < d.Limiter * 0.9f) return g * d.Final;
        return d.Ratios[^1] * d.Final;
    }

    /// <summary>A car settled at a constant speed in a straight line (suspension, aero, engine).</summary>
    public static VehicleState Rolling(VehicleData d, float speed, ref VehicleInputs inp)
    {
        var st = new VehicleState { Vx = speed };
        for (int i = 0; i < 300; i++)
        {
            inp.Throttle = Math.Clamp(0.3f + (speed - st.Vx) * 0.6f, 0, 1);
            Vehicle.Step(d, st, inp, Dt);
        }
        return st;
    }

    /// <summary>
    /// Steady state cornering at a constant speed: the steering wound on slowly until the lateral acceleration doesn't grow any more or the car
    /// starts to slide; the best lateral acceleration (m/s²) held for half a second.
    /// </summary>
    private static float MaxLateral(VehicleData d, float speed, float lateralGrip, bool ai)
    {
        var inp = new VehicleInputs { MassRatio = 1, Grip = 1, LateralGrip = lateralGrip, NoFalloff = ai, AiAbs = ai, Ratio = RatioFor(d, speed) };
        var st = Rolling(d, speed, ref inp);
        float best = 0, window = 0, windowMin = float.MaxValue;
        for (int i = 0; i < 300 * 40; i++)
        {
            float t = i * Dt;
            inp.Steer = MathF.Min(d.MaxSteer, t * 0.4f * MathF.PI / 180 * (25 / speed) * 2);
            inp.Throttle = Math.Clamp(0.25f + (speed - st.Vx) * 0.6f, 0, 1);
            Vehicle.Step(d, st, inp, Dt);
            if (MathF.Abs(MathF.Atan2(st.Vy, MathF.Max(st.Vx, 1))) > 0.25f || st.Vx < speed * 0.85f) break;
            // steady: what the tyres push (force) and how fast the car turns agree; a car that rotates faster than it turns is sliding
            float ay = MathF.Min(MathF.Abs(st.Ay), MathF.Abs(st.Vx * st.R));
            windowMin = MathF.Min(windowMin, ay);
            window += Dt;
            if (window >= 0.5f)
            {
                best = MathF.Max(best, windowMin);
                window = 0;
                windowMin = float.MaxValue;
            }
        }
        return best;
    }

    /// <summary>Hardest braking in a straight line from <paramref name="speed"/> without locking a wheel (with ABS: what ABS gets): decel and the speed it was reached at.</summary>
    private static (float Decel, float Speed) MaxBraking(VehicleData d, float speed, float lateralGrip, bool ai)
    {
        float best = 0, at = speed;
        for (float pedal = 0.4f; pedal <= 1.001f; pedal += 0.05f)
        {
            var inp = new VehicleInputs { MassRatio = 1, Grip = 1, LateralGrip = lateralGrip, NoFalloff = ai, AiAbs = ai, Ratio = RatioFor(d, speed) };
            var st = Rolling(d, speed, ref inp);
            inp.Throttle = 0;
            inp.Brake = pedal;
            bool locked = false;
            float decel = 0, v = speed;
            for (int i = 0; i < 300 * 0.6f; i++)
            {
                Vehicle.Step(d, st, inp, Dt);
                if (st.Kappa.Any(k => k < -0.3f)) { locked = !d.Abs && !ai; break; }
                if (i > 300 * 0.3f && -st.Ax > decel) { decel = -st.Ax; v = st.Vx; }
            }
            if (locked) break;
            if (decel > best) { best = decel; at = v; }
        }
        return (best, at);
    }
}
