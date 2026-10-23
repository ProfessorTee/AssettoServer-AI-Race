using TrackGeometry;
using System.Numerics;

namespace BotDriverPlugin.Core;

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
        if (bot.Car.CalibratedFuelPerLap > 0) return bot.Car.CalibratedFuelPerLap * Settings.FuelRate;
        float perKm = bot.FuelPerKmEma > 0 ? bot.FuelPerKmEma : Settings.FuelRate / MathF.Max(0.3f, bot.Car.KmPerLiter);
        return perKm * Line.Length / 1000f;
    }

    /// <summary>Virtual tyre km per lap (measured on the current set, calibrated before that).</summary>
    private float TyreVkmPerLap(RaceBot bot)
    {
        if (bot.TyreKm > 5 && bot.TyreVirtualKm > 0.1f) return bot.TyreVirtualKm / bot.TyreKm * Line.Length / 1000f;
        if (bot.Car.CalibratedTyreVkmPerLap > 0) return bot.Car.CalibratedTyreVkmPerLap * Settings.TyreWearRate;
        return Line.Length / 1000f * Settings.TyreWearScale * Settings.TyreWearRate;
    }

    /// <summary>Fuel for <paramref name="laps"/> laps plus reserve, limited by the tank.</summary>
    public float FuelForLaps(RaceBot bot, float laps)
        => Math.Clamp(FuelPerLap(bot) * laps + Settings.FuelReserve, 1, bot.Car.FuelCapacity);

    /// <summary>Tank, tyres and condition for a new session.</summary>
    public void ResetCarCondition(RaceBot bot, float fuel, bool warmTyres = true)
    {
        bot.Fuel = Math.Clamp(fuel, 0, bot.Car.FuelCapacity);
        bot.TyreVirtualKm = 0;
        bot.TyreKm = warmTyres ? 10 : 0;
        SetTyreTemperature(bot, warmTyres ? StartTyreTemperature(bot) : ColdTyreTemperature(bot));
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
        NewSession(bot);
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
        bot.PitLateralSpeed = 0;
        bot.PitServiced = true;
        bot.PittedThisLap = true;
        bot.PitEnteredAt = _now;
        bot.Speed = 0;
        bot.LapStartTime = _now;
        // standing in the box: the tyres are as warm as the air around them
        SetTyreTemperature(bot, ColdTyreTemperature(bot));
        return true;
    }

    /// <summary>Distance along the pit lane of the bot's box (-1 = none).</summary>
    public float PitBoxDistance(RaceBot bot) => bot.PitBoxS;

    /// <summary>A drive-through: in at the next pit entry and along the pit lane without stopping.</summary>
    public void RequestDriveThrough(RaceBot bot)
    {
        if (PitLane == null) return;
        bot.Pit = PitPhase.Requested;
        bot.PitReason = "drive-through";
        bot.DriveThrough = true;
    }

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
            float perKm = Settings.FuelRate / MathF.Max(0.3f, car.KmPerLiter) * (0.35f + 1.0f * throttle);
            bot.Fuel = MathF.Max(0, bot.Fuel - perKm * ds / 1000f);
            float alpha = MathF.Min(1, ds / 3000f);
            bot.FuelPerKmEma = bot.FuelPerKmEma <= 0 ? perKm : bot.FuelPerKmEma + (perKm - bot.FuelPerKmEma) * alpha;
        }

        {
            float k = bot.InPitLane ? 0 : Line.CurvatureAt(Line.WrapS((float)bot.Distance));
            float lat = Math.Clamp(bot.Speed * bot.Speed * MathF.Abs(k) / (car.LateralGrip * CarSpec.G), 0, 1.2f);
            float lon = MathF.Abs(bot.Accel) / (car.BrakeGrip * CarSpec.G);
            float brakeUse = bot.Accel < 0 ? Math.Clamp(lon, 0, 1.2f) : 0, driveUse = bot.Accel > 0 ? Math.Clamp(lon, 0, 1.2f) : 0;
            if (bot.Heat is { } heat)
            {
                // the vehicle model heats the tyres: the axles' temperatures the grip goes by
                bot.TyreTempFront = (heat[0].Practical + heat[1].Practical) / 2;
                bot.TyreTempRear = (heat[2].Practical + heat[3].Practical) / 2;
            }
            else UpdateTyreTemperatures(bot, dt, lat, brakeUse, driveUse);
            if (Settings.TyreWearRate > 0 && ds > 0)
            {
                float usage = Math.Clamp(MathF.Max(lat, lon), 0, 1.2f);
                bot.TyreVirtualKm += ds / 1000f * Settings.TyreWearScale * Settings.TyreWearRate * (0.3f + 1.4f * usage) * TyreTempWear(bot);
            }
        }
        bot.TyreKm += ds / 1000f;
        UpdateGrip(bot);
    }

    private void UpdateGrip(RaceBot bot)
    {
        var car = bot.Car;
        float tyre = Settings.TyreWearRate > 0 ? WearGrip(bot, bot.TyreVirtualKm) : 1f;
        float cold = bot.Heat != null ? 1 : TyreTemperatureGrip(bot); // (with a vehicle model the tyres' own grip)
        float mass = car.ReferenceMass - 25 * FuelDensity + bot.Fuel * FuelDensity;
        bot.MassRatio = Settings.FuelRate > 0 ? Math.Clamp(mass / car.ReferenceMass, 0.8f, 1.3f) : 1f;
        // (the fuel's weight is the vehicle model's business: MassRatio)
        bot.CarGrip = tyre * cold * (bot.Tyres?.Grip ?? 1f) * (Settings.Damage ? DamageGrip(bot) : 1f) * RainGrip(bot);
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

    internal void Decide(RaceBot bot)
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

        // the AC AI stops for tyres at 95-97 % of the grip left (ai+0x204, rolled per driver)
        float changeAt = bot.TyreStopGrip;

        // tyres: compare the time lost on worn tyres until the end with the time a stop costs
        if (reason == "" && Settings.TyreWearRate > 0 && (bot.Tyres?.Wear ?? bot.Car.TyreWear) != null)
        {
            float vkmPerLap = TyreVkmPerLap(bot);
            float stay = 0, fresh = 0;
            for (int i = 1; i <= lapsAfterThis; i++)
            {
                stay += lapTime * (1 - MathF.Sqrt(WearGrip(bot, bot.TyreVirtualKm + i * vkmPerLap)));
                fresh += lapTime * (1 - MathF.Sqrt(WearGrip(bot, (i - 0.5f) * vkmPerLap)));
            }
            float stopCost = 30 + bot.Car.TyreChangeTime; // pit lane + stationary time
            float gripNextLap = WearGrip(bot, bot.TyreVirtualKm + vkmPerLap);
            if (openEnd)
            {
                // practice / qualifying: no race to optimise, drive the set until it's worn out
                if (gripNextLap < changeAt) reason = "tyres";
            }
            else if ((stay > fresh + stopCost && gripNextLap < changeAt) || gripNextLap < 0.85f)
                reason = "tyres";
        }

        // damage: a car that can hardly be driven comes in; otherwise like a driver who sees the board: a dented wing is only
        // repaired when it costs more than the stop (time and positions), and it waits for a stop that is due anyway
        if (reason == "" && Settings.Damage && HasDamage(bot))
        {
            float lossToEnd = lapTime * ((1 - MathF.Sqrt(DamageGrip(bot))) + 0.25f * (MathF.Sqrt(DamageDrag(bot)) - 1)) * lapsAfterThis;
            float stopCost = RepairTime(bot) + PitLaneLoss(lapTime);
            if (DamageGrip(bot) < 0.8f || bot.Suspension > 0.25f) reason = "damage";
            else if (lossToEnd > stopCost)
            {
                bool fuelStopDue = Settings.FuelRate > 0 && !openEnd && bot.Fuel < (lapsAfterThis + toLine) * perLap * 1.02f + 0.5f;
                if (openEnd) reason = "damage";
                // in a bunched field (the first laps) a stop costs as many places as the damage: hold the position, look again next lap
                else if (!fuelStopDue && (lossToEnd > 2 * stopCost || CarsBehindWithin(bot, stopCost, lapTime) < CarsBehindWithin(bot, lossToEnd, lapTime)))
                    reason = "damage";
            }
        }

        // mandatory stop in the pit window (race)
        if (reason == "" && Settings.PitWindowEnd > Settings.PitWindowStart && !bot.MandatoryPitDone && !openEnd)
        {
            int lap = bot.LapsCompleted + 1;
            float grip = WearGrip(bot, bot.TyreVirtualKm);
            if (lap >= Settings.PitWindowStart && lap <= Settings.PitWindowEnd && (lap >= Settings.PitWindowEnd - 1 || grip < 0.985f))
                reason = "mandatory";
        }

        if (reason == "") return;
        bot.Pit = PitPhase.Requested;
        bot.PitReason = reason;
        // a fuel stop only takes new tyres when the old ones wouldn't last the next stint (practice: what a full tank lasts)
        float stint = openEnd ? MathF.Min(lapsAfterThis, bot.Car.FuelCapacity / MathF.Max(0.1f, perLap)) : lapsAfterThis;
        PlanService(bot, changeTyres: reason != "fuel"
                                      || WearGrip(bot, bot.TyreVirtualKm + TyreVkmPerLap(bot) * stint) < MathF.Min(0.97f, changeAt - 0.02f));
    }

    /// <summary>Time a drive through the pit lane costs against staying on the track (limiter, slowing down and pulling away).</summary>
    private float PitLaneLoss(float lapTime)
    {
        var lane = PitLane;
        if (lane == null) return 30;
        float vRace = Line.Length / MathF.Max(30, lapTime);
        float limited = MathF.Max(0, lane.LimiterEnd - lane.LimiterStart);
        return limited / MathF.Max(5, Settings.PitSpeedLimit) - lane.Length / vRace + limited / vRace + 6;
    }

    /// <summary>Cars on the track behind <paramref name="bot"/> by less than <paramref name="seconds"/> (the places a delay costs).</summary>
    private int CarsBehindWithin(RaceBot bot, float seconds, float lapTime)
    {
        float vRace = Line.Length / MathF.Max(30, lapTime);
        float mine = bot.LapsCompleted * Line.Length + Line.WrapS((float)bot.Distance - Settings.StartLineS);
        int n = 0;
        foreach (var o in _neighbors)
        {
            if (o.IsBot && (o.Id == bot.Id || o.Bot!.InPitLane || o.Bot.Phase != BotPhase.Racing)) continue;
            float behind = mine - o.Progress;
            if (behind > 0 && behind < seconds * vRace) n++;
        }
        return n;
    }

    private void PlanService(RaceBot bot, bool changeTyres)
    {
        int remaining = bot.RemainingLaps;
        float target = remaining == int.MaxValue
            ? MathF.Max(bot.Fuel, FuelForLaps(bot, 3))
            : FuelForLaps(bot, remaining - 1 + 0.3f);
        bot.PitFuelToAdd = Settings.FuelRate > 0 ? Math.Clamp(target - bot.Fuel, 0, bot.Car.FuelCapacity - bot.Fuel) : 0;
        bot.PitChangeTyres = changeTyres || Settings.FuelRate <= 0;
        // repair when there is something worth repairing (a player would tick "repair" too)
        bot.PitRepair = Settings.Damage && HasDamage(bot);
    }

    // ------------------------------------------------------------------ driving in the pit lane

    private void EnterPitLane(RaceBot bot)
    {
        var lane = PitLane!;
        bot.Pit = PitPhase.InLane;
        bot.PitS = 0;
        bot.PittedThisLap = true;
        bot.PitServiced = bot.DriveThrough; // (a drive-through doesn't stop)
        bot.DriveThrough = false;
        bot.PitEnteredAt = _now;
        bot.Phys = null; // the pit lane is driven along its own line
        // start with the current lateral position and blend over to the pit lane
        bot.LateralSpeed = 0;
        bot.PitLateral = bot.Offset - lane.TrackOffset[0];
        bot.PitLateralSpeed = 0;
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
                    // the next stint: to the flag or as far as the fuel goes
                    float fuelLaps = bot.Fuel / MathF.Max(0.1f, FuelPerLap(bot));
                    ChooseTyres(bot, bot.RemainingLaps == int.MaxValue ? MathF.Min(10, fuelLaps) : MathF.Min(bot.RemainingLaps, fuelLaps), coldStart: true);
                    bot.TyreVirtualKm = 0;
                    bot.TyreKm = 0;
                    ChoosePressures(bot, learn: true);
                    SetTyreTemperature(bot, ColdTyreTemperature(bot));
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
        float pace = Settings.GripFactor * bot.CarGrip;
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
            if (!bot.PitServiced && toBox < 35) latTarget = bot.PitBoxOffset * Smooth(1 - toBox / 35);
            else if (bot.PitServiced && -toBox < 25) latTarget = bot.PitBoxOffset * Smooth(1 + toBox / 25);
        }
        // a car moves sideways only by steering: at most ~15° off the lane (a little at walking pace), the pose turns with it
        float maxLatSpeed = MathF.Max(0.3f, bot.Speed * 0.27f);
        float latSpeed = Math.Clamp((latTarget - bot.PitLateral) / MathF.Max(dt, 1e-3f), -maxLatSpeed, maxLatSpeed);
        bot.PitLateralSpeed += (latSpeed - bot.PitLateralSpeed) * MathF.Min(1, 4 * dt);
        bot.PitLateral += bot.PitLateralSpeed * dt;

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
            bot.Phys = null; // the vehicle model takes over again at the pit exit
            if (bot.Ai is { } ai) ai.DesiredOffset = bot.Offset;
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
        var pos = lane.PositionAt(bot.PitS, bot.PitLateral);
        // standing on the ground under it at its rest height (the lane's points are another car's, ~0.1 m low: the wheels sank)
        if (Road?.HeightAt(pos.X, pos.Z, pos.Y) is { } ground && bot.Car.Physics is { } vd) pos.Y = ground + vd.CgHeight;
        else pos += Vector3.UnitY * Settings.HeightOffset;
        var fwd = lane.ForwardAt(bot.PitS);
        lane.Interp(bot.PitS, out int li, out _, out _);
        var vel = fwd * bot.Speed + lane.Lateral[li] * bot.PitLateralSpeed;
        // heading along its path: moving over into the box or out of it turns the car
        var dir = Rotate(fwd, lane.Lateral[li], MathF.Atan2(bot.PitLateralSpeed, MathF.Max(bot.Speed, 2f)));
        var rotation = new Vector3(
            MathF.Atan2(dir.Z, dir.X) - MathF.PI / 2,
            (MathF.Atan2(new Vector2(dir.Z, dir.X).Length(), dir.Y) - MathF.PI / 2) * -1f,
            0);
        float k = lane.Curvature[lane.IndexAt(bot.PitS)];
        float wheel = MathF.Atan(bot.Car.Wheelbase * k) * 180 / MathF.PI;
        var (gear, rpm) = GearAndRpm(bot);
        float full = bot.Car.AccelAt(bot.Speed, 1);
        byte throttle = bot.Accel > 0.05f && full > 0.1f ? (byte)Math.Clamp(bot.Accel / full * 255f, 0, 255) : (byte)0;
        float s = Line.WrapS((float)bot.Distance);
        return new BotPose(pos, rotation, vel, bot.Speed, wheel, gear, rpm,
            bot.Accel < -1f || bot.Pit == PitPhase.Stopped,
            Line.WrapS(s - Settings.StartLineS) / Line.Length,
            throttle);
    }
}
