namespace BotDriverPlugin.Core.Physics;

/// <summary>
/// State of a car in the vehicle model, in the racing line's coordinates: distance along the line, offset from it, heading relative to it,
/// velocity in the car's frame (x forward, y towards +offset) and yaw rate (positive turns towards +offset, like the line's curvature);
/// the sprung body's height, pitch and roll on its suspension, the four hubs' heights, the wheels' speeds and the tyres' relaxed slips
/// (wheel 0 front +offset side, 1 front −offset side, 2 rear +offset side, 3 rear −offset side), engine, clutch, turbos and electronics.
/// </summary>
public sealed class VehicleState
{
    public double S;
    public float N, Psi, Vx, Vy, R;
    /// <summary>Commanded wheel angle (rad, positive towards +offset): the steering wheel angle divided by the steering ratio.</summary>
    public float Steer;
    /// <summary>Real angle of each wheel (steering through the rack and linkage: Ackermann, toe, bump steer).</summary>
    public readonly float[] WheelAngle = new float[4];
    /// <summary>Acceleration in the car's frame (m/s²) of the last step.</summary>
    public float Ax, Ay;
    /// <summary>Height of the CG above the road and its rate; pitch (rad, + = nose down) and roll (rad, + = the +offset side down) with rates.</summary>
    public float H, HDot, Pitch, PitchRate, Roll, RollRate;
    public readonly float[] HubH = new float[4], HubV = new float[4], Travel = new float[4];
    public readonly float[] Omega = new float[4];
    /// <summary>Relaxed slip ratio and slip angle (rad) of each tyre (AC: Tyre::stepRelaxationLength).</summary>
    public readonly float[] Kappa = new float[4], Alpha = new float[4];
    public readonly float[] Load = new float[4];
    /// <summary>Combined slip relative to the peak per tyre (1 = at the peak; AC's tyre+0x314 that the AI reads).</summary>
    public readonly float[] NdSlip = new float[4];
    /// <summary>Height of the road under each wheel relative to the line's road plane (bumps, kerbs) and its grip (surfaces.ini FRICTION); set by the caller.</summary>
    public readonly float[] Road = new float[4];
    public readonly float[] RoadGrip = [1, 1, 1, 1];
    private readonly float[] _roadPrev = new float[4];
    /// <summary>Speed along the line (ds/dt) and sideways (dn/dt) of the last step.</summary>
    public float SDot, NDot;
    /// <summary>Slip angle relative to the angle of peak grip (1 = at the peak), the larger of an axle's two wheels.</summary>
    public float FrontSlip, RearSlip;
    /// <summary>Combined slip (1 = at the peak), the larger of an axle's two wheels.</summary>
    public float FrontUse, RearUse;
    public bool FrontLocked, DrivenSpinning;
    /// <summary>
    /// Brake pedal / throttle at which the first axle runs out of grip with the cornering it does now (friction circle): what a driver
    /// feels through the seat and modulates to.
    /// </summary>
    public float BrakeLimit = 1, ThrottleLimit = 1;
    /// <summary>Brake pedal at which the first axle locks in a straight line (AC AI's maximum brake).</summary>
    public float BrakeLockLimit = 1;
    /// <summary>Full throttle drive force over what the driven tyres can put down (AC AI's throttle gain).</summary>
    public float DriveRatio = 1;
    public float EngineRpm, EngineOmega;
    public bool ClutchLocked;
    internal float LastRatio;
    public float[] TurboLevel = [];
    internal float LimiterCut, AbsTimer, TcTimer;
    internal readonly bool[] AbsRelease = new bool[4];
    internal bool TcCut;
    /// <summary>Offset and speed handed to the race world last time: a change means something outside the model moved the car (a contact).</summary>
    internal float WrittenOffset = float.NaN, WrittenSpeed = float.NaN, WrittenLateral = float.NaN;
    internal bool WheelsSet;
    /// <summary>The tyres' temperatures and pressures (null = warm at the ideal pressure, e.g. while measuring the car).</summary>
    public TyreHeat[]? Heat;

    internal float RoadRate(int i, float dt)
    {
        float r = (Road[i] - _roadPrev[i]) / dt;
        _roadPrev[i] = Road[i];
        return Math.Clamp(r, -5, 5);
    }

    internal void ResetRoad() => Array.Copy(Road, _roadPrev, 4);
}

/// <summary>Driver inputs, the road under the car and the AI's help for one step.</summary>
public struct VehicleInputs
{
    public float Steer, Throttle, Brake;
    /// <summary>Overall gear ratio (gearbox × final), 0 = no drive (gear change in progress).</summary>
    public float Ratio;
    public float MassRatio, Grip;
    /// <summary>Lateral grip multiplier on the tyre forces (AC: the AI's ultra grip, tyre+0x538; 1 = as the data says, 0 = 1).</summary>
    public float LateralGrip;
    /// <summary>No grip loss past the peak (AC sets it whenever the ultra grip is above 1).</summary>
    public bool NoFalloff;
    /// <summary>AC AI's perfect ABS (absOverride): a wheel past its peak slip gets no brake.</summary>
    public bool AiAbs;
    /// <summary>Line curvature (1/m, positive towards +offset) and vertical curvature (positive = compression).</summary>
    public float Curvature, VerticalCurvature;
    /// <summary>Gravity along the car (downhill positive) and sideways towards +offset (m/s²).</summary>
    public float GravityX, GravityY;
    /// <summary>Extra forces on the body in the car's frame (N): the AC AI's spring to its line and brake help.</summary>
    public float ExtraFx, ExtraFy;
    /// <summary>Extra yaw moment (N·m, AI help).</summary>
    public float ExtraMz;
    /// <summary>Time since the last gear change started (s, NaN = none for a while) and whether it was a downshift (autoclutch, autoblip).</summary>
    public float ShiftTime;
    public bool Downshift;
    /// <summary>Air and road temperature (°C) for the tyres.</summary>
    public float AmbientTemp, RoadTemp;
}

/// <summary>
/// The vehicle model, as close to AC's as a car on its line can be:
/// <list type="bullet">
/// <item>sprung body with heave, pitch and roll on four corners (springs, bump stops, packers, dampers, anti-roll bars) and four hubs on their
/// tyres' vertical spring; the tyre forces push on the body through the linkage (roll centres, anti-dive, anti-squat) — <see cref="SuspensionAxle"/>;</item>
/// <item>each wheel's camber and toe from the solved linkage (static camber, camber gain, bump steer), steering through the rack (Ackermann);</item>
/// <item>tyres: AC's model 10 (<see cref="TyreData"/>) with relaxation length, rolling resistance, radius growth, surface grip;</item>
/// <item>engine with turbos (spool lag), limiter cut (LIMITER_HZ), coast torque, engine inertia, clutch (slips after gear changes and at the
/// start), limited slip differentials (POWER / COAST / PRELOAD), AWD centre differential or AWD2 coupling;</item>
/// <item>brakes per wheel, ABS and traction control as electronics.ini (slip ratio limit, RATE_HZ);</item>
/// <item>wings with lift and drag over angle of attack and ride height (aero.ini LUTs).</item>
/// </list>
/// </summary>
public static class Vehicle
{
    public const float G = CarSpec.G;

    /// <summary>Static equilibrium on a flat road at rest (spring travel, tyre deflection, CG height, pitch) and the rack's direction.</summary>
    public static void Settle(VehicleData d)
    {
        float ms = d.SprungMass, l = d.Wheelbase;
        float zF = 0, zR = 0;
        for (int axle = 0; axle < 2; axle++)
        {
            var s = axle == 0 ? d.FrontSusp : d.RearSusp;
            var t = axle == 0 ? d.Front : d.Rear;
            float w = ms * G * (axle == 0 ? d.B : d.A) / l / 2;
            float lo = -0.5f, hi = 0.5f;
            for (int k = 0; k < 60; k++)
            {
                float mid = (lo + hi) / 2;
                if (s.SpringForce(mid) < w) lo = mid; else hi = mid;
            }
            float z = (lo + hi) / 2;
            float hub = t.Radius - (w + s.HubMass * G) / t.Rate;
            d.StaticHub[axle * 2] = d.StaticHub[axle * 2 + 1] = hub;
            if (axle == 0) zF = z; else zR = z;
        }
        // body point above each axle: hub − travel = H + BASEY − x·pitch
        float pF = d.StaticHub[0] - zF - d.FrontSusp.BaseY, pR = d.StaticHub[2] - zR - d.RearSusp.BaseY;
        d.StaticPitch = (pR - pF) / (d.A + d.B);
        d.CgHeight = pF + d.A * d.StaticPitch;

        var fs = d.FrontSusp;
        if (fs.HasGeometry && fs.RackCount > 1)
        {
            float r = 0.01f;
            float mean = (-fs.Angles(zF, -r).ToeIn + fs.Angles(zF, r).ToeIn) / 2;
            d.RackSign = mean >= 0 ? 1 : -1;
        }
    }

    private static void Init(VehicleData d, VehicleState st)
    {
        if (d.StaticHub[0] == 0) Settle(d);
        for (int i = 0; i < 4; i++)
        {
            st.Omega[i] = st.Vx / (i < 2 ? d.Front.Radius : d.Rear.Radius);
            st.HubH[i] = d.StaticHub[i] + st.Road[i];
            st.HubV[i] = 0;
        }
        st.H = d.CgHeight;
        st.Pitch = d.StaticPitch;
        st.HDot = st.PitchRate = st.RollRate = st.Roll = 0;
        if (st.TurboLevel.Length != d.Turbos.Count) st.TurboLevel = new float[d.Turbos.Count];
        st.ResetRoad();
        st.EngineOmega = MathF.Max(d.IdleRpm * 2 * MathF.PI / 60, 0);
        st.ClutchLocked = false;
        st.WheelsSet = true;
    }

    public static void Step(VehicleData d, VehicleState st, in VehicleInputs inp, float dt)
    {
        if (!st.WheelsSet) Init(d, st);
        float massRatio = inp.MassRatio > 0 ? inp.MassRatio : 1;
        float m = d.Mass * massRatio;
        float ms = m - 2 * d.FrontSusp.HubMass - 2 * d.RearSusp.HubMass;
        float vx = st.Vx, vy = st.Vy, r = st.R;
        float lateralGrip = inp.LateralGrip > 0 ? inp.LateralGrip : 1;

        st.Steer = Math.Clamp(inp.Steer, -d.MaxSteer, d.MaxSteer);
        float gEff = MathF.Max(0, G + vx * vx * inp.VerticalCurvature);

        // ---- suspension travel, wheel angles and camber from the linkage
        Span<float> xs = stackalloc float[4], ys = stackalloc float[4], lean = stackalloc float[4], spring = stackalloc float[4];
        float rack = d.RackSign * d.RackPerDeg * st.Steer * 180 / MathF.PI;
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2, plus = i % 2 == 0;
            var sa = front ? d.FrontSusp : d.RearSusp;
            xs[i] = front ? d.A : -d.B;
            ys[i] = (plus ? 0.5f : -0.5f) * sa.Track;
            float bodyPt = st.H + sa.BaseY - xs[i] * st.Pitch - ys[i] * st.Roll;
            float z = st.HubH[i] - bodyPt;
            float zDot = st.HubV[i] - (st.HDot - xs[i] * st.PitchRate - ys[i] * st.RollRate);
            st.Travel[i] = z;
            spring[i] = sa.SpringForce(z) + sa.DamperForce(zDot);
            float rackIn = front ? (plus ? -rack : rack) : 0;
            var (topIn, toeIn) = sa.Angles(z, rackIn);
            float delta = plus ? -toeIn : toeIn;
            if (front && !(sa.HasGeometry && sa.RackCount > 1)) delta += st.Steer;
            st.WheelAngle[i] = delta;
            lean[i] = plus ? st.Roll - topIn : st.Roll + topIn;
        }
        for (int axle = 0; axle < 2; axle++)
        {
            var sa = axle == 0 ? d.FrontSusp : d.RearSusp;
            float f = sa.Arb * (st.Travel[axle * 2] - st.Travel[axle * 2 + 1]);
            spring[axle * 2] += f;
            spring[axle * 2 + 1] -= f;
        }
        // tyre loads from their vertical spring and damper
        for (int i = 0; i < 4; i++)
        {
            var t = i < 2 ? d.Front : d.Rear;
            float defl = t.Radius - (st.HubH[i] - st.Road[i]);
            float deflRate = st.RoadRate(i, dt) - st.HubV[i];
            st.Load[i] = defl > 0 ? MathF.Max(0, t.Rate * defl + t.Damp * deflRate) : 0;
        }

        // ---- aero: each wing at its height and angle of attack
        float down = 0, downMoment = 0, drag = 0;
        float q = d.AirDensityHalf * vx * vx;
        foreach (var w in d.Wings)
        {
            float height = st.H + w.Y - w.Z * st.Pitch;
            float aoa = (st.Pitch + (st.HDot - w.Z * st.PitchRate) / MathF.Max(MathF.Abs(vx), 1)) * 180 / MathF.PI;
            var (clA, cdA) = w.Coefficients(aoa, height);
            down += q * clA;
            downMoment += q * clA * w.Z;
            drag += q * cdA * d.DragScale;
        }

        // ---- engine: turbos, limiter, coast, clutch
        float drivenOmega = d.Drive switch
        {
            1 => (st.Omega[0] + st.Omega[1]) / 2,
            2 => (st.Omega[0] + st.Omega[1] + st.Omega[2] + st.Omega[3]) / 4,
            _ => (st.Omega[2] + st.Omega[3]) / 2,
        };
        float ratio = inp.Ratio;
        float throttle = Math.Clamp(inp.Throttle, 0, 1);
        // gear change: the autoblip opens the throttle on a downshift, the autoclutch lets the clutch out along its profile
        float clutchCap = 1;
        if (!float.IsNaN(inp.ShiftTime))
        {
            float t = inp.ShiftTime;
            if (inp.Downshift && d.Blip is { } bp && d.BlipLevel > 0)
            {
                float blip = t < bp[0] ? d.BlipLevel * t / MathF.Max(1e-3f, bp[0]) : t < bp[1] ? d.BlipLevel
                    : t < bp[2] ? d.BlipLevel * (bp[2] - t) / MathF.Max(1e-3f, bp[2] - bp[1]) : 0;
                throttle = MathF.Max(throttle, blip);
            }
            if ((inp.Downshift ? d.DownClutch : d.UpClutch) is { } prof)
                clutchCap = t < prof[0] ? 1 - t / MathF.Max(1e-3f, prof[0]) : t < prof[1] ? 0 : t < prof[2] ? (t - prof[1]) / MathF.Max(1e-3f, prof[2] - prof[1]) : 1;
        }
        if (st.LimiterCut > 0) { st.LimiterCut -= dt; throttle = 0; }
        if (st.TcCut) throttle = 0;
        float rpmE = st.EngineOmega * 60 / (2 * MathF.PI);
        float boost = 0;
        for (int k = 0; k < d.Turbos.Count; k++)
        {
            var tb = d.Turbos[k];
            float target = VehicleData.TurboTarget(tb, rpmE, throttle);
            float lag = target > st.TurboLevel[k] ? tb.LagUp : tb.LagDn;
            st.TurboLevel[k] += (target - st.TurboLevel[k]) * (1 - MathF.Pow(Math.Clamp(lag, 0, 0.99999f), dt * 333.33f));
            boost += VehicleData.TurboBoost(tb, st.TurboLevel[k], rpmE);
        }
        float EngineTorque(float rpm)
        {
            float te = throttle * d.TorqueAt(rpm) * (1 + boost);
            if (rpm > d.IdleRpm) te -= (1 - throttle) * d.CoastTorque * rpm / MathF.Max(1000, d.CoastRpm);
            return te;
        }
        float engineToWheels = 0, engineInertiaAtWheels = 0;
        float ie = d.EngineInertia;
        float omegaIdle = d.IdleRpm * 2 * MathF.PI / 60, omegaLimit = d.Limiter * 2 * MathF.PI / 60;
        if (ratio != st.LastRatio) st.ClutchLocked = false;
        st.LastRatio = ratio;
        if (ratio > 0)
        {
            float wGear = drivenOmega * ratio;
            float launch = MathF.Min(d.Limiter * 0.6f, 4500) * 2 * MathF.PI / 60;
            // the engine would stall: the clutch opens
            if (st.ClutchLocked && wGear < omegaIdle * 0.9f) st.ClutchLocked = false;
            // the revs the car's speed (not spinning wheels) gives in this gear
            float wRoad = MathF.Max(0, vx) / (d.Drive == 1 ? d.Front.Radius : d.Rear.Radius) * ratio;
            if (!st.ClutchLocked && wRoad < launch * 0.85f && throttle > 0.05f)
            {
                // pulling away: the clutch slips with the engine held at the launch revs until the car catches up
                st.EngineOmega = launch;
                engineToWheels = MathF.Min(EngineTorque(launch * 60 / (2 * MathF.PI)), d.ClutchTorque) * ratio * VehicleData.Efficiency;
                if (wGear > launch) engineToWheels = MathF.Min(engineToWheels, 0); // spinning faster than the engine: the clutch holds them back
            }
            else if (!st.ClutchLocked && wGear < omegaIdle)
            {
                // rolling slowly off the throttle: the clutch stays open, the engine idles
                st.EngineOmega = omegaIdle;
            }
            else if (st.ClutchLocked && clutchCap >= 1)
            {
                st.EngineOmega = MathF.Max(wGear, omegaIdle);
                float te = EngineTorque(st.EngineOmega * 60 / (2 * MathF.PI));
                engineToWheels = te * ratio * (te > 0 ? VehicleData.Efficiency : 1 / VehicleData.Efficiency);
                engineInertiaAtWheels = ie * ratio * ratio;
            }
            else
            {
                // after a gear change the clutch closes with the revs still apart: it slips at its torque until they meet
                float slip = st.EngineOmega - wGear;
                float tc = d.ClutchTorque * clutchCap * Math.Clamp(slip / 5, -1, 1);
                float te = EngineTorque(st.EngineOmega * 60 / (2 * MathF.PI));
                float next = st.EngineOmega + dt * (te - tc) / ie;
                if ((MathF.Sign(next - wGear) != MathF.Sign(slip) || MathF.Abs(slip) < 1) && clutchCap >= 1)
                {
                    st.ClutchLocked = true;
                    st.EngineOmega = MathF.Max(wGear, omegaIdle);
                }
                else st.EngineOmega = Math.Clamp(next, omegaIdle, omegaLimit * 1.02f);
                engineToWheels = tc * ratio * VehicleData.Efficiency;
            }
        }
        else
        {
            // gear change in progress: the engine revs freely
            st.ClutchLocked = false;
            float te = EngineTorque(rpmE);
            st.EngineOmega = Math.Clamp(st.EngineOmega + dt * te / ie, omegaIdle, omegaLimit * 1.02f);
        }
        st.EngineRpm = st.EngineOmega * 60 / (2 * MathF.PI);
        if (st.EngineRpm >= d.Limiter && d.Limiter > 0 && st.LimiterCut <= 0) st.LimiterCut = 1 / d.LimiterHz;

        Span<float> drive = stackalloc float[4];
        Span<float> extraInertia = stackalloc float[4];
        switch (d.Drive)
        {
            case 1:
                drive[0] = drive[1] = engineToWheels / 2;
                extraInertia[0] = extraInertia[1] = engineInertiaAtWheels / 2;
                break;
            case 2:
                drive[0] = drive[1] = engineToWheels * d.AwdFrontShare / 2;
                drive[2] = drive[3] = engineToWheels * (1 - d.AwdFrontShare) / 2;
                for (int i = 0; i < 4; i++) extraInertia[i] = engineInertiaAtWheels / 4;
                break;
            default:
                drive[2] = drive[3] = engineToWheels / 2;
                extraInertia[2] = extraInertia[3] = engineInertiaAtWheels / 2;
                if (d.Drive == 3)
                {
                    // AWD2: the coupling sends torque to the front when the rear turns faster (CENTRE_RAMP_TORQUE per rad/s, up to MAX)
                    float slipAxles = (st.Omega[2] + st.Omega[3]) / 2 * d.Rear.Radius - (st.Omega[0] + st.Omega[1]) / 2 * d.Front.Radius;
                    float tcpl = Math.Clamp(d.CouplingRamp * slipAxles / d.Rear.Radius, 0, d.CouplingMax);
                    drive[0] += tcpl / 2; drive[1] += tcpl / 2;
                    drive[2] -= tcpl / 2; drive[3] -= tcpl / 2;
                }
                break;
        }

        // ---- electronics (electronics.ini RATE_HZ): ABS per wheel on the slip ratio, TC cuts the engine for a period
        st.AbsTimer += dt;
        if (st.AbsTimer >= 1 / d.AbsRateHz)
        {
            st.AbsTimer = 0;
            for (int i = 0; i < 4; i++) st.AbsRelease[i] = d.Abs && vx > 20 / 3.6f && st.Kappa[i] < -d.AbsSlip;
        }
        st.TcTimer += dt;
        if (st.TcTimer >= 1 / d.TcRateHz)
        {
            st.TcTimer = 0;
            float k = 0;
            for (int i = 0; i < 4; i++)
                if (d.Drive switch { 1 => i < 2, 0 => i >= 2, _ => true }) k = MathF.Max(k, st.Kappa[i]);
            st.TcCut = d.Tc && vx > MathF.Max(1, d.TcMinSpeed) && k > d.TcSlip;
        }

        // ---- wheels: slip, tyre forces, spin
        float fxBody = 0, fyBody = 0, mz = 0;
        Span<float> fxw = stackalloc float[4], fyw = stackalloc float[4];
        float brakeF = inp.Brake * d.BrakeTorque * d.BrakeFront, brakeR = inp.Brake * d.BrakeTorque * (1 - d.BrakeFront);
        float frontSlip = 0, rearSlip = 0, frontUse = 0, rearUse = 0;
        bool frontLocked = false, drivenSpin = false;
        float totalLoad = st.Load[0] + st.Load[1] + st.Load[2] + st.Load[3];
        Span<float> inertia = stackalloc float[4];
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2;
            var t = front ? d.Front : d.Rear;
            float x = xs[i], y = ys[i];
            float c = MathF.Cos(st.WheelAngle[i]), s = MathF.Sin(st.WheelAngle[i]);
            float vxw = vx - r * y, vyw = vy + r * x;
            float vl = vxw * c + vyw * s, vt = -vxw * s + vyw * c;
            float fz = st.Load[i];
            float speed = MathF.Sqrt(vl * vl + vt * vt);
            float w0 = st.Omega[i];
            float radius = t.Radius + t.RadiusAngularK * MathF.Abs(w0) * 0.001f;
            bool slow = speed < 1;
            float denom = MathF.Max(MathF.Abs(vl), 1);
            float alphaRaw = slow ? Math.Clamp(5.5f * vt, -1, 1) : MathF.Atan2(vt, MathF.Abs(vl));
            // relaxation length: longer at light slip and high load (AC: Tyre::stepRelaxationLength)
            float relax = (t.RelaxationLength * (MathF.Max(fz, 1) / t.Fz0 - 1) * 0.3f + t.RelaxationLength) * 2;
            relax += (t.RelaxationLength - relax) * Math.Clamp(st.NdSlip[i], 0, 1);
            float f = relax > 0 ? Math.Clamp(speed * dt / relax, 0.04f, 1) : 1;
            float alpha = st.Alpha[i] + f * (alphaRaw - st.Alpha[i]);
            float vlLocal = vl;
            float KappaRaw(float w) => slow ? Math.Clamp(0.5f * (w * radius - vlLocal), -1, 1) : (w * radius - vlLocal) / denom;
            float dKdW = f * radius * (slow ? 0.5f : 1 / denom);
            var heat = st.Heat?[i];
            float u = (inp.Grip > 0 ? inp.Grip : 1) * st.RoadGrip[i] * d.GripScale * (heat?.Grip ?? 1);

            float k0 = st.Kappa[i] + f * (KappaRaw(w0) - st.Kappa[i]);
            var (fx0, fy, nd) = t.Solve(fz, alpha, k0, lean[i], speed, u, inp.NoFalloff);
            const float eps = 1e-3f;
            var (fxE, _, _) = t.Solve(fz, alpha, k0 + eps, lean[i], speed, u, inp.NoFalloff);
            float dFxdK = (fxE - fx0) / eps;
            float dFxdW = dFxdK * dKdW;

            // brake: the car's ABS releases a locking wheel for a period; the AC AI's override releases any wheel past its peak
            float tb = front ? brakeF : brakeR;
            if (st.AbsRelease[i]) tb = 0;
            if (inp.AiAbs && st.NdSlip[i] > 1 && fz > 0.05f * totalLoad) tb = 0;
            float iw = t.AngularInertia + extraInertia[i];
            inertia[i] = iw;
            float rr = fz * 0.001f * (t.Rr0 + t.Rr1 * w0 * radius * w0 * radius) * (1 + 0.001f * t.RrSlip * Math.Clamp(st.NdSlip[i], 0, 1)) * (heat?.RollFactor ?? 1);
            float sign = w0 > 0.05f ? 1 : w0 < -0.05f ? -1 : 0;
            float torque = drive[i] - radius * fx0 - rr * radius * sign;
            float w1 = w0 + dt * (torque - tb * sign) / (iw + dt * radius * dFxdW);
            // the brake holds a wheel instead of turning it backwards through zero
            if (sign != 0 && MathF.Sign(w1) != sign) w1 = 0;
            else if (sign == 0 && MathF.Abs(drive[i] - radius * fx0) <= tb) w1 = 0;
            st.Omega[i] = w1;
            float k1 = st.Kappa[i] + f * (KappaRaw(w1) - st.Kappa[i]);
            float fx = fx0 + dFxdK * (k1 - k0);
            st.Kappa[i] = k1;
            st.Alpha[i] = alpha;
            heat?.Step(t, dt, w1, fz, MathF.Sqrt(vt * vt + Sq(w1 * radius - vl)), -lean[i], vx, inp.AmbientTemp, inp.RoadTemp);
            st.NdSlip[i] = nd;
            fy *= lateralGrip;

            float fxb = fx * c - fy * s, fyb = fx * s + fy * c;
            fxw[i] = fxb;
            fyw[i] = fyb;
            fxBody += fxb;
            fyBody += fyb;
            mz += x * fyb - y * fxb;

            float sy = MathF.Abs(MathF.Tan(alpha)) / MathF.Max(1e-3f, t.PeakSlip(MathF.Max(fz, 1), u));
            if (front)
            {
                frontSlip = MathF.Max(frontSlip, sy);
                frontUse = MathF.Max(frontUse, nd);
                if (tb > 0 && k1 < -0.5f) frontLocked = true;
            }
            else
            {
                rearSlip = MathF.Max(rearSlip, sy);
                rearUse = MathF.Max(rearUse, nd);
            }
            bool driven = d.Drive switch { 1 => front, 0 => !front, _ => true };
            if (driven && drive[i] > 0 && nd > 1.3f && k1 > 0) drivenSpin = true;
        }

        // limited slip differentials: the wheels of an axle are locked together up to the locking torque (an impulse evening out their speeds)
        if (d.Drive != 0) LockPair(st, inertia, 0, 1, d.FrontDiff.Lock(drive[0] + drive[1]), dt);
        if (d.Drive != 1) LockPair(st, inertia, 2, 3, d.RearDiff.Lock(drive[2] + drive[3]), dt);
        if (d.Drive == 2)
        {
            // centre differential between the axles (speeds at the road, so different tyre sizes don't wind it up)
            float vf = (st.Omega[0] + st.Omega[1]) / 2 * d.Front.Radius, vr = (st.Omega[2] + st.Omega[3]) / 2 * d.Rear.Radius;
            float iF = (inertia[0] + inertia[1]) / (d.Front.Radius * d.Front.Radius), iR = (inertia[2] + inertia[3]) / (d.Rear.Radius * d.Rear.Radius);
            float fEq = (vf - vr) * (iF * iR / (iF + iR)) / dt;
            float lockC = d.CentreDiff.Lock(engineToWheels) / d.Rear.Radius;
            float fTr = Math.Clamp(fEq, -lockC, lockC);
            for (int i = 0; i < 2; i++) st.Omega[i] -= fTr / 2 * d.Front.Radius * dt / inertia[i];
            for (int i = 2; i < 4; i++) st.Omega[i] += fTr / 2 * d.Rear.Radius * dt / inertia[i];
        }

        st.FrontSlip = frontSlip;
        st.RearSlip = rearSlip;
        st.FrontUse = frontUse;
        st.RearUse = rearUse;
        st.FrontLocked = frontLocked;
        st.DrivenSpinning = drivenSpin;

        // what the pedals may do before an axle lets go, with what this cornering leaves of the grip
        float u0 = inp.Grip > 0 ? inp.Grip : 1;
        float muxF = (d.Front.StaticDx(st.Load[0]) * st.Load[0] + d.Front.StaticDx(st.Load[1]) * st.Load[1]) * u0;
        float muxR = (d.Rear.StaticDx(st.Load[2]) * st.Load[2] + d.Rear.StaticDx(st.Load[3]) * st.Load[3]) * u0;
        float roomF = MathF.Sqrt(MathF.Max(0, 1 - MathF.Min(1, frontSlip) * MathF.Min(1, frontSlip)));
        float roomR = MathF.Sqrt(MathF.Max(0, 1 - MathF.Min(1, rearSlip) * MathF.Min(1, rearSlip)));
        float fullF = 2 * d.BrakeTorque * d.BrakeFront / d.Front.Radius, fullR = 2 * d.BrakeTorque * (1 - d.BrakeFront) / d.Rear.Radius;
        st.BrakeLimit = MathF.Min(muxF * roomF / MathF.Max(1, fullF), muxR * roomR / MathF.Max(1, fullR));
        st.BrakeLockLimit = MathF.Min(muxF / MathF.Max(1, fullF), muxR / MathF.Max(1, fullR));
        float driveR = d.Drive == 1 ? d.Front.Radius : d.Rear.Radius;
        float fullDrive = ratio > 0 ? d.FullTorqueAt(MathF.Max(st.EngineRpm, d.IdleRpm)) * ratio * VehicleData.Efficiency / driveR : 0;
        float traction = d.Drive switch { 1 => muxF, 0 => muxR, _ => muxF + muxR };
        st.DriveRatio = traction > 1 ? MathF.Max(0.05f, fullDrive / traction) : 1;
        st.ThrottleLimit = fullDrive <= 1 ? 1 : d.Drive switch
        {
            1 => muxF * roomF / fullDrive,
            0 => muxR * roomR / fullDrive,
            _ => (muxF * roomF + muxR * roomR) / fullDrive,
        };

        // ---- the body moves in the plane
        float resist = drag * MathF.Sign(vx);
        st.Ax = (fxBody - resist + inp.ExtraFx + m * inp.GravityX) / m;
        st.Ay = (fyBody + inp.ExtraFy + m * inp.GravityY) / m;
        vx += (st.Ax + r * vy) * dt;
        vy += (st.Ay - r * vx) * dt;
        r += (mz + inp.ExtraMz) / (d.YawInertia * massRatio) * dt;
        // standing and creeping: below walking pace the slip angles mean nothing, the car rolls like a cart
        float wBlend = Math.Clamp((MathF.Abs(vx) - 0.5f) / 1.5f, 0, 1);
        if (wBlend < 1)
        {
            float mean = (st.WheelAngle[0] + st.WheelAngle[1]) / 2;
            r = wBlend * r + (1 - wBlend) * vx * MathF.Tan(mean) / d.Wheelbase;
            // (turning about the rear axle: no sideways speed there)
            vy = wBlend * vy + (1 - wBlend) * r * d.B;
        }
        st.Vx = vx;
        st.Vy = vy;
        st.R = r;

        // ---- vertical: hubs on their tyres, the body on its springs; tyre forces push through the linkage (jacking)
        float fBody = -down - ms * gEff, mPitch = downMoment - ms * st.Ax * st.H, mRoll = -ms * st.Ay * st.H;
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2, plus = i % 2 == 0;
            var sa = front ? d.FrontSusp : d.RearSusp;
            var (dyCp, dxCp, _, dPitch) = sa.Jacking(st.Travel[i]);
            float fIn = plus ? -fyw[i] : fyw[i];
            float qj = fIn * dyCp + fxw[i] * dxCp + drive[i] * dPitch;
            float net = spring[i] - qj; // up on the body
            fBody += net;
            mPitch -= xs[i] * net;
            mRoll -= ys[i] * net;
            st.HubV[i] += dt * (st.Load[i] - net - sa.HubMass * gEff) / sa.HubMass;
            st.HubH[i] += st.HubV[i] * dt;
        }
        float bodyShare = ms / m;
        st.HDot += dt * fBody / ms;
        st.H += st.HDot * dt;
        st.PitchRate += dt * mPitch / (d.PitchInertia * massRatio * bodyShare);
        st.Pitch += st.PitchRate * dt;
        st.RollRate += dt * mRoll / (d.RollInertia * massRatio * bodyShare);
        st.Roll += st.RollRate * dt;
        // a GT car neither tips over nor wheelies: the body is held within -0.25/+0.6 m of its rest height (crests let it go light) and 8° of
        // pitch and roll. The model has no rollover: sliding sideways or spinning its wheels against a wall it pumped itself up until it flew apart
        Hold(ref st.H, ref st.HDot, d.CgHeight - 0.25f, d.CgHeight + 0.6f);
        Hold(ref st.Pitch, ref st.PitchRate, d.StaticPitch - 0.14f, d.StaticPitch + 0.14f);
        Hold(ref st.Roll, ref st.RollRate, -0.14f, 0.14f);

        // move along the line
        float lineDenom = MathF.Max(0.2f, 1 - inp.Curvature * st.N);
        float cp = MathF.Cos(st.Psi), sp = MathF.Sin(st.Psi);
        st.SDot = (vx * cp - vy * sp) / lineDenom;
        st.NDot = vx * sp + vy * cp;
        st.S += st.SDot * dt;
        st.N += st.NDot * dt;
        st.Psi += (r - inp.Curvature * st.SDot) * dt;
    }

    private static float Sq(float x) => x * x;

    private static void Hold(ref float x, ref float rate, float lo, float hi)
    {
        if (x < lo) { x = lo; rate = MathF.Max(rate, 0); }
        else if (x > hi) { x = hi; rate = MathF.Min(rate, 0); }
    }

    /// <summary>A differential's locking between two wheels: the torque that evens out their speeds this step, up to the locking torque.</summary>
    private static void LockPair(VehicleState st, Span<float> inertia, int a, int b, float lockTorque, float dt)
    {
        if (lockTorque <= 0) return;
        float ia = inertia[a], ib = inertia[b];
        float tEq = (st.Omega[a] - st.Omega[b]) * (ia * ib / (ia + ib)) / dt;
        float tTr = Math.Clamp(tEq, -lockTorque, lockTorque);
        st.Omega[a] -= tTr * dt / ia;
        st.Omega[b] += tTr * dt / ib;
    }
}
