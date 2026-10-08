using System.Numerics;

namespace BotDriverPlugin.Core;

/// <summary>
/// Human behaviour: mistakes (late / early braking, too much throttle on the exit, spins, wheels on the grass),
/// light contacts between bots and damage that is repaired in the pits.
/// </summary>
public sealed partial class RaceWorld
{
    private float ErrorLevel(RaceBot me)
    {
        float e = Settings.HumanErrors ? Math.Clamp(me.Driver.Errors, 0, 1) : 0;
        // somebody sitting in the slipstream for a long time: slightly more mistakes
        e += 0.15f * me.Pressure;
        e *= me.Driver.Personality.Mistakes;
        // a wet track makes everybody less precise
        if (Settings.RainCaution) e += WetFactor() * 0.4f;
        // little grip (green or dirty track, cold or overheated tyres): the car moves around, more mistakes
        e += 0.6f * MathF.Max(0, 0.97f - Settings.GripFactor * me.CarGrip);
        // concentration fades towards the end of a long race (cool heads less)
        if (Settings.IsRace && me.RemainingLaps is > 0 and < int.MaxValue)
        {
            int total = me.LapsCompleted + me.RemainingLaps;
            float lapTime = me.LastLapSeconds > 0 ? me.LastLapSeconds : Line.Length / 45f;
            float longRace = Math.Clamp((total * lapTime / 60 - 15) / 45, 0, 1);
            float done = (float)me.LapsCompleted / total;
            e += 0.15f * longRace * done * done * (1 - Math.Clamp(me.Driver.Personality.Composure, 0, 1));
        }
        return Math.Clamp(e, 0, 1);
    }

    /// <summary>0 = dry, 1 = soaking wet with standing water.</summary>
    private float WetFactor() => Math.Clamp(Settings.Wetness * 0.6f + Settings.Water * 1.2f, 0, 1);

    /// <summary>Grip on the current track (1 = dry). Slicks lose a lot in the wet and on standing water.</summary>
    public float RainGrip(RaceBot bot)
    {
        // the server already lowers the track grip for everybody when RainTrackGripReductionPercent is set
        if (Settings.ServerRainReduction > 0) return 1;
        float w = Math.Clamp(Settings.Wetness, 0, 1), water = Math.Clamp(Settings.Water, 0, 1);
        return Math.Clamp(1 - Settings.RainGripLoss * (0.15f * w + 0.25f * water), 0.4f, 1f);
    }

    /// <summary>
    /// Where water stands: in dips (compressions) and in the grooves of the racing line. Real puddle maps are computed by CSP
    /// on the clients, the server doesn't know them, so this is the typical pattern.
    /// </summary>
    private float PuddleRisk(RaceBot me)
    {
        int i = Line.IndexAt(Line.WrapS((float)me.Distance));
        float dip = Math.Clamp(Line.VerticalCurvature[i] * 150f, 0, 1);
        float onLine = MathF.Abs(me.Offset) < 0.8f ? 1.4f : 0.7f;
        return (1 + 2.5f * dip) * onLine;
    }

    /// <summary>
    /// Rain line: in the wet the bots leave the rubbered racing line (slippery, water in the grooves) a little towards the outside
    /// of the next corner, and move aside before dips where water collects.
    /// </summary>
    private float RainLineOffset(RaceBot me, float myS, float minOff, float maxOff)
    {
        if (!Settings.RainCaution) return 0;
        float wf = WetFactor();
        if (wf < 0.2f) return 0;
        float off = -NextCornerSign(myS, 150) * 1.0f * wf;
        for (float d = 10; d < 80; d += 10)
        {
            if (Line.VerticalCurvature[Line.IndexAt(myS + d)] * 150f > 0.5f && Settings.Water > 0.1f)
            {
                var (rm, rp) = Line.MinRoom(myS + d, 20);
                off += (rp >= rm ? 1 : -1) * 1.2f * Math.Clamp(Settings.Water * 3, 0, 1);
                break;
            }
        }
        return Math.Clamp(Math.Clamp(off, -1.5f, 1.5f), minOff, maxOff);
    }

    /// <summary>Aquaplaning on standing water at speed (called every Think).</summary>
    private void Aquaplaning(RaceBot me)
    {
        if (!Settings.RainCaution || Settings.Water < 0.2f || me.Speed < 40 || !MistakesAllowed(me)) return;
        float perSecond = (Settings.Water - 0.2f) * 0.35f * (me.Speed / 70f) * PuddleRisk(me);
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
        if (me.OvertakeTargetId >= 0) Diag("end:mistake");
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
        // diving down the inside: more often too late
        float attacking = me.OvertakeTargetId >= 0 ? 1 + AttackOf(me) : 1;
        float att = AttackOf(me);
        // an aggressive driver forcing a pass gets it wrong a lot more often, however good he is
        if (r < 0.07 * e * (1 + 0.5 * me.Driver.Personality.BrakeBehavior) * attacking + (me.OvertakeTargetId >= 0 ? (0.04 * att + 0.10 * att * att) * (0.35 + 0.65 * Math.Min(1, e * 2)) : 0))
        {
            float corner = NextCornerSign(Line.WrapS((float)me.Distance), 150);
            if (me.OvertakeTargetId >= 0 && corner != 0 && Settings.GrassMoments && _rng.NextSingle() < 0.7f * att)
            {
                // dived in far too late: straight on past the apex and off on the outside of the exit (grass, gravel), positions lost
                float outer = -corner;
                float s = Line.WrapS((float)me.Distance);
                float edge = outer > 0 ? Line.RoomPlusAt(s) - me.Car.Width / 2 : -Line.RoomMinusAt(s) + me.Car.Width / 2;
                StartMistake(me, MistakeKind.Grass, outer, 0.5f + 0.5f * _rng.NextSingle(), 2.2f + 1.5f * _rng.NextSingle());
                float wide = 1.2f + 2.3f * me.MistakeSeverity;
                me.MistakeTargetOffset = edge + outer * wide;
                me.EdgeAllowance = MathF.Max(me.EdgeAllowance, wide + 0.5f);
                Diag("overshoot");
                return;
            }
            // braked too late: the plan assumes more braking than the car has. A car with much of its braking at the rear (and without
            // ABS) gets light at the back instead of locking the fronts: the rear steps out on the way into the corner
            float pRear = Math.Clamp((0.66f - me.Car.BrakeFront) * 4, 0, 0.5f) + (me.Car.HasAbs ? 0 : 0.1f);
            float side = NextCornerSign(Line.WrapS((float)me.Distance), 150);
            if (side != 0 && _rng.NextSingle() < pRear)
            {
                StartMistake(me, MistakeKind.Slide, side, 0.3f + 0.4f * _rng.NextSingle(), 1.4f);
                me.MistakeTargetOffset = me.Offset - side * 0.5f;
            }
            else
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
        Diag("exit rolls");
        if (!MistakesAllowed(me)) { Diag("exit: not allowed"); return; }

        float e = ErrorLevel(me);
        float turn = MathF.Sign(kEff);
        double r = _rng.NextDouble();
        float pSlide = (0.08f * e + (Settings.HumanErrors ? (1 - me.Driver.Consistency) * 0.02f : 0)) * me.Car.ExitSlide;
        float pGrass = Settings.GrassMoments ? 0.05f * e + (1 - me.Driver.Consistency) * 0.04f : 0;
        if (r < pSlide)
        {
            float sev = 0.25f + 0.75f * _rng.NextSingle();
            if (Settings.Spins && sev > 0.6f && _rng.NextSingle() < 0.12f * e * sev * 2)
            {
                StartMistake(me, MistakeKind.Spin, turn, sev, 60f);
                RaiseYellow(me.Id, true, Line.WrapS((float)me.Distance), "spin");
                me.SpinPhase = 0;
                me.SpinAngle = turn * MathF.PI * (0.9f + 0.8f * _rng.NextSingle());
                me.SpinCount++;
                return;
            }
            StartMistake(me, MistakeKind.Slide, turn, sev, 1.2f + 0.9f * sev);
            // the rear steps out: the car drifts to the outside a little
            me.MistakeTargetOffset = me.Offset - turn * (0.4f + 1.0f * sev);
        }
        else if (r < pSlide + pGrass + GreedyExitChance(me))
        {
            if (r >= pSlide + pGrass)
            {
                // too much throttle too early: the car runs wide past the exit kerb, one or two wheels on the grass (costs time)
                float outerG = -turn;
                float sG = Line.WrapS((float)me.Distance);
                float halfG = me.Car.Width / 2;
                float edgeG = outerG > 0 ? Line.RoomPlusAt(sG) - halfG : -Line.RoomMinusAt(sG) + halfG;
                StartMistake(me, MistakeKind.Grass, outerG, 0.2f + 0.6f * _rng.NextSingle(), 1.1f + 0.7f * _rng.NextSingle());
                me.MistakeTargetOffset = edgeG + outerG * (KerbAllowance(me) + 0.3f + 0.7f * me.MistakeSeverity);
                me.GreedyExits++;
                return;
            }
            float outer = -turn;
            int i = Line.IndexAt(Line.WrapS((float)me.Distance));
            float half = me.Car.Width / 2;
            float edge = outer > 0 ? Line.RoomPlus[i] - half : -Line.RoomMinus[i] + half;
            StartMistake(me, MistakeKind.Grass, outer, 0.3f + 0.7f * _rng.NextSingle(), 1.6f + _rng.NextSingle());
            // two wheels over the edge
            me.MistakeTargetOffset = edge + outer * (0.4f + 0.5f * me.MistakeSeverity + half * 0.3f);
        }
    }

    /// <summary>
    /// Chance (per corner exit) that a greedy driver is on the throttle too early and runs wide: independent of the strength (fast
    /// aggressive drivers do it too), more when attacking or impatient. Only where the exit runs out to the edge.
    /// </summary>
    private float GreedyExitChance(RaceBot me)
    {
        if (!Settings.GrassMoments || me.Clone != null) return 0;
        float greed = Math.Clamp(me.Driver.Personality.ExitGreed, 0, 1) * (0.5f + me.Driver.Aggression);
        greed *= 1 + me.Impatience + (me.OvertakeTargetId >= 0 ? 0.8f : 0) + me.Pressure;
        // the exit must lead towards an edge: the driver's line comes close to the outer edge within the next 60 m
        float s = Line.WrapS((float)me.Distance);
        float outer = -MathF.Sign(Line.CurvatureAt(s));
        float half = me.Car.Width / 2;
        float edgeRoom = float.MaxValue;
        for (float d = 10; d <= 60; d += 5)
        {
            float sa = s + d, lo = OwnLineOffset(me, sa);
            edgeRoom = MathF.Min(edgeRoom, outer > 0 ? Line.RoomPlusAt(sa) - half - lo : lo + Line.RoomMinusAt(sa) - half);
        }
        Diag(edgeRoom > 1.2f ? "greedy:no edge" : "greedy:eligible");
        if (edgeRoom > 1.2f) return 0;
        return 0.045f * greed;
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
                float vl = me.Car.CornerLimit(kEff, Line.VerticalCurvature[i], pace, me.MassRatio);
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
                // ABS: only a short chirp before it catches the wheels; without ABS they lock up and smoke
                me.FrontLock = !double.IsNaN(me.LockStart) && _now - me.LockStart < (me.Car.HasAbs ? 0.15f : 0.5f + 0.4f * me.MistakeSeverity) && me.Speed > 15;
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
        // kerbs are part of the track for drivers who use them; grass starts behind them
        float overPlus = me.Offset - (Line.RoomPlus[i] - half), overMinus = -Line.RoomMinus[i] + half - me.Offset;
        float beyond = MathF.Max(overPlus, overMinus) - MathF.Max(0.05f, KerbAllowance(me));
        if (beyond <= 0) return;
        float share = MathF.Min(1, beyond);
        float decel = 1.2f + 2.5f * share; // grass
        // what is really there under the outer wheels: grass, gravel (DAMPING eats speed), a tarmac run-off (hardly slows)
        if (OffTrack != null)
        {
            float s = Line.WrapS((float)me.Distance);
            float side = overPlus > overMinus ? 1 : -1; // the edge it is over, not the sign of the offset (the line isn't the middle)
            var p = Line.PositionAt(s, me.Offset + side * half * 0.8f);
            if (OffTrack.SurfaceAt(p.X, p.Z, p.Y) is { } surf)
                decel = decel * Math.Clamp((1 - surf.Friction) / 0.4f, 0.2f, 2f) + surf.Damping * me.Speed * share;
        }
        me.Speed = MathF.Max(0, me.Speed - decel * dt);
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
            if (o.Speed < 2) continue; // standing cars (a queue behind me) don't come: I go first
            if (ds > -12) return false; // somebody right here
            if (o.Speed > 5 && -ds / o.Speed < 7) return false;
        }
        return true;
    }

    private void ClampOffsetWithAllowance(RaceBot me)
    {
        float s = Line.WrapS((float)me.Distance);
        float half = me.Car.Width / 2;
        // a clone may use the kerbs like the player did (its line stays KerbLimit from the edge of the width data)
        float allow = MathF.Max(Settings.GrassMoments ? me.EdgeAllowance : 0, KerbAllowance(me));
        // an overtake with two wheels on the grass (GrassPass)
        if (me.OvertakeTargetId >= 0) allow = MathF.Max(allow, GrassPassRoom(me) + EdgeMarginFor(me));
        float before = me.Offset;
        float limit = Math.Clamp(me.Offset, -Line.RoomMinusAt(s) + half - allow, Line.RoomPlusAt(s) - half + allow);
        float diff = limit - before;
        if (MathF.Abs(diff) <= 0.01f) return;
        me.ClampedAt = _now;
        // the limit itself can move inwards suddenly (an attack on the grass ends, the road gets narrower): back at up to 3 m/s
        // instead of at once, a snap of a metre sideways is a visible jump on the clients' screens
        // what this step's own sideways motion carried it past the limit is taken back at once, the rest smoothly
        float ownMotion = diff * me.LateralSpeed < 0 ? MathF.Abs(me.LateralSpeed) * _stepDt : 0;
        // in a mistake (grass, slide, spin) the car is where the mistake put it: hard limit, as before
        float maxStep = me.Mistake != MistakeKind.None ? MathF.Abs(diff) : ownMotion + MathF.Max(0.02f, 3f * _stepDt);
        if (MathF.Abs(diff) > maxStep + 0.75f) maxStep = MathF.Abs(diff) - 0.75f;
        me.Offset += Math.Clamp(diff, -maxStep, maxStep);
        // at the edge: no more sideways speed towards it (else it's pushed back every step: the car jitters on the clients' screens)
        if (diff * me.LateralSpeed < 0) me.LateralSpeed = 0;
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
            if (impact > 30) RaiseYellow(me.Id, true, Line.WrapS((float)me.Distance), "crash");
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
            if (impact > 30) RaiseYellow(me.Id, true, Line.WrapS((float)me.Distance), "crash");
            other.Speed = MathF.Min(other.Speed + MathF.Min(1.5f, impact / 20f), MathF.Max(other.Speed, me.Speed));
            AddDamage(me, 0, impact);
            AddDamage(other, 1, impact);
            Wobble(other, Math.Clamp(impact / 40f, 0.15f, 0.5f));
        }
        me.CautiousUntil = Math.Max(me.CautiousUntil, _now + 2);
        float hit = sideBySide ? closing * 3.6f + 3 : closing * 3.6f + 2;
        Shaken(me, hit);
        Shaken(other, hit);
        // "me" did it: ran into the car in front, or moved over into the car alongside (closing above)
        Grudge(other, me.Id);
        RaiseIncident(me.Id, other.Id, hit > 30 ? "crash" : "contact", sideBySide ? $"side contact ({hit:F0} km/h)" : $"hit the car in front ({hit:F0} km/h)");
        if (me.OvertakeTargetId >= 0) Diag("end:contact");
        me.OvertakeTargetId = -1;
    }

    /// <summary>
    /// After a real crash the driver is shaken for a while: no attacks, keeps out of trouble (cool heads get over it sooner).
    /// </summary>
    internal void Shaken(RaceBot me, float impactKmh)
    {
        if (impactKmh < 25) return;
        float seconds = Math.Clamp(impactKmh / 40f, 0.6f, 2f) * (25 - 15 * me.Driver.Personality.Composure);
        me.CautiousUntil = Math.Max(me.CautiousUntil, _now + seconds);
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
        // below the zone's damage speed (damage.ini) only scratches
        float body = v < bot.Car.DamageMinKmh[zone] ? v * 0.15f : v * 0.6f;
        bot.DamageZones[zone] = MathF.Min(400, bot.DamageZones[zone] + body);
        if (v > bot.Car.SuspDamageMinKmh) suspension += (v - bot.Car.SuspDamageMinKmh) / 250f;
        bot.Suspension = Math.Clamp(bot.Suspension + suspension * Settings.DamageRate, 0, 1);
        bot.DamageVersion++;
        UpdateGrip(bot);
    }

    /// <summary>Grip lost to damage (1 = undamaged).</summary>
    public static float DamageGrip(RaceBot bot)
    {
        var z = bot.DamageZones;
        // like in AC: a dented body doesn't cost mechanical grip, a bent suspension does (and the aero below)
        float g = 1 - 0.15f * bot.Suspension;
        // a damaged wing or splitter loses downforce (aero.ini ZONE_x_CL): the aero part of the grip (a third in a fast corner) goes
        var cl = bot.Car.ZoneCl;
        if (bot.Car.Downforce > 0)
            g -= 0.3f * (1 - 1 / (1 + cl[0] * z[0] + cl[1] * z[1] + cl[2] * z[2] + cl[3] * z[3]));
        return Math.Clamp(g, 0.65f, 1f);
    }

    /// <summary>Extra aero drag from damage (1 = undamaged), like aero.ini ZONE_x_CD.</summary>
    public static float DamageDrag(RaceBot bot)
    {
        var z = bot.DamageZones;
        var cd = bot.Car.ZoneCd;
        return 1 + cd[0] * z[0] + cd[1] * z[1] + cd[2] * z[2] + cd[3] * z[3];
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
