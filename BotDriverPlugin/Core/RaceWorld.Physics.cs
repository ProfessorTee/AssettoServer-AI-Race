using System.Numerics;
using BotDriverPlugin.Core.Physics;

namespace BotDriverPlugin.Core;

/// <summary>
/// The AC AI's driver (acs.exe AIDriver, see UEBERGABE_AC_KI.md) for a bot driving with the vehicle model: steering, pedals and gears like
/// Kunos' code, with the AI's help in the physics (ultra grip without falloff, perfect ABS, traction control, spring to the line, brake help).
/// </summary>
internal sealed class PhysicsDriver
{
    /// <summary>Steering output (−0.9..0.9 of full lock) and the PID's memory (AIDriver::stepSteer, PID at ai+0x50).</summary>
    public float Steer, PidIntegral, PidPrev;
    /// <summary>Last steering demand (7·asin of the direction to the target point, ai+0x3e4).</summary>
    public float SteerDemand;
    public float LateralGrip = 1;
    public int Sub;
    /// <summary>Genome: 20 push factors 0.98..1.02, one per 5 % of the lap (normalised to a mean of 1).</summary>
    public readonly float[] Genome = new float[20];
    /// <summary>Downshift rpm jitter (±7.5 %) and the time of the last gear change.</summary>
    public float DownJitter = 1;
    public double LastShift = double.NegativeInfinity;
    /// <summary>Time since the last gear change started (physics time) and its direction.</summary>
    public float ShiftClock = float.NaN;
    public bool Downshift;
    /// <summary>Pulling away from the grid (AC: ai+0x2a0), until 50 km/h.</summary>
    public bool StartPhase = true;

    public PhysicsDriver(Random rng)
    {
        for (int i = 0; i < Genome.Length; i++) Genome[i] = 0.98f + 0.04f * rng.NextSingle();
        float mean = Genome.Average();
        for (int i = 0; i < Genome.Length; i++) Genome[i] /= mean;
        DownJitter = 1 + 0.15f * rng.NextSingle() - 0.075f;
    }

    public float GenomeAt(float s, float length) => Genome[Math.Clamp((int)(s / length * Genome.Length), 0, Genome.Length - 1)];
}

/// <summary>
/// Driving with the vehicle model (<see cref="RaceWorldSettings.PhysicsDriving"/>): the racecraft (Think) still decides where to drive and
/// how fast, the AC AI's driver turns that into steering and pedals, and the car's physics decide what happens. A spin (stopping, turning
/// round, rejoining) and the pit lane use the kinematic model.
/// </summary>
public sealed partial class RaceWorld
{
    /// <summary>Physics step (s): AC runs at 333 Hz; the tyre forces need small steps.</summary>
    private const float PhysicsStep = 1f / 300f;
    /// <summary>The AI decides every 5th physics step (AC: index % 5).</summary>
    private const int AiEvery = 5;

    private bool UsesPhysics(RaceBot me) => Settings.PhysicsDriving && me.Car.Physics != null && me.Mistake != MistakeKind.Spin && !me.InPitLane;

    /// <summary>The road under the cars (bumps, kerbs, surfaces), null = the line's flat road.</summary>
    public RoadSurface? Road { get; set; }

    private void PhysicsIntegrate(RaceBot me, float dt)
    {
        var d = me.Car.Physics!;
        var st = me.Phys;
        var ai = me.PhysAi ??= new PhysicsDriver(_rng);
        float s0 = Line.WrapS((float)me.Distance);
        if (st == null)
        {
            // take over from the kinematic model: heading = direction of travel turned by the yaw it showed
            st = me.Phys = new VehicleState
            {
                S = me.Distance, N = me.Offset, Vx = me.Speed,
                Psi = MathF.Atan2(me.LateralSpeed, MathF.Max(me.Speed, 1)) + me.Yaw,
                R = me.Speed * Line.CurvatureAt(s0),
            };
            SampleRoad(me, st);
            ai.Steer = 0;
            ai.PidIntegral = ai.PidPrev = 0;
        }
        else
        {
            // something outside the model moved the car (contact, overlap, a placement): follow it
            if (MathF.Abs(st.WrittenOffset - me.Offset) > 1e-3f) { st.N = me.Offset; st.Vy *= 0.5f; }
            if (MathF.Abs(st.WrittenSpeed - me.Speed) > 1e-3f) st.Vx = me.Speed;
            // a contact bounced the car sideways
            if (MathF.Abs(st.WrittenLateral - me.LateralSpeed) > 1e-3f) st.Vy += me.LateralSpeed - st.WrittenLateral;
            st.S = me.Distance;
        }

        // ---- what the racecraft wants
        float target = me.TargetSpeed;
        if (!double.IsNaN(me.LaunchAt) && _now < me.LaunchAt && me.Speed < 1 && me.LapsCompleted == 0) target = 0;
        float targetFall = float.IsNaN(me.PrevTargetSpeed) ? 0 : MathF.Max(0, (me.PrevTargetSpeed - target) / dt);
        me.PrevTargetSpeed = target;
        // where the racecraft wants the car: on its own line (shifted by what cars alongside leave of it) or at its target offset
        bool ownLine = me.OnOwnLine && me.OvertakeTargetId < 0 && me.Mistake == MistakeKind.None && !me.Weaving;
        float lineShift = ownLine ? me.TargetOffset - OwnLineOffset(me, s0) : 0;
        float WantOffset(float s) => ownLine ? OwnLineOffset(me, s) + lineShift : me.TargetOffset;
        // mistakes in the physics: braked too late = AC's humanizer (brake pressure doubled, no ABS help); too much throttle on the exit
        bool brakeMistake = me.Mistake == MistakeKind.LateBrake;
        bool gasMistake = me.Mistake == MistakeKind.Slide;
        float phys = Settings.GripFactor * me.CarGrip;
        float skill = me.Driver.Pace + me.PaceNoise + me.PaceBoost + me.DayForm;
        float m = d.Mass * me.MassRatio;

        int n = Math.Max(1, (int)MathF.Ceiling(dt / PhysicsStep));
        float h = dt / n;
        var inp = new VehicleInputs { MassRatio = me.MassRatio, Grip = phys, NoFalloff = true };
        for (int k = 0; k < n; k++)
        {
            float s = Line.WrapS((float)st.S);
            if (ai.Sub++ % AiEvery == 0)
            {
                Gears(me, d, st, ai);
                Decide(me, d, st, ai, AiEvery * h, s, target, targetFall, WantOffset, brakeMistake, gasMistake, skill);
            }
            bool shifting = me.ShiftTo > 0 && _now < me.ShiftEnd;
            if (me.ShiftTo > 0 && _now >= me.ShiftEnd) { me.Gear = me.ShiftTo; me.ShiftTo = 0; }
            inp.Ratio = shifting || me.Gear <= 0 ? 0 : d.Ratios[Math.Clamp(me.Gear, 1, d.Ratios.Length) - 1] * d.Final;
            if (!float.IsNaN(ai.ShiftClock)) ai.ShiftClock = ai.ShiftClock > 3 ? float.NaN : ai.ShiftClock + h;
            inp.ShiftTime = ai.ShiftClock;
            inp.Downshift = ai.Downshift;
            inp.Steer = ai.Steer * d.MaxSteer;
            inp.Throttle = me.Throttle;
            inp.Brake = me.Brake;
            inp.LateralGrip = ai.LateralGrip;
            inp.AiAbs = !brakeMistake;

            // the AC AI's help every physics step (AIDriver::stepSuperhuman): a spring to its line and extra braking before its brake point
            float v = st.Vx;
            float err = WantOffset(s) - st.N;
            bool beside = me.Alongside;
            inp.ExtraFy = 0;
            if (v * 3.6f > 20 && MathF.Abs(err) < (beside ? 6 : 4) && _now - Math.Max(me.LastContactAt, me.LastOverlapAt) > 1)
                inp.ExtraFy = m * Math.Clamp(err, -2, 2) * (beside ? 2 : 1);
            inp.ExtraFx = 0;
            if (me.LimitDist < 50 && v > me.LimitSpeed)
                inp.ExtraFx = -5 * m * Math.Clamp(0.3f * (v - me.LimitSpeed), 0, 1);

            inp.Curvature = Line.CurvatureAt(s);
            int idx = Line.IndexAt(s);
            inp.VerticalCurvature = Line.VerticalCurvature[idx];
            // gravity along and across the road (downhill, banking) from the line's direction and road normal
            var fwd = Line.ForwardAt(s);
            var lat = Line.LateralAt(s);
            var nrm = Line.Normal[idx];
            var latRoad = lat - Vector3.Dot(lat, nrm) * nrm;
            inp.GravityX = -CarSpec.G * fwd.Y;
            inp.GravityY = latRoad.LengthSquared() > 1e-6f ? -CarSpec.G * Vector3.Normalize(latRoad).Y : 0;
            SampleRoad(me, st);
            Vehicle.Step(d, st, inp, h);
        }

        // ---- back to the race world
        me.Distance = st.S;
        me.Offset = st.N;
        me.Speed = st.Vx;
        me.LateralSpeed = st.NDot;
        me.Accel = st.Ax;
        float sNow = Line.WrapS((float)st.S);
        float along = st.SDot * MathF.Max(0.2f, 1 - Line.CurvatureAt(sNow) * st.N);
        me.Yaw = st.Psi - MathF.Atan2(st.NDot, MathF.Max(along, 0.5f));
        me.FrontLock = st.FrontLocked;
        me.RearSlip = st.DrivenSpinning ? 1.3f : 1f;
        float before = me.Offset;
        ClampOffsetWithAllowance(me);
        if (MathF.Abs(me.Offset - before) > 1e-3f)
        {
            // at the edge of the road: the car is held there and loses its sideways motion
            st.N = me.Offset;
            st.Vy *= 0.3f;
            st.Psi *= 0.7f;
        }
        if (TraceBotId == me.Id && Trace != null && (int)(_now * 4) != (int)((_now - dt) * 4))
            Trace(FormattableString.Invariant($"phys t={_now:F2} s={sNow:F0} v={st.Vx * 3.6f:F0} tgt={target * 3.6f:F0} thr={me.Throttle:F2} brk={me.Brake:F2} g={me.Gear} rpm={st.EngineRpm:F0} steer={st.Steer * 57.3f:F1} psi={st.Psi * 57.3f:F1} vy={st.Vy:F2} r={st.R:F2} n={st.N:F2}/{WantOffset(sNow):F2} slipF={st.FrontSlip:F2} slipR={st.RearSlip:F2} nd={st.NdSlip[0]:F2}/{st.NdSlip[1]:F2}/{st.NdSlip[2]:F2}/{st.NdSlip[3]:F2} ax={st.Ax:F1} ay={st.Ay:F1} h={st.H:F3} roll={st.Roll * 57.3f:F1} pitch={st.Pitch * 57.3f:F2} fz={st.Load[0]:F0}/{st.Load[1]:F0}/{st.Load[2]:F0}/{st.Load[3]:F0} k={st.Kappa[2]:F2}/{st.Kappa[3]:F2} lim={me.LimitDist:F0}/{me.LimitSpeed * 3.6f:F0} clutch={st.ClutchLocked} grip={ai.LateralGrip:F3} road={st.Road[0]:F3}/{st.Road[2]:F3} surf={st.RoadGrip[0]:F2}"));
        st.WrittenOffset = me.Offset;
        st.WrittenSpeed = me.Speed;
        st.WrittenLateral = me.LateralSpeed;
    }

    /// <summary>
    /// AIDriver::stepGears: up above the UP revs at full throttle (or in first) unless the driven wheels spin; down when the revs in the
    /// gear below would still be under UP × DOWN (±7.5 % per driver) and the driver isn't on full throttle; at least 1 s between changes.
    /// </summary>
    private void Gears(RaceBot me, VehicleData d, VehicleState st, PhysicsDriver ai)
    {
        if (me.Gear <= 0 || me.Gear > d.Ratios.Length)
        {
            // taking over: the gear that suits the speed
            float r = Envelope.RatioFor(d, MathF.Max(st.Vx, 1)) / d.Final;
            me.Gear = Math.Max(1, Array.IndexOf(d.Ratios, r) + 1);
            return;
        }
        if (me.ShiftTo > 0 || _now - ai.LastShift < 1) return;
        float up = MathF.Min(d.AiUpRpm, d.Limiter);
        float down = up * d.AiDownFactor * ai.DownJitter;
        float drivenSlip = d.Drive == 1 ? MathF.Max(st.Kappa[0], st.Kappa[1]) : MathF.Max(st.Kappa[2], st.Kappa[3]);
        if (me.Gear < d.Ratios.Length && st.EngineRpm > up && (st.Vx < 1 || drivenSlip < 1) && (me.Throttle > 0.99f || me.Gear == 1))
        {
            Shift(me, me.Gear + 1, me.Car.ShiftUpTime);
            ai.LastShift = _now;
            ai.ShiftClock = 0;
            ai.Downshift = false;
            return;
        }
        if (me.Gear >= 2 && me.Throttle <= 0.98f)
        {
            float rpmBelow = st.EngineRpm * d.Ratios[me.Gear - 2] / d.Ratios[me.Gear - 1];
            if (rpmBelow > 0 && rpmBelow < down && rpmBelow < d.Limiter * 0.97f)
            {
                Shift(me, me.Gear - 1, me.Car.ShiftDownTime);
                ai.LastShift = _now;
                ai.ShiftClock = 0;
                ai.Downshift = true;
            }
        }
    }

    /// <summary>The AI's decision (every 5th physics step): steering (AIDriver::stepSteer), throttle and brake (AIDriver::stepGasBrake).</summary>
    private void Decide(RaceBot me, VehicleData d, VehicleState st, PhysicsDriver ai, float dtAi, float s0, float target, float targetFall,
        Func<float, float> wantOffset, bool brakeMistake, bool gasMistake, float skill)
    {
        float v = st.Vx;

        // ---- steering: aim at a point ahead on the wanted line; PID on 7·asin of its direction (STEER_GAIN·0.27, 0.02, 0.02)
        float lookahead = 3f + Math.Clamp(v * 0.018f, 0, 1) * d.AiLookahead;
        float off = wantOffset(s0 + lookahead);
        float th = 0, px = 0, py = 0;
        int steps = Math.Max(1, (int)lookahead);
        float ds = lookahead / steps;
        for (int i = 0; i < steps; i++)
        {
            th += Line.CurvatureAt(s0 + (i + 0.5f) * ds) * ds;
            px += MathF.Cos(th) * ds;
            py += MathF.Sin(th) * ds;
        }
        float dx = px - off * MathF.Sin(th), dy = py + off * MathF.Cos(th) - st.N;
        float lat = MathF.Sin(MathF.Atan2(dy, dx) - st.Psi);
        float demand = MathF.Asin(Math.Clamp(lat, -1, 1)) * 7;
        ai.SteerDemand = demand;
        if (v * 3.6f < 5) { ai.PidIntegral = 0; ai.PidPrev = 0; }
        float kp = d.AiSteerGain * 0.27f;
        ai.PidIntegral += demand * dtAi;
        float pid = kp * demand + 0.02f * ai.PidIntegral + 0.02f * (demand - ai.PidPrev) / dtAi;
        ai.PidPrev = demand;
        if (!float.IsFinite(pid)) { pid = 0; ai.PidIntegral = ai.PidPrev = 0; }
        pid = Math.Clamp(pid, -0.9f, 0.9f);
        float lo = -0.9f, hi = 0.9f;
        if (v * 3.6f > 30)
        {
            // never more than ±10° of slip at the more loaded front wheel (12 = AC's assumed steering ratio)
            int fw = st.Load[0] >= st.Load[1] ? 0 : 1;
            float alphaDeg = st.Alpha[fw] * 180 / MathF.PI;
            float lockDeg = d.MaxSteer * 180 / MathF.PI * MathF.Abs(d.SteerRatio);
            float k = 12 / MathF.Max(1, lockDeg);
            lo = (alphaDeg - 10) * k + ai.Steer;
            hi = (alphaDeg + 10) * k + ai.Steer;
        }
        pid = Math.Clamp(pid, ai.Steer - 0.4f, ai.Steer + 0.4f);
        ai.Steer = pid > hi ? hi : pid < lo ? lo : pid;

        // ---- target speed (stepGasBrake): wide of the line, understeer, never more than 15 m/s above the speed now
        float vt = target;
        float kHere = Line.CurvatureAt(s0);
        float wide = (wantOffset(s0) - st.N) * MathF.Sign(kHere);
        if (wide > 0 && MathF.Abs(kHere) > 1 / 1000f && v > 5)
            vt += (v / (wide * 0.055f + 1) - vt) * Math.Clamp(MathF.Abs(demand * 0.6666667f), 0, 1);
        float ndF = MathF.Max(st.NdSlip[0], st.NdSlip[1]), ndR = MathF.Max(st.NdSlip[2], st.NdSlip[3]);
        float under = ndF - ndR;
        if (v * 3.6f > 30 && MathF.Max(ndF, ndR) * d.AiUndersteer > 1 && under > 0 && vt > v)
            vt = Math.Clamp(1 - Math.Clamp(under, 0, 1) * 0.2f * d.AiUndersteer, 0, 1) * v;
        vt = MathF.Min(vt, v + 15);

        // ---- brake (getLABraking): up to the pedal where an axle locks (×0.99; ×2 in a mistake), following the racecraft's braking
        // curve; the rules above only change the throttle target
        float brakeNorm = 0;
        if (target < v - 0.3f)
        {
            float brakeCap = me.Car.BrakeAt(v, Settings.GripFactor * me.CarGrip, me.MassRatio);
            brakeNorm = Math.Clamp((targetFall * 0.9f + (v - target) / 0.4f) / MathF.Max(1, brakeCap), 0, 1);
            if (target >= v - 1.5f && me.Brake < 0.05f) brakeNorm = 0;
        }
        float maxBrake = st.BrakeLockLimit * 0.99f * (brakeMistake ? 2 : 1);
        float brake = MathF.Min(1, brakeNorm * maxBrake);

        // ---- throttle: P on the speed error over the car's drive/traction ratio, plus what holds the speed against the drag
        float full = MathF.Max(1, me.Car.AccelAt(v, 1) + me.Car.DragCoefficient * v * v);
        float hold = Math.Clamp(me.Car.DragCoefficient * v * v / full, 0, 1);
        float throttle = Math.Clamp(0.6f * (vt - v) / MathF.Max(0.05f, st.DriveRatio) + hold, 0, 1);
        if (brakeNorm > 0) throttle = Math.Clamp(throttle - brakeNorm * 4, 0, 1);
        bool limiter = st.EngineRpm >= d.Limiter * 0.99f;
        if (limiter) throttle = 0.5f;
        // weaker drivers are later and more careful on the power out of a corner, on the straights everybody is flat out
        float cornerLoad = Math.Clamp(MathF.Abs(st.Ay) / (me.Car.LateralGrip * CarSpec.G) * 1.3f, 0, 1);
        if (!gasMistake) throttle *= 1 - (1 - DriverProfile.ThrottleSkill(skill)) * cornerLoad;
        // AI traction control (stepSuperhuman): driven tyres past their peak → 20 % of the throttle
        float drivenNd = d.Drive switch { 1 => ndF, 0 => ndR, _ => MathF.Max(ndF, ndR * (1 / 1.2f)) };
        // (pulling away from the grid: until 50 km/h much more slip allowed below 20 km/h, and only down to half the throttle)
        bool start = me.LapsCompleted == 0 && !double.IsNaN(me.LaunchAt) && _now - me.LaunchAt < 20 && v * 3.6f < 50 && ai.StartPhase;
        if (v * 3.6f >= 50) ai.StartPhase = false;
        float tcLimit = start ? (v * 3.6f < 20 ? (20 - v * 3.6f) * 10 + 4 : 4) : 1;
        if (gasMistake) throttle = MathF.Min(1, throttle * (1 + 0.5f * me.MistakeSeverity));
        else if (drivenNd > tcLimit && v * 3.6f > 5) throttle *= start ? 0.5f : 0.2f;

        me.Throttle = throttle;
        me.Brake = brake;
        // ultra grip (stepGasBrake): 1.001 on the throttle alone (no car beside, not on the limiter), the car's ULTRA_GRIP otherwise
        float ultra = d.AiUltraGrip * Settings.PhysicsCorneringGrip;
        ai.LateralGrip = throttle > 0 && brake <= 0 && !me.Alongside && !limiter ? 1.001f : ultra;
    }

    /// <summary>The road under each wheel: bumps and kerbs (height relative to the line's road plane) and the surface's grip.</summary>
    private void SampleRoad(RaceBot me, VehicleState st)
    {
        var d = me.Car.Physics!;
        float s = Line.WrapS((float)st.S);
        float cp = MathF.Cos(st.Psi), sp = MathF.Sin(st.Psi);
        for (int i = 0; i < 4; i++)
        {
            float x = i < 2 ? d.A : -d.B, y = (i % 2 == 0 ? 0.5f : -0.5f) * (i < 2 ? d.TrackFront : d.TrackRear);
            float ws = s + x * cp - y * sp, wn = st.N + x * sp + y * cp;
            var p0 = Line.PositionAt(ws, 0);
            var p = Line.PositionAt(ws, wn);
            float bump = 0, grip = 1;
            if (Road != null && Road.HeightAt(p0.X, p0.Z, p0.Y) is { } h0 && Road.HeightAt(p.X, p.Z, p.Y + h0 - p0.Y) is { } hw)
            {
                bump = hw - h0 - (p.Y - p0.Y);
                // more than 10 cm off the line's road plane is a mesh artefact (another surface found, a seam), not a bump or kerb
                if (MathF.Abs(bump) > 0.1f) bump = 0;
                // kerbs and other track surfaces have their own grip (surfaces.ini FRICTION)
                if (Road.SurfaceAt(p.X, p.Z, p.Y + h0 - p0.Y) is { } rs) grip = rs.Friction;
            }
            else if (OffTrack != null && OffTrack.SurfaceAt(p.X, p.Z, p.Y) is { } surf)
                grip = surf.Friction;
            st.Road[i] = bump;
            st.RoadGrip[i] = grip;
        }
    }
}
