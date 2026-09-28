using System.Numerics;

namespace RaceAiPlugin.Core;

/// <summary>Fuel, tyre wear, car weight and pit stops.</summary>
public sealed partial class RaceWorld
{
    private const float FuelDensity = 0.745f;
    private const float DecisionDistance = 1000f;

    /// <summary>Assigns a pit box (world position, e.g. AC_PIT_x) to a bot. Needs <see cref="PitLane"/>.</summary>
    public void SetPitBox(RaceBot bot, Vector3 position)
    {
        if (PitLane == null) return;
        var (s, off) = PitLane.Project(position);
        bot.PitBoxS = s;
        bot.PitBoxOffset = Math.Clamp(off, -8, 8);
    }

    /// <summary>Litres a bot needs per lap (measured while driving, estimated from the car data before that).</summary>
    public float FuelPerLap(RaceBot bot)
    {
        if (bot.LastLapFuel > 0) return bot.LastLapFuel;
        if (bot.Car.CalibratedFuelPerLap > 0) return bot.Car.CalibratedFuelPerLap * Settings.FuelRate * FuelStyle(bot);
        float perKm = bot.FuelPerKmEma > 0 ? bot.FuelPerKmEma : Settings.FuelRate / MathF.Max(0.3f, bot.Car.KmPerLiter);
        return perKm * Line.Length / 1000f;
    }

    /// <summary>Virtual tyre km per lap (measured on the current set, calibrated before that).</summary>
    private float TyreVkmPerLap(RaceBot bot)
    {
        if (bot.TyreKm > 5 && bot.TyreVirtualKm > 0.1f) return bot.TyreVirtualKm / bot.TyreKm * Line.Length / 1000f;
        if (bot.Car.CalibratedTyreVkmPerLap > 0) return bot.Car.CalibratedTyreVkmPerLap * Settings.TyreWearRate * TyreStyle(bot);
        return Line.Length / 1000f * Settings.TyreWearScale * Settings.TyreWearRate;
    }

    private static float FuelStyle(RaceBot bot) => bot.Driver.Personality.FuelUse * (1 - 0.08f * bot.Driver.Personality.Smoothness);
    private static float TyreStyle(RaceBot bot) => bot.Driver.Personality.TyreWear * (1 - 0.3f * bot.Driver.Personality.Smoothness);

    /// <summary>Fuel for <paramref name="laps"/> laps plus reserve, limited by the tank.</summary>
    public float FuelForLaps(RaceBot bot, float laps)
        => Math.Clamp(FuelPerLap(bot) * laps + Settings.FuelReserve, 1, bot.Car.FuelCapacity);

    /// <summary>Tank, tyres and condition for a new session.</summary>
    public void ResetCarCondition(RaceBot bot, float fuel, bool warmTyres = true)
    {
        bot.Fuel = Math.Clamp(fuel, 0, bot.Car.FuelCapacity);
        bot.TyreVirtualKm = 0;
        bot.TyreKm = warmTyres ? 10 : 0;
        bot.Pit = PitPhase.None;
        bot.PitReason = "";
        bot.PitStops = 0;
        bot.MandatoryPitDone = false;
        bot.LastDecisionLap = long.MinValue;
        bot.FuelPerKmEma = 0;
        bot.FuelAtLapStart = -1;
        bot.FuelAddedThisLap = 0;
        bot.LastLapFuel = 0;
        Array.Clear(bot.DamageZones);
        bot.Suspension = 0;
        bot.DamageVersion++;
        bot.PitRepair = false;
        if (bot.Mistake != MistakeKind.None) EndMistake(bot);
        bot.EdgeAllowance = 0;
        UpdateGrip(bot);
    }

    /// <summary>
    /// Session start from the pits: the bot starts standing in its box and drives down the pit lane onto the track (out lap).
    /// False when there is no pit lane or no box, then the caller has to place the bot somewhere else.
    /// </summary>
    public bool ReleaseFromPitBox(RaceBot bot)
    {
        var lane = PitLane;
        if (lane == null || bot.PitBoxS < 0) return false;
        PlaceAt(bot, lane.TrackSAt(bot.PitBoxS, Line), lane.TrackOffsetAt(bot.PitBoxS), BotPhase.Racing);
        bot.Pit = PitPhase.InLane;
        bot.PitS = bot.PitBoxS;
        bot.PitLateral = bot.PitBoxOffset;
        bot.PitServiced = true;
        bot.PittedThisLap = true;
        bot.PitEnteredAt = _now;
        bot.Speed = 0;
        bot.LapStartTime = _now;
        bot.CautiousUntil = _now + 3;
        return true;
    }

    /// <summary>Distance along the pit lane of the bot's box (-1 = none).</summary>
    public float PitBoxDistance(RaceBot bot) => bot.PitBoxS;

    /// <summary>Sends a bot into the pits at the next pit entry (e.g. admin command).</summary>
    public void RequestPitStop(RaceBot bot, string reason)
    {
        if (PitLane == null || bot.PitBoxS < 0 || bot.InPitLane) return;
        bot.Pit = PitPhase.Requested;
        bot.PitReason = reason;
        PlanService(bot, changeTyres: true);
    }

    private bool OffTrackInPits(RaceBot bot)
        => PitLane != null && (bot.Pit == PitPhase.Stopped
                               || (bot.Pit == PitPhase.InLane && bot.PitS >= PitLane.LimiterStart && bot.PitS <= PitLane.LimiterEnd));

    // ------------------------------------------------------------------ condition

    private void UpdateCarCondition(RaceBot bot, float dt)
    {
        float ds = bot.Speed * dt;
        var car = bot.Car;

        if (Settings.FuelRate > 0 && ds > 0 && bot.Phase != BotPhase.CoolDown)
        {
            float full = MathF.Max(0.1f, car.AccelAt(bot.Speed, 1));
            float throttle = bot.Accel > 0 ? Math.Clamp(bot.Accel * bot.MassRatio / full, 0, 1) : 0.05f;
            float perKm = Settings.FuelRate / MathF.Max(0.3f, car.KmPerLiter) * (0.35f + 1.0f * throttle) * FuelStyle(bot);
            bot.Fuel = MathF.Max(0, bot.Fuel - perKm * ds / 1000f);
            float alpha = MathF.Min(1, ds / 3000f);
            bot.FuelPerKmEma = bot.FuelPerKmEma <= 0 ? perKm : bot.FuelPerKmEma + (perKm - bot.FuelPerKmEma) * alpha;
        }

        if (Settings.TyreWearRate > 0 && ds > 0)
        {
            float k = bot.InPitLane ? 0 : Line.CurvatureAt(Line.WrapS((float)bot.Distance));
            float lat = bot.Speed * bot.Speed * MathF.Abs(k) / (car.LateralGrip * CarSpec.G);
            float lon = MathF.Abs(bot.Accel) / (car.BrakeGrip * CarSpec.G);
            float usage = Math.Clamp(MathF.Max(lat, lon), 0, 1.2f);
            bot.TyreVirtualKm += TyreStyle(bot) * ds / 1000f * Settings.TyreWearScale * Settings.TyreWearRate * (0.3f + 1.4f * usage) * (0.9f + 0.2f * bot.Driver.Aggression);
        }
        bot.TyreKm += ds / 1000f;
        UpdateGrip(bot);
    }

    private void UpdateGrip(RaceBot bot)
    {
        var car = bot.Car;
        float tyre = Settings.TyreWearRate > 0 ? car.TyreGripAt(bot.TyreVirtualKm) : 1f;
        float cold = bot.TyreKm < 3 ? 0.94f + 0.06f * bot.TyreKm / 3 : 1f;
        float mass = car.ReferenceMass - 25 * FuelDensity + bot.Fuel * FuelDensity;
        bot.MassRatio = Settings.FuelRate > 0 ? Math.Clamp(mass / car.ReferenceMass, 0.8f, 1.3f) : 1f;
        float massGrip = 1 - 0.3f * (bot.MassRatio - 1);
        bot.CarGrip = tyre * cold * massGrip * (Settings.Damage ? DamageGrip(bot) : 1f) * RainGrip(bot);
    }

    // ------------------------------------------------------------------ strategy

    private void CheckPitEntry(RaceBot bot)
    {
        var lane = PitLane;
        if (lane == null || !Settings.PitStops || bot.PitBoxS < 0 || bot.Phase != BotPhase.Racing) return;

        float s = Line.WrapS((float)bot.Distance);
        float toEntry = Line.Delta(s, lane.EntryTrackS);

        if (bot.Pit == PitPhase.None && toEntry > 0 && toEntry < DecisionDistance)
        {
            long key = (long)Math.Floor((bot.Distance - lane.EntryTrackS) / Line.Length);
            if (key != bot.LastDecisionLap)
            {
                bot.LastDecisionLap = key;
                Decide(bot);
            }
        }

        if (bot.Pit == PitPhase.Requested && toEntry <= 0 && toEntry > -25)
            EnterPitLane(bot);
    }

    private void Decide(RaceBot bot)
    {
        int remaining = bot.RemainingLaps;
        if (remaining <= 1) return; // last lap: no stop, whatever happens

        bool openEnd = remaining == int.MaxValue;
        int lapsAfterThis = openEnd ? 10 : remaining - 1;
        float perLap = FuelPerLap(bot);
        float lapTime = bot.LastLapSeconds > 0 ? bot.LastLapSeconds : Line.Length / 48f;
        // share of a lap from here to the finish line
        float toLine = Line.WrapS(Settings.StartLineS - Line.WrapS((float)bot.Distance)) / Line.Length;
        string reason = "";

        // fuel: not enough to finish and not enough to come round once more
        if (reason == "" && Settings.FuelRate > 0)
        {
            float needFinish = openEnd ? float.MaxValue : (lapsAfterThis + toLine) * perLap * 1.02f + 0.5f;
            float needNextLap = (1 + toLine) * perLap * 1.03f + 0.5f;
            if (bot.Fuel < needFinish && bot.Fuel < needNextLap) reason = "fuel";
        }

        // tyres: compare the time lost on worn tyres until the end with the time a stop costs
        if (reason == "" && Settings.TyreWearRate > 0 && bot.Car.TyreWear != null)
        {
            float vkmPerLap = TyreVkmPerLap(bot);
            float stay = 0, fresh = 0;
            for (int i = 1; i <= lapsAfterThis; i++)
            {
                stay += lapTime * (1 - MathF.Sqrt(bot.Car.TyreGripAt(bot.TyreVirtualKm + i * vkmPerLap)));
                fresh += lapTime * (1 - MathF.Sqrt(bot.Car.TyreGripAt((i - 0.5f) * vkmPerLap)));
            }
            float stopCost = 30 + bot.Car.TyreChangeTime; // pit lane + stationary time
            float gripNextLap = bot.Car.TyreGripAt(bot.TyreVirtualKm + vkmPerLap);
            if ((stay > fresh + stopCost && gripNextLap < Settings.TyreChangeGrip) || gripNextLap < 0.85f)
                reason = "tyres";
        }

        // damage: time lost until the end against the repair time
        if (reason == "" && Settings.Damage && HasDamage(bot))
        {
            float lossPerLap = lapTime * ((1 - MathF.Sqrt(DamageGrip(bot))) + 0.25f * (MathF.Sqrt(DamageDrag(bot)) - 1));
            if (lossPerLap * lapsAfterThis > RepairTime(bot) + 30 || DamageGrip(bot) < 0.8f)
                reason = "damage";
        }

        // mandatory stop in the pit window (race)
        if (reason == "" && Settings.PitWindowEnd > Settings.PitWindowStart && !bot.MandatoryPitDone && !openEnd)
        {
            int lap = bot.LapsCompleted + 1;
            float grip = bot.Car.TyreGripAt(bot.TyreVirtualKm);
            if (lap >= Settings.PitWindowStart && lap <= Settings.PitWindowEnd && (lap >= Settings.PitWindowEnd - 1 || grip < 0.985f))
                reason = "mandatory";
        }

        if (reason == "") return;
        bot.Pit = PitPhase.Requested;
        bot.PitReason = reason;
        PlanService(bot, changeTyres: reason != "fuel" || bot.Car.TyreGripAt(bot.TyreVirtualKm + TyreVkmPerLap(bot) * lapsAfterThis) < 0.97f);
    }

    private void PlanService(RaceBot bot, bool changeTyres)
    {
        float perLap = FuelPerLap(bot);
        int remaining = bot.RemainingLaps;
        float target = remaining == int.MaxValue
            ? MathF.Max(bot.Fuel, FuelForLaps(bot, 3))
            : FuelForLaps(bot, remaining - 1 + 0.3f);
        bot.PitFuelToAdd = Settings.FuelRate > 0 ? Math.Clamp(target - bot.Fuel, 0, bot.Car.FuelCapacity - bot.Fuel) : 0;
        bot.PitChangeTyres = changeTyres || Settings.FuelRate <= 0;
        // repair when there is something worth repairing (a player would tick "repair" too)
        bot.PitRepair = Settings.Damage && HasDamage(bot);
        _ = perLap;
    }

    // ------------------------------------------------------------------ driving in the pit lane

    private void EnterPitLane(RaceBot bot)
    {
        var lane = PitLane!;
        bot.Pit = PitPhase.InLane;
        bot.PitS = 0;
        bot.PittedThisLap = true;
        bot.PitServiced = false;
        bot.PitEnteredAt = _now;
        bot.OvertakeTargetId = -1;
        // start with the current lateral position and blend over to the pit lane
        bot.LateralSpeed = 0;
        bot.PitLateral = bot.Offset - lane.TrackOffset[0];
    }

    private void PitStep(RaceBot bot, float dt)
    {
        var lane = PitLane;
        if (lane == null)
        {
            bot.Pit = PitPhase.None;
            return;
        }

        if (bot.Pit == PitPhase.Stopped)
        {
            bot.Speed = 0;
            bot.Accel = 0;
            if (_now >= bot.PitServiceUntil)
            {
                float stopTime = (float)(_now - bot.PitStoppedAt);
                bot.Fuel = MathF.Min(bot.Car.FuelCapacity, bot.Fuel + bot.PitFuelToAdd);
                bot.FuelAddedThisLap += bot.PitFuelToAdd;
                if (bot.PitChangeTyres)
                {
                    bot.TyreVirtualKm = 0;
                    bot.TyreKm = 0;
                }
                if (bot.PitRepair) RepairDamage(bot);
                bot.PitStops++;
                if (Settings.PitWindowEnd > Settings.PitWindowStart && bot.LapsCompleted + 1 >= Settings.PitWindowStart)
                    bot.MandatoryPitDone = true;
                bot.PitServiced = true;
                bot.Pit = PitPhase.InLane;
                PitStopCompleted?.Invoke(bot, stopTime, bot.PitFuelToAdd, bot.PitChangeTyres);
            }
            return;
        }

        var car = bot.Car;
        float pace = DriverProfile.CornerSkill(bot.Driver.Pace) * Settings.GripFactor * bot.CarGrip;
        float v = bot.Speed;
        float decel = car.BrakeAt(v, pace) * 0.8f;
        float target = car.TopSpeed;

        // corners of the pit lane
        for (float d = 0; d < 120; d += 3)
        {
            float ps = bot.PitS + d;
            if (ps > lane.Length) break;
            float vLim = car.CornerLimit(lane.Curvature[lane.IndexAt(ps)], 0, pace * 0.9f);
            target = MathF.Min(target, MathF.Sqrt(vLim * vLim + 2 * decel * d));
        }

        // speed limit
        float limit = Settings.PitSpeedLimit;
        if (bot.PitS >= lane.LimiterStart && bot.PitS <= lane.LimiterEnd) target = MathF.Min(target, limit);
        else if (bot.PitS < lane.LimiterStart) target = MathF.Min(target, MathF.Sqrt(limit * limit + 2 * decel * (lane.LimiterStart - bot.PitS)));

        // stop in the box
        if (!bot.PitServiced && bot.PitBoxS >= 0)
        {
            float dist = bot.PitBoxS - bot.PitS;
            if (dist <= 0.3f)
            {
                bot.Speed = 0;
                bot.Accel = 0;
                bot.Pit = PitPhase.Stopped;
                bot.PitStoppedAt = _now;
                bot.PitRepair = Settings.Damage && HasDamage(bot);
                float service = (bot.PitChangeTyres ? car.TyreChangeTime : 0) + bot.PitFuelToAdd * car.FuelLiterTime + 3
                                + (bot.PitRepair ? RepairTime(bot) : 0);
                bot.PitServiceUntil = _now + service;
                return;
            }
            target = MathF.Min(target, MathF.Sqrt(2 * 5f * MathF.Max(0, dist - 0.3f)) + 0.5f);
        }

        // queue behind other cars in the pit lane
        foreach (var o in Bots)
        {
            if (o == bot || !o.InPitLane) continue;
            float gap = o.PitS - bot.PitS - (o.Car.Length + car.Length) / 2;
            if (gap < -1 || gap > 30) continue;
            // a car stopped in its box further along the lane is not in my way (it stands in the box lane)
            if (o.Pit == PitPhase.Stopped && MathF.Abs(o.PitBoxOffset) > 2) continue;
            target = MathF.Min(target, MathF.Max(0, o.Speed + (gap - 3) * 0.8f));
        }

        if (target > v)
        {
            float accel = car.AccelAt(v, pace) / bot.MassRatio;
            v = MathF.Min(target, v + accel * dt);
            bot.Accel = accel;
        }
        else
        {
            float brake = car.BrakeAt(v, pace);
            v = MathF.Max(target, v - brake * dt);
            bot.Accel = target < bot.Speed - 0.05f ? -brake : 0;
        }
        bot.Speed = MathF.Max(0, v);
        bot.PitS += bot.Speed * dt;

        // lateral: blend from the racing position into the pit lane, move over into the box and back out
        float latTarget = 0;
        if (bot.PitBoxS >= 0)
        {
            float toBox = bot.PitBoxS - bot.PitS;
            if (!bot.PitServiced && toBox < 25) latTarget = bot.PitBoxOffset * Smooth(1 - toBox / 25);
            else if (bot.PitServiced && -toBox < 20) latTarget = bot.PitBoxOffset * Smooth(1 + toBox / 20);
        }
        float maxLatStep = MathF.Max(1.2f, bot.Speed * 0.08f) * dt;
        bot.PitLateral += Math.Clamp(latTarget - bot.PitLateral, -maxLatStep, maxLatStep);

        // keep the track position (distance, lap timing, what the other cars see) in sync
        float trackS = lane.TrackSAt(bot.PitS, Line);
        float delta = Line.Delta(Line.WrapS((float)bot.Distance), trackS);
        if (delta > 0 && delta < 200) bot.Distance += delta;
        bot.Offset = lane.TrackOffsetAt(bot.PitS);

        if (bot.PitS >= lane.Length - 0.5f)
        {
            // back on the racing line
            bot.Pit = PitPhase.None;
            bot.PitReason = "";
            bot.TargetOffset = bot.Offset;
            bot.LateralSpeed = 0;
            bot.ReturnToLineAfter = _now + 1;
            bot.CautiousUntil = _now + 2;
        }
    }

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    private BotPose PitPose(RaceBot bot)
    {
        var lane = PitLane!;
        var pos = lane.PositionAt(bot.PitS, bot.PitLateral) + Vector3.UnitY * Settings.HeightOffset;
        var fwd = lane.ForwardAt(bot.PitS);
        var vel = fwd * bot.Speed;
        var rotation = new Vector3(
            MathF.Atan2(fwd.Z, fwd.X) - MathF.PI / 2,
            (MathF.Atan2(new Vector2(fwd.Z, fwd.X).Length(), fwd.Y) - MathF.PI / 2) * -1f,
            0);
        float k = lane.Curvature[lane.IndexAt(bot.PitS)];
        float wheel = MathF.Atan(bot.Car.Wheelbase * k) * 180 / MathF.PI;
        var (gear, rpm) = GearAndRpm(bot);
        float full = bot.Car.AccelAt(bot.Speed, bot.Driver.Pace);
        byte throttle = bot.Accel > 0.05f && full > 0.1f ? (byte)Math.Clamp(bot.Accel / full * 255f, 0, 255) : (byte)0;
        float s = Line.WrapS((float)bot.Distance);
        return new BotPose(pos, rotation, vel, bot.Speed, wheel, gear, rpm,
            bot.Accel < -1f || bot.Pit == PitPhase.Stopped,
            Line.WrapS(s - Settings.StartLineS) / Line.Length,
            throttle);
    }
}
