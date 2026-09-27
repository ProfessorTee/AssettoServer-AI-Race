using System.Numerics;

namespace RaceAiPlugin.Core;

/// <summary>
/// Human behaviour: mistakes (late / early braking, too much throttle on the exit, spins, wheels on the grass),
/// light contacts between bots and damage that is repaired in the pits.
/// </summary>
public sealed partial class RaceWorld
{
    private float ErrorLevel(RaceBot me)
    {
        float e = Settings.HumanErrors ? Math.Clamp(me.Driver.Errors, 0, 1) : 0;
        // a wet track makes everybody less precise, on slicks a lot
        if (Settings.RainCaution) e += WetFactor() * (me.OnWets ? 0.1f : 0.45f);
        return Math.Clamp(e, 0, 1);
    }

    /// <summary>0 = dry, 1 = soaking wet with standing water.</summary>
    private float WetFactor() => Math.Clamp(Settings.Wetness * 0.6f + Settings.Water * 1.2f, 0, 1);

    /// <summary>Grip of the bot's tyres on the current track (1 = dry slicks).</summary>
    public float RainGrip(RaceBot bot)
    {
        float w = Math.Clamp(Settings.Wetness, 0, 1), water = Math.Clamp(Settings.Water, 0, 1);
        float g;
        if (bot.OnWets)
        {
            // rain tyres: little loss in the wet, slower (and overheating) in the dry
            float dry = 1 - 0.07f * (1 - Math.Clamp(w * 1.5f, 0, 1));
            if (Settings.ServerRainReduction > 0)
            {
                float loss = Settings.ServerRainReduction * (0.3f * w + 0.7f * water);
                g = dry * (1 - 0.3f * loss) / MathF.Max(0.5f, 1 - loss);
            }
            else g = dry * (1 - Settings.RainGripLoss * (0.04f * w + 0.08f * water));
        }
        else
        {
            // slicks: the server already lowers the track grip for everybody when RainTrackGripReductionPercent is set
            g = Settings.ServerRainReduction > 0 ? 1 : 1 - Settings.RainGripLoss * (0.15f * w + 0.25f * water);
        }
        return Math.Clamp(g, 0.4f, 1.05f);
    }

    /// <summary>Should this bot be on rain tyres now (with some hysteresis)?</summary>
    public bool WantsWets(RaceBot bot)
    {
        if (!Settings.WetTyres || !(bot.Car.HasWetTyres || Settings.VirtualWetTyres)) return false;
        float w = Settings.Wetness, water = Settings.Water;
        if (bot.OnWets) return !(w < 0.2f && water < 0.03f && Settings.RainIntensity < 0.05f);
        return (w > 0.45f || water > 0.15f) && (Settings.RainIntensity > 0.05f || water > 0.25f);
    }

    /// <summary>Aquaplaning on standing water at speed (called every Think).</summary>
    private void Aquaplaning(RaceBot me)
    {
        if (!Settings.RainCaution || Settings.Water < 0.2f || me.Speed < 40 || !MistakesAllowed(me)) return;
        float perSecond = (Settings.Water - 0.2f) * 0.35f * (me.OnWets ? 0.2f : 1f) * (me.Speed / 70f);
        if (_rng.NextSingle() < perSecond * _stepDt)
        {
            StartMistake(me, MistakeKind.Slide, _rng.NextSingle() < 0.5f ? -1 : 1, 0.2f + 0.4f * _rng.NextSingle(), 1.0f);
            me.MistakeTargetOffset = me.Offset + (_rng.NextSingle() - 0.5f) * 1.2f;
        }
    }

    private bool MistakesAllowed(RaceBot me)
        => me.Phase == BotPhase.Racing && !me.InPitLane && me.Mistake == MistakeKind.None
           && _now - me.LapStartTime > 0.5 && !(me.LapsCompleted == 0 && _now - Settings.RaceStartTime < 12)
           && _now >= me.YellowUntil && _now >= me.BlueFlagUntil;

    private float CornerLoad(RaceBot me, out float kEff)
    {
        float s = Line.WrapS((float)me.Distance);
        float k = Line.CurvatureAt(s);
        kEff = k;
        if (MathF.Abs(k) > 1e-5f)
        {
            float r = 1f / MathF.Abs(k) - me.Offset * MathF.Sign(k);
            kEff = MathF.Sign(k) / MathF.Max(r, 4f);
        }
        return me.Speed * me.Speed * MathF.Abs(kEff) / (me.Car.LateralGrip * CarSpec.G);
    }

    private void StartMistake(RaceBot me, MistakeKind kind, float side, float severity, float duration)
    {
        me.Mistake = kind;
        me.MistakeStart = _now;
        me.MistakeDuration = duration;
        me.MistakeEnd = _now + duration;
        me.MistakeSide = side;
        me.MistakeSeverity = severity;
        me.MistakeCount++;
        me.CornerSeen = false;
        me.LockStart = double.NaN;
        me.OvertakeTargetId = -1;
        if (Settings.GrassMoments)
            me.EdgeAllowance = MathF.Max(me.EdgeAllowance, kind == MistakeKind.Spin ? Settings.GrassAllowance + 0.5f : Settings.GrassAllowance);
    }

    private void EndMistake(RaceBot me)
    {
        me.Mistake = MistakeKind.None;
        me.Overspeed = false;
        me.FrontLock = false;
        me.RearSlip = 1f;
        me.SteerExtraDeg = 0;
        me.Yaw = 0;
        me.ReturnToLineAfter = _now + 0.5;
    }

    /// <summary>Called once when a bot starts braking for a corner.</summary>
    private void RollBrakingMistake(RaceBot me)
    {
        if (!MistakesAllowed(me)) return;
        float e = ErrorLevel(me);
        double r = _rng.NextDouble();
        if (r < 0.07 * e)
        {
            // braked too late: the plan assumes more braking than the car has
            StartMistake(me, MistakeKind.LateBrake, 0, 0.4f + 0.6f * _rng.NextSingle(), 6f);
        }
        else if (r < 0.13 * e)
        {
            // braked too early / too much: slow through the corner
            me.MistakeUntil = _now + 2.5 + _rng.NextDouble() * 2;
            me.MistakeCount++;
        }
        else if (_rng.NextSingle() < (1 - me.Driver.Consistency) * 0.15f)
        {
            me.MistakeUntil = _now + 1.5 + _rng.NextDouble() * 1.5;
        }
    }

    /// <summary>Corner tracking and the rolls on the way out of a corner (called every Think).</summary>
    private void CornerExitMistakes(RaceBot me)
    {
        float load = CornerLoad(me, out float kEff);
        if (load > 0.5f) me.InCorner = true;
        if (me.InCorner && load < 0.25f)
        {
            me.InCorner = false;
            me.CornerExitRolled = false;
        }
        if (!me.InCorner || me.CornerExitRolled || load < 0.4f || me.TargetSpeed < me.Speed + 0.3f) return;
        me.CornerExitRolled = true;
        if (!MistakesAllowed(me)) return;

        float e = ErrorLevel(me);
        float turn = MathF.Sign(kEff);
        double r = _rng.NextDouble();
        float pSlide = 0.08f * e + (Settings.HumanErrors ? (1 - me.Driver.Consistency) * 0.02f : 0);
        float pGrass = Settings.GrassMoments ? 0.05f * e + (1 - me.Driver.Consistency) * 0.04f : 0;
        if (r < pSlide)
        {
            float sev = 0.25f + 0.75f * _rng.NextSingle();
            if (Settings.Spins && sev > 0.6f && _rng.NextSingle() < 0.12f * e * sev * 2)
            {
                StartMistake(me, MistakeKind.Spin, turn, sev, 60f);
                me.SpinPhase = 0;
                me.SpinAngle = turn * MathF.PI * (0.9f + 0.8f * _rng.NextSingle());
                me.SpinCount++;
                return;
            }
            StartMistake(me, MistakeKind.Slide, turn, sev, 1.2f + 0.9f * sev);
            // the rear steps out: the car drifts to the outside a little
            me.MistakeTargetOffset = me.Offset - turn * (0.4f + 1.0f * sev);
        }
        else if (r < pSlide + pGrass)
        {
            float outer = -turn;
            int i = Line.IndexAt(Line.WrapS((float)me.Distance));
            float half = me.Car.Width / 2;
            float edge = outer > 0 ? Line.RoomPlus[i] - half : -Line.RoomMinus[i] + half;
            StartMistake(me, MistakeKind.Grass, outer, 0.3f + 0.7f * _rng.NextSingle(), 1.6f + _rng.NextSingle());
            // two wheels over the edge
            me.MistakeTargetOffset = edge + outer * (0.4f + 0.5f * me.MistakeSeverity + half * 0.3f);
        }
    }

    /// <summary>Overrides of the target speed / lane while a mistake is going on (end of Think).</summary>
    private void MistakeThink(RaceBot me, ref float vTarget)
    {
        if (me.Mistake == MistakeKind.None) return;
        float t = (float)(_now - me.MistakeStart);
        switch (me.Mistake)
        {
            case MistakeKind.LateBrake:
            {
                float load = CornerLoad(me, out _);
                if (load > 0.5f) me.CornerSeen = true;
                if ((me.CornerSeen && load < 0.2f && !me.Overspeed) || _now > me.MistakeEnd) EndMistake(me);
                else if (me.Overspeed) me.TargetOffset = me.Offset; // doesn't fight it, the car runs wide
                break;
            }
            case MistakeKind.Slide:
                if (_now > me.MistakeEnd) { EndMistake(me); break; }
                me.TargetOffset = me.MistakeTargetOffset;
                if (t < me.MistakeDuration * 0.6f) vTarget = MathF.Min(vTarget, me.Speed); // lift (the lost speed is taken in MistakeIntegrate)
                break;
            case MistakeKind.Grass:
                if (_now > me.MistakeEnd) { EndMistake(me); break; }
                if (t < me.MistakeDuration * 0.55f) me.TargetOffset = me.MistakeTargetOffset;
                vTarget = MathF.Min(vTarget, me.Speed + 0.2f);
                break;
            case MistakeKind.Spin:
                vTarget = 0;
                break;
        }
    }

    /// <summary>Dynamics of a mistake after the speed update (Integrate). Returns true when the spin took over the whole step.</summary>
    private bool MistakeIntegrate(RaceBot me, float dt, float pace)
    {
        // allowance to leave the tarmac decays when there is no mistake
        if (me.Mistake == MistakeKind.None || !Settings.GrassMoments)
            me.EdgeAllowance = MathF.Max(0, me.EdgeAllowance - 0.35f * dt);

        if (me.Mistake == MistakeKind.Spin)
        {
            SpinIntegrate(me, dt);
            return true;
        }

        float t = (float)(_now - me.MistakeStart);
        me.Overspeed = false;
        me.FrontLock = false;
        me.RearSlip = 1f;
        me.SteerExtraDeg = 0;
        me.Yaw = 0;

        if (me.Mistake is MistakeKind.LateBrake or MistakeKind.Slide or MistakeKind.Grass)
        {
            // too fast for the corner: the car can't turn tightly enough and drifts to the outside
            CornerLoad(me, out float kEff);
            if (MathF.Abs(kEff) > 1f / 2000f)
            {
                int i = Line.IndexAt(Line.WrapS((float)me.Distance));
                float vl = me.Car.CornerLimit(kEff, Line.VerticalCurvature[i], pace);
                float aReq = me.Speed * me.Speed * MathF.Abs(kEff), aMax = vl * vl * MathF.Abs(kEff);
                if (aReq > aMax * 1.03f)
                {
                    float excess = MathF.Min(6f, aReq - aMax);
                    me.Overspeed = true;
                    me.LateralSpeed -= MathF.Sign(kEff) * excess * dt;
                    me.Speed = MathF.Max(0, me.Speed - MathF.Min(3f, excess * 0.3f) * dt);
                    me.Yaw = MathF.Sign(kEff) * -0.03f * MathF.Min(1, excess / 3); // understeer: nose points a bit outwards
                }
            }
        }

        switch (me.Mistake)
        {
            case MistakeKind.LateBrake:
                if (me.Accel < -6 && double.IsNaN(me.LockStart)) me.LockStart = _now;
                me.FrontLock = !double.IsNaN(me.LockStart) && _now - me.LockStart < 0.5 + 0.4 * me.MistakeSeverity && me.Speed > 15;
                break;
            case MistakeKind.Slide:
            {
                float T = me.MistakeDuration;
                float a = 0.12f + 0.25f * me.MistakeSeverity;
                // rear steps out, countersteer catches it with a little fishtail
                me.Yaw = me.MistakeSide * a * MathF.Sin(2 * MathF.PI * t / T) * MathF.Exp(-1.5f * t / T);
                me.SteerExtraDeg = -me.Yaw * 180 / MathF.PI * 0.9f;
                me.RearSlip = t < T * 0.5f ? 1.35f : 1f;
                if (t < T * 0.6f) me.Speed = MathF.Max(0, me.Speed - (1.0f + 1.5f * me.MistakeSeverity) * dt);
                break;
            }
            case MistakeKind.Grass:
                me.Yaw = me.MistakeSide * 0.025f * MathF.Sin(t * 11);
                break;
        }

        GrassDrag(me, dt);
        return false;
    }

    private void GrassDrag(RaceBot me, float dt)
    {
        int i = Line.IndexAt(Line.WrapS((float)me.Distance));
        float half = me.Car.Width / 2;
        float beyond = MathF.Max(me.Offset - (Line.RoomPlus[i] - half), -Line.RoomMinus[i] + half - me.Offset);
        if (beyond > 0.05f)
            me.Speed = MathF.Max(0, me.Speed - (1.2f + 2.5f * MathF.Min(1, beyond)) * dt);
    }

    private void SpinIntegrate(RaceBot me, float dt)
    {
        float t = (float)(_now - me.MistakeStart);
        float outer = -me.MistakeSide;
        me.FrontLock = false;
        me.RearSlip = 1f;
        switch (me.SpinPhase)
        {
            case 0: // sliding and rotating
            {
                const float T = 2.0f;
                float x = MathF.Min(1, t / T);
                me.Yaw = me.SpinAngle * (1 - (1 - x) * (1 - x));
                me.SteerExtraDeg = -me.MistakeSide * 25;
                me.Speed = MathF.Max(0, me.Speed - 8.5f * dt);
                me.LateralSpeed = outer * MathF.Min(4f, me.Speed * 0.18f + 0.5f * (1 - x));
                me.Accel = -8.5f;
                if (me.Speed <= 0.3f && x >= 1)
                {
                    me.Speed = 0;
                    me.LateralSpeed = 0;
                    me.SpinPhase = 1;
                    me.SpinStandUntil = _now + 2.5 + _rng.NextDouble() * 2;
                    me.SpinClearSince = double.NaN;
                    if (Settings.Damage) AddDamage(me, 4, 5 + 10 * me.MistakeSeverity, suspension: 0.02f * me.MistakeSeverity);
                }
                break;
            }
            case 1: // standing, hazards on, waiting for a gap
                me.Speed = 0;
                me.Accel = 0;
                me.SteerExtraDeg = 0;
                if (_now >= me.SpinStandUntil)
                {
                    if (TrafficClearBehind(me)) { if (double.IsNaN(me.SpinClearSince)) me.SpinClearSince = _now; }
                    else me.SpinClearSince = double.NaN;
                    if ((!double.IsNaN(me.SpinClearSince) && _now - me.SpinClearSince > 1) || _now - me.SpinStandUntil > 25)
                    {
                        me.SpinPhase = 2;
                        me.MistakeStart = _now;
                    }
                }
                break;
            case 2: // turning round and rolling back onto the track
            {
                float rate = MathF.PI / 2.8f;
                float step = MathF.Min(MathF.Abs(me.Yaw), rate * dt);
                me.Yaw -= MathF.Sign(me.Yaw) * step;
                me.SteerExtraDeg = MathF.Sign(me.Yaw) * 28;
                me.Speed = MathF.Min(MathF.Abs(me.Yaw) > 0.3f ? 2.5f : 8f, me.Speed + 2f * dt);
                me.Accel = 1.5f;
                int i = Line.IndexAt(Line.WrapS((float)me.Distance));
                float half = me.Car.Width / 2;
                float inside = Math.Clamp(me.Offset, -Line.RoomMinus[i] + half + 0.5f, Line.RoomPlus[i] - half - 0.5f);
                me.LateralSpeed = Math.Clamp((inside - me.Offset) * 0.8f, -1.2f, 1.2f);
                if (MathF.Abs(me.Yaw) < 0.02f && MathF.Abs(inside - me.Offset) < 0.3f)
                {
                    EndMistake(me);
                    me.TargetOffset = me.Offset;
                    me.CautiousUntil = _now + 5;
                    me.StoppedSince = double.NaN;
                }
                break;
            }
        }

        me.Offset += me.LateralSpeed * dt;
        ClampOffsetWithAllowance(me);
        GrassDrag(me, dt);
        float s = Line.WrapS((float)me.Distance);
        float k = Line.Curvature[Line.IndexAt(s)];
        me.Distance += me.Speed * dt / MathF.Max(0.5f, 1 - k * me.Offset);
    }

    private bool TrafficClearBehind(RaceBot me)
    {
        float myS = Line.WrapS((float)me.Distance);
        foreach (var o in _neighbors)
        {
            if (o.IsBot && o.Id == me.Id) continue;
            float ds = Line.Delta(myS, o.S);
            if (ds > 0 || ds < -250) continue;
            if (ds > -12) return false; // somebody right here
            if (o.Speed > 5 && -ds / o.Speed < 7) return false;
        }
        return true;
    }

    private void ClampOffsetWithAllowance(RaceBot me)
    {
        int i = Line.IndexAt(Line.WrapS((float)me.Distance));
        float half = me.Car.Width / 2;
        float allow = Settings.GrassMoments ? me.EdgeAllowance : 0;
        me.Offset = Math.Clamp(me.Offset, -Line.RoomMinus[i] + half - allow, Line.RoomPlus[i] - half + allow);
    }


    /// <summary>High beams only with nobody ahead (players or bots); switch back at once when somebody shows up.</summary>
    private void UpdateClearAhead(RaceBot me, float myS)
    {
        bool clear = !me.InPitLane;
        if (clear)
        {
            foreach (var o in _neighbors)
            {
                if (o.IsBot && o.Id == me.Id) continue;
                if (o.IsBot && o.Bot!.InPitLane) continue;
                float ds = Line.Delta(myS, o.S);
                if (ds > -3 && ds < Settings.HighBeamRange) { clear = false; break; }
            }
        }
        if (!clear) { me.ClearAhead = false; me.ClearAheadSince = double.NaN; return; }
        // switch on a moment after the road got free, like a driver would
        if (double.IsNaN(me.ClearAheadSince)) me.ClearAheadSince = _now;
        me.ClearAhead = _now - me.ClearAheadSince > 1.5;
    }

    // ------------------------------------------------------------------ contacts and damage

    /// <summary>Two bots touched (from ResolveOverlaps). Light bump: speed, a nudge sideways, a wobble, damage.</summary>
    private void BotContact(RaceBot me, RaceBot other, bool sideBySide, float dOff, float ds)
    {
        if (!Settings.BotContacts) return;
        if (me.LastContactWith == other.Id && _now - me.LastContactAt < 1.5) return;
        // only real hits count: the cars have to move into each other (grazing while both drift the same way is nothing)
        float closing = sideBySide ? (me.LateralSpeed - other.LateralSpeed) * MathF.Sign(dOff) : me.Speed - other.Speed;
        if (closing < (sideBySide ? 0.35f : 1.5f)) return;
        me.LastContactAt = other.LastContactAt = _now;
        me.LastContactWith = other.Id;
        other.LastContactWith = me.Id;
        me.ContactCount++;
        other.ContactCount++;

        if (sideBySide)
        {
            float impact = closing * 3.6f + 3;
            float bounce = 0.6f + 0.02f * impact;
            me.LateralSpeed = -MathF.Sign(dOff) * bounce;
            other.LateralSpeed = MathF.Sign(dOff) * bounce;
            me.Speed *= 0.992f;
            other.Speed *= 0.992f;
            bool otherIsRight = (dOff > 0) == Line.RightIsPlus;
            AddDamage(me, otherIsRight ? 3 : 2, impact);
            AddDamage(other, otherIsRight ? 2 : 3, impact);
            Wobble(me, 0.15f);
            Wobble(other, 0.15f);
        }
        else
        {
            // me behind, other in front
            float impact = closing * 3.6f + 2;
            other.Speed = MathF.Min(other.Speed + MathF.Min(1.5f, impact / 20f), MathF.Max(other.Speed, me.Speed));
            AddDamage(me, 0, impact);
            AddDamage(other, 1, impact);
            Wobble(other, Math.Clamp(impact / 40f, 0.15f, 0.5f));
        }
        me.CautiousUntil = _now + 2;
        me.OvertakeTargetId = -1;
    }

    private void Wobble(RaceBot bot, float severity)
    {
        if (bot.Mistake != MistakeKind.None || bot.Phase != BotPhase.Racing) return;
        float side = _rng.NextSingle() < 0.5f ? -1 : 1;
        StartMistake(bot, MistakeKind.Slide, side, severity, 0.9f);
        bot.MistakeCount--; // not the driver's fault
        bot.MistakeTargetOffset = bot.Offset;
    }

    /// <summary>
    /// Adds damage to one AC damage zone (0 front, 1 rear, 2 left, 3 right, 4 centre). <paramref name="impactKmh"/> is the impact speed;
    /// AC's damage values are roughly impact km/h, the car's damage.ini animates the body between MIN_SPEED and FULL_SPEED.
    /// </summary>
    public void AddDamage(RaceBot bot, int zone, float impactKmh, float suspension = 0)
    {
        if (!Settings.Damage || Settings.DamageRate <= 0) return;
        float v = impactKmh * Settings.DamageRate;
        if (v < 4) return;
        bot.DamageZones[zone] = MathF.Min(400, bot.DamageZones[zone] + v * 0.6f);
        if (v > 25) suspension += (v - 25) / 250f;
        bot.Suspension = Math.Clamp(bot.Suspension + suspension * Settings.DamageRate, 0, 1);
        bot.DamageVersion++;
        UpdateGrip(bot);
    }

    /// <summary>Grip lost to damage (1 = undamaged).</summary>
    public static float DamageGrip(RaceBot bot)
    {
        var z = bot.DamageZones;
        float g = 1 - 0.0012f * z[0] - 0.0005f * (z[2] + z[3]) - 0.0003f * z[1] - 0.15f * bot.Suspension;
        return Math.Clamp(g, 0.65f, 1f);
    }

    /// <summary>Extra aero drag from damage (1 = undamaged), like aero.ini ZONE_x_CD.</summary>
    public static float DamageDrag(RaceBot bot)
    {
        var z = bot.DamageZones;
        return 1 + 0.004f * z[0] + 0.004f * (z[2] + z[3]) * 0.5f + 0.002f * z[1];
    }

    /// <summary>Body damage in percent (for the repair time).</summary>
    public static float BodyDamagePercent(RaceBot bot)
    {
        var z = bot.DamageZones;
        return Math.Clamp((z[0] + z[1] + z[2] + z[3] + z[4]) / 3f, 0, 100);
    }

    public static float RepairTime(RaceBot bot)
        => BodyDamagePercent(bot) / 10f * bot.Car.BodyRepairTime + bot.Suspension * 10f * bot.Car.SuspRepairTime;

    public static bool HasDamage(RaceBot bot) => BodyDamagePercent(bot) > 1 || bot.Suspension > 0.01f;

    public void RepairDamage(RaceBot bot)
    {
        Array.Clear(bot.DamageZones);
        bot.Suspension = 0;
        bot.DamageVersion++;
        UpdateGrip(bot);
    }

    // ------------------------------------------------------------------ signal test (night test of lights, indicators, hazards, flash)

    public double SignalTestStart { get; private set; } = double.NegativeInfinity;
    public const double SignalTestLength = 38.4;

    public void StartSignalTest(double now) => SignalTestStart = now;

    /// <summary>0 = off, 1 left indicator, 2 right indicator, 3 hazards, 4 flash, 5 brake lights, 6 high beams.</summary>
    public int SignalTestPhase(double now)
    {
        double t = now - SignalTestStart;
        if (t < 0 || t > SignalTestLength) return 0;
        return 1 + (int)(t / (SignalTestLength / 6)) % 6;
    }

    private static Vector3 Rotate(Vector3 dir, Vector3 lat, float yaw)
    {
        if (MathF.Abs(yaw) < 1e-4f) return dir;
        var latH = lat - Vector3.Dot(lat, dir) * dir;
        if (latH.LengthSquared() < 1e-6f) return dir;
        latH = Vector3.Normalize(latH);
        return Vector3.Normalize(dir * MathF.Cos(yaw) + latH * MathF.Sin(yaw));
    }
}
