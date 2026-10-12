using System.Numerics;
using BotDriverPlugin.Core.Physics;

namespace BotDriverPlugin.Core;

/// <summary>
/// The state of one AC AI driver (acs.exe AIDriver, see UEBERGABE_AC_KI.md): steering PID, genome, push variation, humanizer, gears.
/// </summary>
internal sealed class KunosAi
{
    /// <summary>Steering output (−0.9..0.9 of full lock) and the PID's memory (AIDriver::stepSteer, PID at ai+0x50).</summary>
    public float Steer, PidIntegral, PidPrev;
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
    /// <summary>Push variation (stepPushVariations): from <see cref="PushFrom"/> to <see cref="PushTo"/> between the two times.</summary>
    public float PushFrom, PushTo;
    public double PushFromAt = double.NaN, PushToAt;
    /// <summary>Humanizer (stepHumanizer): next mistake at, current mistake until.</summary>
    public double NextMistakeAt = double.NaN, MistakeUntil = double.NegativeInfinity;
    /// <summary>The offset the AI steers for (getEngagedDesiredOffset, changes with at most 3-4 m/s) and the car it is passing.</summary>
    public float DesiredOffset;
    public int PassingId = -1;
    /// <summary>The throttle the AI wants before the level's cap (the gearbox shifts up on that).</summary>
    public float ThrottleWanted;

    public KunosAi(Random rng, bool genome)
    {
        for (int i = 0; i < Genome.Length; i++) Genome[i] = genome ? 0.98f + 0.04f * rng.NextSingle() : 1;
        float mean = Genome.Average();
        for (int i = 0; i < Genome.Length; i++) Genome[i] /= mean;
        DownJitter = 1 + 0.15f * rng.NextSingle() - 0.075f;
    }

    public float GenomeAt(float s, float length) => Genome[Math.Clamp((int)(s / length * Genome.Length), 0, Genome.Length - 1)];
    public bool InMistake(double now) => now < MistakeUntil;
}

/// <summary>
/// The Assetto Corsa singleplayer AI on the vehicle model: where to drive (the line, cars alongside, the side to pass on), how fast
/// (look-ahead braking with the Kunos corner speed), and the driver itself (steering, pedals, gears, the AI's help in the physics).
/// </summary>
public sealed partial class RaceWorld
{
    /// <summary>Physics step (s): AC runs at 333 Hz; the tyre forces need small steps.</summary>
    private const float PhysicsStep = 1f / 300f;
    /// <summary>The AI decides every 5th physics step (AC: index % 5).</summary>
    private const int AiEvery = 5;
    /// <summary>Corner speed penalty per missing push (AC: CLASSIC_PUSH_PENALTY, 80 km/h).</summary>
    private const float PushPenalty = 80 / 3.6f;

    // ------------------------------------------------------------------ push: level, genome, variation, cars alongside

    private KunosAi AiOf(RaceBot me) => me.Ai ??= new KunosAi(_rng, Settings.PushVariation);

    /// <summary>
    /// The AI's push here (getLateralPush): its level, drifting ±0.04 over 30-600 s (stepPushVariations), × the genome of this part of the
    /// lap, × 0.9 with cars on both sides / (1 − 0.1·(n−1)) with n side by side.
    /// </summary>
    private float Push(RaceBot me, float s, int sideBySide, bool bothSides)
    {
        var ai = AiOf(me);
        float level = me.Driver.Pace;
        if (Settings.PushVariation)
        {
            if (double.IsNaN(ai.PushFromAt) || _now >= ai.PushToAt)
            {
                ai.PushFrom = double.IsNaN(ai.PushFromAt) ? level : ai.PushTo;
                ai.PushFromAt = _now;
                ai.PushToAt = _now + 30 + 570 * _rng.NextDouble();
                ai.PushTo = level + 0.04f * (2 * _rng.NextSingle() - 1);
            }
            float x = (float)Math.Clamp((_now - ai.PushFromAt) / Math.Max(1, ai.PushToAt - ai.PushFromAt), 0, 1);
            level = ai.PushFrom + (ai.PushTo - ai.PushFrom) * x;
        }
        float push = level * ai.GenomeAt(s, Line.Length);
        if (bothSides) push *= 0.9f;
        else if (sideBySide > 1) push *= 1 - 0.1f * (sideBySide - 1);
        if (me.Phase == BotPhase.CoolDown) push *= Settings.CoolDownPace;
        return push;
    }

    // ------------------------------------------------------------------ corner speed and look-ahead braking (getLABraking)

    private float[]? _bank;

    /// <summary>
    /// Banking at each line point relative to the corner (+ = the road rises to the outside, AC: AISpline::calculateNormals). fast_lane.ai
    /// stores camber × direction; the sign that makes banked corners positive (nearly all of them on real tracks) is found once.
    /// </summary>
    private float BankAt(int i)
    {
        if (_bank == null)
        {
            var b = new float[Line.Count];
            float vote = 0;
            for (int k = 0; k < Line.Count; k++)
                if (MathF.Abs(Line.Curvature[k]) > 1 / 120f) vote += Line.Camber[k] * MathF.Sign(Line.Curvature[k]);
            float sign = vote >= 0 ? 1 : -1;
            for (int k = 0; k < Line.Count; k++) b[k] = Math.Clamp(sign * Line.Camber[k] * MathF.Sign(Line.Curvature[k]), -0.35f, 0.35f);
            _bank = b;
        }
        return _bank[i];
    }

    /// <summary>
    /// The Kunos corner speed at a line point for a car at <paramref name="offset"/>: v = sqrt(r·g·(μ + tan θ)/(1 − μ·tan θ)) with the
    /// radius at that offset × HINT_, μ the tyres' plain grip with load transfer and downforce (Envelope.PlanLateral via the car's
    /// planning numbers), θ the banking; then MAXSPEED_.
    /// </summary>
    private float CornerSpeed(RaceBot me, int i, float offset, float grip)
    {
        float k = Line.Curvature[i];
        if (MathF.Abs(k) > 1e-5f)
        {
            float r = 1f / MathF.Abs(k) - offset * MathF.Sign(k);
            k = MathF.Sign(k) / MathF.Max(r, 4f);
        }
        if (Settings.UseTrackHints) k /= MathF.Max(0.2f, Line.HintFactor[i]);
        float v = me.Car.CornerLimit(k, Line.VerticalCurvature[i], grip, me.MassRatio);
        float tanT = MathF.Tan(BankAt(i));
        float mu = me.Car.LateralGrip * grip;
        if (MathF.Abs(tanT) > 1e-3f && MathF.Abs(k) > 1e-4f)
        {
            float bank = (mu + tanT) / MathF.Max(0.2f, 1 - mu * tanT) / MathF.Max(0.1f, mu);
            v *= MathF.Sqrt(Math.Clamp(bank, 0.5f, 2f));
        }
        if (Settings.UseTrackHints) v = MathF.Min(v, Line.MaxSpeed[i]);
        return MathF.Min(v, me.Car.TopSpeed);
    }

    /// <summary>
    /// The speed the AI may have now (getLABraking): 300 m ahead in 4 m steps, every point's corner speed minus (1 − push) × 80 km/h
    /// (at least half the corner speed), braked to with push × the car's braking plus the drag; slower cars in its way are targets too,
    /// their speed 7.5 m behind them. Remembers the binding corner for the brake help.
    /// </summary>
    internal float PlanSpeed(RaceBot me, float offset, float push = float.NaN)
    {
        var car = me.Car;
        float s0 = Line.WrapS((float)me.Distance);
        if (float.IsNaN(push)) push = Push(me, s0, 0, false);
        float grip = Settings.GripFactor * me.CarGrip;
        float v = MathF.Max(me.Speed, 5);
        float best = car.TopSpeed / MathF.Sqrt(DamageDrag(me));
        if (me.Phase == BotPhase.CoolDown) best = MathF.Min(best, 120 / 3.6f);
        me.LimitDist = float.PositiveInfinity;
        for (float d = 0; d <= 300; d += 4)
        {
            int i = Line.IndexAt(s0 + d);
            float vc = CornerSpeed(me, i, offset, grip);
            if (vc >= best) continue;
            float vt = MathF.Max(vc - (1 - push) * PushPenalty, 0.5f * vc);
            float decel = push * car.BrakeAt((vt + v) * 0.5f, grip, me.MassRatio) + car.DragCoefficient * v * v;
            // downhill the braking zone is longer, uphill shorter (AC: grade·g in the braking)
            decel = MathF.Max(1.5f, decel + CarSpec.G * Line.Forward[i].Y);
            float allowed = MathF.Sqrt(vt * vt + 2 * decel * d);
            if (allowed < best)
            {
                best = allowed;
                me.LimitDist = d;
                me.LimitSpeed = vt;
            }
        }
        return best;
    }

    // ------------------------------------------------------------------ other cars (stepCarsAwareness2, getEngagedDesiredOffset)

    /// <summary>
    /// Where the AI drives with other cars around, and how fast because of them:
    /// <list type="bullet">
    /// <item>alongside (sideways under 8 m, along under 15 m, more than 25° to the side): keeps (1 + 0.03·closing) × 3 half widths
    /// between the centres, in the middle with cars on both sides;</item>
    /// <item>a car ahead within (v·0.07 + 8)·(1 + 0.05·n) m: takes the side with more room beside it (its offset ± 3.75 half widths) when the
    /// room is there, the move takes less time than the gap closes, and it catches up before the next narrow part on that side (or that is
    /// over 200 m away, or the aggression allows it); otherwise follows it at its speed 7.5 m behind.</item>
    /// </list>
    /// </summary>
    private (float Offset, float SpeedLimit, int SideBySide, bool BothSides) Awareness(RaceBot me, KunosAi ai, float myS, float lineOffset)
    {
        float half = me.Car.Width / 2;
        float v = me.Speed;
        float lo = -Line.RoomMinusAt(myS) + half, hi = Line.RoomPlusAt(myS) - half;
        float want = lineOffset;
        float limit = float.MaxValue;
        int n = 0, beside = 0;
        float sideMin = float.MinValue, sideMax = float.MaxValue;
        bool leftSide = false, rightSide = false;
        Neighbor? ahead = null;
        float aheadGap = float.MaxValue;
        foreach (var o in _neighbors)
        {
            // the car it was passing is behind now: done
            if (o.Id == ai.PassingId && (!o.IsBot || o.Bot != me) && Line.Delta(myS, o.S) < -(me.Car.Length + o.Length) / 2)
            {
                me.Overtakes++;
                ai.PassingId = -1;
            }
            if (o.IsBot && o.Id == me.Id) continue;
            if (o.IsBot && o.Bot!.InPitLane) continue;
            float ds = Line.Delta(myS, o.S);
            float dOff = o.Offset - me.Offset;
            if (MathF.Abs(ds) > 60 || MathF.Abs(dOff) > 8) continue;
            n++;
            float longClear = (me.Car.Length + o.Length) / 2;
            bool alongside = MathF.Abs(ds) < 15 && MathF.Atan2(MathF.Abs(dOff), MathF.Abs(ds)) > 25 * MathF.PI / 180;
            if (alongside)
            {
                beside++;
                float closing = MathF.Abs(o.Speed - v);
                float spacing = (1 + 0.03f * closing) * 1.5f * (half + o.Width / 2);
                if (dOff > 0) { sideMax = MathF.Min(sideMax, o.Offset - spacing); rightSide = true; }
                else { sideMin = MathF.Max(sideMin, o.Offset + spacing); leftSide = true; }
                continue;
            }
            float gap = ds - longClear;
            if (ds > 0 && gap < aheadGap) { ahead = o; aheadGap = gap; }
        }

        // a car ahead: pass it on the side with more room, or follow it
        if (ahead is { } a)
        {
            float closing = v - a.Speed;
            float reach = (v * 0.07f + 8) * (1 + 0.05f * n);
            float oHalf = a.Width / 2, units = half + oHalf;
            float room = (1 + 0.2f * n) * 1.25f * units;
            float sPlus = a.Offset + 1.875f * units, sMinus = a.Offset - 1.875f * units;
            float freePlus = Line.RoomPlusAt(a.S) - (a.Offset + oHalf), freeMinus = (a.Offset - oHalf) + Line.RoomMinusAt(a.S);
            bool committed = ai.PassingId == a.Id && aheadGap < reach * 1.5f;
            if (aheadGap < reach && (committed || closing > 0.3f))
            {
                float side = ai.PassingId == a.Id ? MathF.Sign(ai.DesiredOffset - a.Offset) : freePlus >= freeMinus ? 1 : -1;
                if (side == 0) side = 1;
                float target = side > 0 ? sPlus : sMinus;
                float free = side > 0 ? freePlus : freeMinus;
                bool ok = free > room && target >= lo && target <= hi;
                if (ok && !committed)
                {
                    float tti = closing > 0.1f ? (aheadGap - 6) / closing : float.MaxValue;
                    float move = MathF.Abs(target - me.Offset) / (beside > 0 ? 4f : 3f);
                    float catchUp = closing > 0.1f ? 0.8f * v * MathF.Max(0, aheadGap - 3) / closing : float.MaxValue;
                    float pinch = NarrowAhead(me.Offset, myS, side, units);
                    float cornerSpeed = MathF.Max(1, CornerSpeed(me, Line.IndexAt(myS + MathF.Min(pinch, 200)), target, Settings.GripFactor * me.CarGrip));
                    ok = (tti <= 0 || move < tti || tti == float.MaxValue)
                         && (catchUp < pinch || pinch > 200 || v / cornerSpeed < me.Driver.Aggression);
                }
                if (ok)
                {
                    ai.PassingId = a.Id;
                    want = target;
                }
                else ai.PassingId = -1;
            }
            else if (ai.PassingId == a.Id && aheadGap > reach * 1.5f) ai.PassingId = -1;

            // behind it in its lane: its speed 7.5 m behind it
            bool inLane = MathF.Abs(a.Offset - me.Offset) < units + 0.3f && MathF.Abs(a.Offset - want) < units + 0.3f;
            if (inLane)
            {
                float decel = me.Car.BrakeAt(v, Settings.GripFactor * me.CarGrip, me.MassRatio) * 0.8f;
                limit = MathF.Sqrt(a.Speed * a.Speed + 2 * decel * MathF.Max(0, aheadGap - 7.5f));
                if (aheadGap < 7.5f) limit = MathF.Max(0, a.Speed - (7.5f - aheadGap) * 0.5f);
            }
        }
        else ai.PassingId = -1;

        if (leftSide && rightSide) want = (sideMin + sideMax) / 2;
        else want = Math.Clamp(want, sideMin, sideMax);
        return (Math.Clamp(want, lo, hi), limit, beside, leftSide && rightSide);
    }

    /// <summary>Distance (m) to where the road on that side of the car gets narrower than its width (10 m steps, up to 250 m).</summary>
    private float NarrowAhead(float offset, float s, float side, float units)
    {
        for (float d = 10; d <= 250; d += 10)
        {
            float free = side > 0 ? Line.RoomPlusAt(s + d) - offset : offset + Line.RoomMinusAt(s + d);
            if (free < units) return d;
        }
        return float.MaxValue;
    }

    // ------------------------------------------------------------------ driving

    /// <summary>One world step of a bot on the track: the AI decides (every 5th physics step), the vehicle model drives.</summary>
    private void Drive(RaceBot me, float dt)
    {
        var d = me.Car.Physics!;
        var ai = AiOf(me);
        var st = me.Phys;
        float s0 = Line.WrapS((float)me.Distance);
        if (st == null)
        {
            // start of the vehicle model: heading along the direction of travel
            st = me.Phys = new VehicleState
            {
                S = me.Distance, N = me.Offset, Vx = me.Speed,
                Psi = MathF.Atan2(me.LateralSpeed, MathF.Max(me.Speed, 1)),
                R = me.Speed * Line.CurvatureAt(s0),
            };
            st.Heat = HeatOf(me);
            SampleRoad(me, st);
            ai.Steer = 0;
            ai.PidIntegral = ai.PidPrev = 0;
            ai.DesiredOffset = me.Offset;
        }
        else
        {
            // something outside the model moved the car (contact, overlap, a placement): follow it
            if (MathF.Abs(st.WrittenOffset - me.Offset) > 1e-3f) { st.N = me.Offset; st.Vy *= 0.5f; }
            if (MathF.Abs(st.WrittenSpeed - me.Speed) > 1e-3f) st.Vx = me.Speed;
            if (MathF.Abs(st.WrittenLateral - me.LateralSpeed) > 1e-3f) st.Vy += me.LateralSpeed - st.WrittenLateral;
            st.S = me.Distance;
        }

        // ---- humanizer: every 20-120 s a mistake of up to 2 s (doubled brake pressure, no ABS)
        if (Settings.HumanErrors && me.Phase == BotPhase.Racing)
        {
            if (double.IsNaN(ai.NextMistakeAt)) ai.NextMistakeAt = _now + 20 + 100 * _rng.NextDouble();
            if (_now >= ai.NextMistakeAt)
            {
                ai.MistakeUntil = _now + 2 * _rng.NextDouble();
                ai.NextMistakeAt = _now + 20 + 100 * _rng.NextDouble();
            }
        }
        bool mistake = ai.InMistake(_now);

        // ---- where and how fast (the plan, every world step)
        var (want, carLimit, beside, both) = Awareness(me, ai, s0, 0);
        float push = Push(me, s0, beside + 1, both);
        float target = MathF.Min(PlanSpeed(me, me.Offset, push), carLimit);
        // wide of the line or beside another car: the AI moves with at most 3-4 m/s, back to its line slowly
        float rate = (ai.PassingId >= 0 || beside > 0 || MathF.Abs(want) > MathF.Abs(ai.DesiredOffset) + 0.05f) ? (beside > 0 ? 4f : 3f) : 0.6f;
        ai.DesiredOffset += Math.Clamp(want - ai.DesiredOffset, -rate * dt, rate * dt);
        me.TargetOffset = ai.DesiredOffset;
        me.Alongside = beside > 0;
        if (!double.IsNaN(me.LaunchAt) && _now < me.LaunchAt && me.Speed < 1 && me.LapsCompleted == 0) target = 0;
        // off the road: slow, and back on when nobody comes (AC stepGasBrake: ≤ 20 + 10·cos heading m/s)
        float half = me.Car.Width / 2;
        bool off = me.Offset > Line.RoomPlusAt(s0) + half || me.Offset < -Line.RoomMinusAt(s0) - half;
        if (off) target = MathF.Min(target, 20 + 10 * MathF.Cos(st.Psi));
        me.TargetSpeed = target;
        float desired = ai.DesiredOffset;

        float phys = Settings.GripFactor * me.CarGrip;
        float m = d.Mass * me.MassRatio;
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / PhysicsStep));
        float h = dt / steps;
        var inp = new VehicleInputs
        {
            MassRatio = me.MassRatio, Grip = phys, NoFalloff = true, AmbientTemp = Settings.AmbientTemp, RoadTemp = Settings.RoadTemp
        };
        for (int k = 0; k < steps; k++)
        {
            float s = Line.WrapS((float)st.S);
            if (ai.Sub++ % AiEvery == 0)
            {
                Gears(me, d, st, ai);
                Decide(me, d, st, ai, AiEvery * h, s, target, desired, mistake);
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
            inp.AiAbs = !mistake;

            // the AC AI's help every physics step (stepSuperhuman, "Magic"): a spring to its line and extra braking before its brake point
            float v = st.Vx;
            float err = desired - st.N;
            inp.ExtraFy = 0;
            if (v * 3.6f > 20 && MathF.Abs(err) < (me.Alongside ? 6 : 4) && _now - Math.Max(me.LastContactAt, me.LastOverlapAt) > 1)
                inp.ExtraFy = m * Math.Clamp(err, -2, 2) * (me.Alongside ? 2 : 1);
            // a car sliding more than 2-5° (body against travel) is turned back to the yaw rate of its path (our tyre model lets go under
            // trail braking where AC's holds; the SP AI never slides like that)
            inp.ExtraMz = 0;
            float beta = MathF.Abs(MathF.Atan2(st.Vy, MathF.Max(v, 1)));
            if (v * 3.6f > 20 && !mistake && beta > 2 / 57.3f && _now - me.LastContactAt > 1)
            {
                float rRef = v * Line.CurvatureAt(s) / MathF.Max(0.2f, 1 - Line.CurvatureAt(s) * st.N);
                float blend = Math.Clamp((beta - 2 / 57.3f) / (3 / 57.3f), 0, 1);
                inp.ExtraMz = -d.YawInertia * me.MassRatio * Math.Clamp(5 * (st.R - rRef), -4, 4) * blend;
            }
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
        // beyond the run-off the car is held (a wall, the end of the gravel)
        float allow = Settings.GrassAllowance;
        float held = Math.Clamp(me.Offset, -Line.RoomMinusAt(sNow) + half - allow, Line.RoomPlusAt(sNow) - half + allow);
        if (MathF.Abs(held - me.Offset) > 1e-3f)
        {
            me.Offset = st.N = held;
            st.Vy *= 0.3f;
            st.Psi *= 0.7f;
            me.LateralSpeed = 0;
        }
        if (TraceBotId == me.Id && Trace != null && (int)(_now * 4) != (int)((_now - dt) * 4))
            Trace(FormattableString.Invariant($"t={_now:F2} s={sNow:F0} v={st.Vx * 3.6f:F0} tgt={target * 3.6f:F0} car={(carLimit < 999 ? carLimit * 3.6f : 0):F0} push={push:F3} off={st.N:F2}/{desired:F2} pass={ai.PassingId} beside={beside} thr={me.Throttle:F2} brk={me.Brake:F2} g={me.Gear} rpm={st.EngineRpm:F0} steer={st.Steer * 57.3f:F1} psi={st.Psi * 57.3f:F1} slipF={st.FrontSlip:F2} slipR={st.RearSlip:F2} lim={me.LimitDist:F0}/{me.LimitSpeed * 3.6f:F0} mistake={mistake}"));
        st.WrittenOffset = me.Offset;
        st.WrittenSpeed = me.Speed;
        st.WrittenLateral = me.LateralSpeed;
    }

    /// <summary>
    /// AIDriver::stepGears: up above the UP revs at full throttle (or in first) unless the driven wheels spin; down when the revs in the
    /// gear below would still be under UP × DOWN (±7.5 % per driver) and the driver isn't on full throttle; at least 1 s between changes.
    /// </summary>
    private void Gears(RaceBot me, VehicleData d, VehicleState st, KunosAi ai)
    {
        if (me.Gear <= 0 || me.Gear > d.Ratios.Length)
        {
            float r = Envelope.RatioFor(d, MathF.Max(st.Vx, 1)) / d.Final;
            me.Gear = Math.Max(1, Array.IndexOf(d.Ratios, r) + 1);
            return;
        }
        if (me.ShiftTo > 0 || _now - ai.LastShift < 1) return;
        float up = MathF.Min(d.AiUpRpm, d.Limiter);
        float down = up * d.AiDownFactor * ai.DownJitter;
        float drivenSlip = d.Drive == 1 ? MathF.Max(st.Kappa[0], st.Kappa[1]) : MathF.Max(st.Kappa[2], st.Kappa[3]);
        // (on what the AI wants: below level 0.9 the throttle itself is capped, AC shifts all the same)
        if (me.Gear < d.Ratios.Length && st.EngineRpm > up && (st.Vx < 1 || drivenSlip < 1) && (ai.ThrottleWanted > 0.99f || me.Gear == 1))
        {
            Shift(me, me.Gear + 1, me.Car.ShiftUpTime, ai, false);
            return;
        }
        if (me.Gear >= 2 && ai.ThrottleWanted <= 0.98f)
        {
            float rpmBelow = st.EngineRpm * d.Ratios[me.Gear - 2] / d.Ratios[me.Gear - 1];
            if (rpmBelow > 0 && rpmBelow < down && rpmBelow < d.Limiter * 0.97f) Shift(me, me.Gear - 1, me.Car.ShiftDownTime, ai, true);
        }
    }

    private void Shift(RaceBot me, int to, float time, KunosAi ai, bool down)
    {
        me.ShiftTo = to;
        me.ShiftEnd = _now + time;
        ai.LastShift = _now;
        ai.ShiftClock = 0;
        ai.Downshift = down;
    }

    /// <summary>The AI's decision (every 5th physics step): steering (AIDriver::stepSteer), throttle and brake (AIDriver::stepGasBrake).</summary>
    private void Decide(RaceBot me, VehicleData d, VehicleState st, KunosAi ai, float dtAi, float s0, float target, float wantOffset, bool mistake)
    {
        float v = st.Vx;

        // ---- steering: aim at a point ahead on the wanted line; PID on 7·asin of its direction (STEER_GAIN·0.27, 0.02, 0.02)
        float lookahead = 3f + Math.Clamp(v * 0.018f, 0, 1) * d.AiLookahead;
        float th = 0, px = 0, py = 0;
        int steps = Math.Max(1, (int)lookahead);
        float ds = lookahead / steps;
        for (int i = 0; i < steps; i++)
        {
            th += Line.CurvatureAt(s0 + (i + 0.5f) * ds) * ds;
            px += MathF.Cos(th) * ds;
            py += MathF.Sin(th) * ds;
        }
        float dx = px - wantOffset * MathF.Sin(th), dy = py + wantOffset * MathF.Cos(th) - st.N;
        float lat = MathF.Sin(MathF.Atan2(dy, dx) - st.Psi);
        float demand = MathF.Asin(Math.Clamp(lat, -1, 1)) * 7;
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

        // ---- throttle target: wide of the line (0.055 per m), understeer, never more than 15 m/s above the speed now
        float vt = target;
        float kHere = Line.CurvatureAt(s0);
        float wide = (wantOffset - st.N) * MathF.Sign(kHere);
        if (wide > 0 && MathF.Abs(kHere) > 1 / 1000f && v > 5)
            vt += (v / (wide * 0.055f + 1) - vt) * Math.Clamp(MathF.Abs(demand * 0.6666667f), 0, 1);
        float ndF = MathF.Max(st.NdSlip[0], st.NdSlip[1]), ndR = MathF.Max(st.NdSlip[2], st.NdSlip[3]);
        float under = ndF - ndR;
        if (v * 3.6f > 30 && MathF.Max(ndF, ndR) * d.AiUndersteer > 1 && under > 0 && vt > v)
            vt = Math.Clamp(1 - Math.Clamp(under, 0, 1) * 0.2f * d.AiUndersteer, 0, 1) * v;
        vt = MathF.Min(vt, v + 15);

        // ---- brake: (v − target) × 0.5, up to the pedal where an axle locks (×0.99; ×2 in a humanizer mistake)
        float brakeNorm = target < v - 0.3f ? Math.Clamp((v - target) * 0.5f, 0, 1) : 0;
        float brake = MathF.Min(1, brakeNorm * st.BrakeLockLimit * 0.99f * (mistake ? 2 : 1));

        // ---- throttle: P on the speed error over the car's drive/traction ratio, plus what holds the speed against the drag
        float full = MathF.Max(1, me.Car.AccelAt(v, 1) + me.Car.DragCoefficient * v * v);
        float hold = Math.Clamp(me.Car.DragCoefficient * v * v / full, 0, 1);
        float throttle = Math.Clamp(0.6f * (vt - v) / MathF.Max(0.05f, st.DriveRatio) + hold, 0, 1);
        if (brakeNorm > 0) throttle = Math.Clamp(throttle - brakeNorm * 4, 0, 1);
        ai.ThrottleWanted = throttle;
        // the level caps the throttle below 0.9 (1 − (0.9 − level) × 2)
        float level = me.Driver.Pace;
        if (level < 0.9f) throttle = MathF.Min(throttle, Math.Clamp(1 - (0.9f - level) * 2, 0.2f, 1));
        if (me.Phase == BotPhase.CoolDown) throttle = MathF.Min(throttle, 0.5f);
        bool limiter = st.EngineRpm >= d.Limiter * 0.99f;
        if (limiter) throttle = 0.5f;
        // AI traction control (stepSuperhuman): driven tyres past their peak → 20 % of the throttle
        float drivenNd = d.Drive switch { 1 => ndF, 0 => ndR, _ => MathF.Max(ndF, ndR * (1 / 1.2f)) };
        // pulling away from the grid: until 50 km/h much more slip allowed below 20 km/h, and only down to half the throttle
        bool start = me.LapsCompleted == 0 && !double.IsNaN(me.LaunchAt) && _now - me.LaunchAt < 20 && v * 3.6f < 50 && ai.StartPhase;
        if (v * 3.6f >= 50) ai.StartPhase = false;
        float tcLimit = start ? (v * 3.6f < 20 ? (20 - v * 3.6f) * 10 + 4 : 4) : 1;
        // a driver who spins the wheels off the line floors it and doesn't feel for the grip (the car's own TC still works)
        if (_now < me.LaunchSpinUntil) throttle = 1;
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
            float? h0 = Road?.HeightAt(p0.X, p0.Z, p0.Y);
            float baseY = p.Y + (h0 ?? p0.Y) - p0.Y; // the line's road plane under the wheel
            if (Road != null && h0 != null && Road.HeightAt(p.X, p.Z, baseY) is { } hw)
            {
                var rs = Road.SurfaceAt(p.X, p.Z, baseY);
                bump = hw - baseY;
                // further off the line's road plane than a kerb (25 cm) or, on plain road, 10 cm is a mesh artefact (another surface, a seam)
                bool kerb = rs?.Key.Contains("KERB", StringComparison.OrdinalIgnoreCase) == true;
                if (MathF.Abs(bump) > (kerb ? 0.25f : 0.1f)) bump = 0;
                if (rs != null) grip = rs.Value.Friction;
            }
            else if (OffTrack != null && OffTrack.HeightAt(p.X, p.Z, baseY) is { } ho)
            {
                // beside the road: the grass/gravel's own height (ditches, banks) up to 30 cm, its grip
                bump = ho - baseY;
                if (MathF.Abs(bump) > 0.3f) bump = 0;
                if (OffTrack.SurfaceAt(p.X, p.Z, baseY) is { } surf) grip = surf.Friction;
            }
            st.Road[i] = bump;
            st.RoadGrip[i] = grip;
        }
    }

    /// <summary>What the AI is doing right now, in a few words (debug telemetry).</summary>
    internal string AiState(RaceBot b)
    {
        if (b.Phase != BotPhase.Racing) return b.Phase.ToString();
        if (b.InPitLane) return $"pit {b.Pit} {b.PitReason}";
        var ai = b.Ai;
        var parts = new List<string>();
        if (ai != null && ai.InMistake(_now)) parts.Add("mistake");
        if (ai is { PassingId: >= 0 }) parts.Add($"passing #{ai.PassingId}");
        if (b.Alongside) parts.Add("alongside");
        if (b.Pit == PitPhase.Requested) parts.Add($"pit requested {b.PitReason}");
        if (b.GhostUntil > _now) parts.Add("ghost");
        return parts.Count == 0 ? "drive" : string.Join(", ", parts);
    }

    /// <summary>A new session: the humanizer's timers start again (AIDriver::onNewSession).</summary>
    public void NewSession(RaceBot bot)
    {
        if (bot.Ai is { } ai)
        {
            ai.NextMistakeAt = double.NaN;
            ai.MistakeUntil = double.NegativeInfinity;
            ai.PassingId = -1;
            ai.StartPhase = true;
        }
        ChoosePressures(bot, learn: true);
        bot.TyreStopGrip = 0.95f + 0.02f * _rng.NextSingle();
    }
}
