namespace BotDriverPlugin.Core;

/// <summary>
/// Tyre temperatures (core, front and rear axle). Tyres have to be driven into their window: cold tyres after the pits
/// or on the out-lap have less grip, a long fight, slides or lock-ups overheat them, the wet cools them down.
/// The temperatures change the grip and the wear. (The AC network protocol doesn't carry tyre temperatures,
/// so the Tyres app in the game can't show them for other cars; the dashboard shows them.)
/// </summary>
public sealed partial class RaceWorld
{
    /// <summary>Middle of the working window of a GT3 slick (°C).</summary>
    public const float TyreOptimum = 85f;

    /// <summary>Grip factor of a tyre at a core temperature.</summary>
    public static float TyreTempGrip(float t)
    {
        float d = t - TyreOptimum;
        // GT3 slicks: cold is slippery (65 °C ~4 %, 55 °C ~10 %, 45 °C ~18 % less grip), overheated they go off too
        // (100 °C ~2 %, 110 °C ~5 %, 120 °C ~10 % less)
        float k = d < 0 ? 1.1e-4f : 8e-5f;
        return Math.Clamp(1 - k * d * d, 0.75f, 1f);
    }

    /// <summary>Grip factor at a core temperature from the compound's own curve (street tyres work cooler than slicks), else a GT3 slick.</summary>
    public static float TyreTempGrip(RaceBot bot, float t)
        => (bot.Tyres?.TempCurve ?? bot.Car.TyreTempCurve) is { } c ? Math.Clamp(c.At(t), 0.7f, 1f) : TyreTempGrip(t);

    /// <summary>Middle of the working window of the compound on the car.</summary>
    public static float TyreOptimumOf(RaceBot bot) => bot.Tyres?.Optimum ?? bot.Car.TyreOptimum;

    /// <summary>Grip of the tyres on the car after <paramref name="virtualKm"/> (their wear curve, 1 = new).</summary>
    public static float WearGrip(RaceBot bot, float virtualKm) => bot.Tyres?.GripAt(virtualKm) ?? bot.Car.TyreGripAt(virtualKm);

    /// <summary>Short name of the compound on the car (S, M, H, SM ...), "" when unknown.</summary>
    public static string CompoundName(RaceBot bot) => (bot.Tyres ?? bot.Car.DefaultCompound)?.ShortName ?? "";

    /// <summary>
    /// Picks the compound for the next stint, like a driver with his engineer:
    /// cars on road tyres (JDM, street cars) take the grippiest one they may use (their best semislicks);
    /// cars on slicks weigh grip against wear over the stint with the compounds' own wear curves and warm-up windows (a soft is quick
    /// for a few laps, then drops off; a hard needs more heat), plus the driver's taste: aggressive attackers like a soft, smooth and
    /// careful drivers a hard. Qualifying: the softest. Only what LEGAL_TYRES allows.
    /// </summary>
    public void ChooseTyres(RaceBot bot, float stintLaps, bool coldStart = false)
    {
        var car = bot.Car;
        var legal = string.IsNullOrWhiteSpace(bot.LegalTyres) ? null
            : bot.LegalTyres.Split(';', ',').Select(x => x.Trim()).Where(x => x != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = car.Compounds.Where(c => legal == null || legal.Contains(c.ShortName)).ToList();
        if (options.Count == 0) { bot.Tyres = null; return; }
        var dry = options.Where(c => !c.IsWet).ToList();
        if (dry.Count > 0) options = dry;
        if (car.DefaultCompound?.IsSlick != true || options.All(c => !c.IsSlick) || Settings.Qualifying)
        {
            bot.Tyres = options.MaxBy(c => c.Grip);
            return;
        }
        options = options.Where(c => c.IsSlick).ToList();
        float minGrip = options.Min(c => c.Grip), maxGrip = options.Max(c => c.Grip);
        int laps = (int)Math.Clamp(MathF.Round(stintLaps), 1, 60);
        float vkm = Settings.TyreWearRate > 0 ? TyreVkmPerLap(bot) : 0;
        var p = bot.Driver.Personality;
        // taste: +1 likes it soft (attacks, late on the brakes, greedy), -1 likes it hard (smooth, careful, saves the tyres)
        float taste = Math.Clamp((bot.Driver.Aggression - 0.5f) * 1.2f + (p.Attack - 0.55f) * 0.8f + 0.4f * p.BrakeBehavior - 0.6f * p.Smoothness, -1, 1);
        float start = coldStart ? ColdTyreTemperature() : float.NaN;
        Compound? best = null;
        float bestCost = float.MaxValue;
        foreach (var c in options)
        {
            // share of the lap time lost against perfect tyres, lap by lap through the stint
            float cost = 0;
            for (int i = 0; i < laps; i++) cost += 1 - MathF.Sqrt(c.Grip * c.GripAt((i + 0.5f) * vkm) / maxGrip);
            // out of the pits on cold tyres: about half a lap below the window (a hard needs longer)
            if (coldStart)
            {
                float tg = c.TempCurve is { } curve ? Math.Clamp(curve.At(start), 0.7f, 1f) : TyreTempGrip(start - (c.Optimum - TyreOptimum));
                cost += 0.5f * (1 - MathF.Sqrt(tg));
            }
            float soft = maxGrip > minGrip ? (c.Grip - minGrip) / (maxGrip - minGrip) : 0.5f;
            cost -= (0.012f * taste + 0.003f * (float)NextGaussian()) * soft * laps;
            if (cost < bestCost) { bestCost = cost; best = c; }
        }
        bot.Tyres = best;
    }

    public void SetTyreTemperature(RaceBot bot, float temperature)
    {
        bot.TyreTempFront = temperature;
        bot.TyreTempRear = temperature;
    }

    /// <summary>Temperature of a new set straight from the rack (no tyre warmers).</summary>
    private float ColdTyreTemperature() => MathF.Max(Settings.AmbientTemp, Settings.RoadTemp * 0.6f + Settings.AmbientTemp * 0.4f) + 6;

    private void UpdateTyreTemperatures(RaceBot bot, float dt, float latUsage, float brakeUsage, float driveUsage)
    {
        if (dt <= 0) return;
        float v = bot.Speed;
        float road = Settings.RoadTemp;
        float wet = Math.Clamp(Settings.Wetness + 2 * Settings.Water, 0, 2);

        // heating: work done by the tyre (load x speed), a lot more while sliding or locking up
        float slide = bot.Mistake is MistakeKind.Slide or MistakeKind.Spin ? 1.2f : 0f;
        float lock_ = bot.FrontLock ? 1.2f : 0f;
        const float h = 0.061f;
        float heatF = h * v * (0.2f + 1.2f * MathF.Max(latUsage, brakeUsage) + lock_);
        float heatR = h * v * (0.2f + 1.2f * MathF.Max(latUsage, driveUsage) + slide);
        // cooling: towards the road temperature, faster at speed (air flow) and a lot faster in the wet
        float cool = 0.02f * (1 + v / 50f) * (1 + 1.5f * wet);

        bot.TyreTempFront += (heatF - cool * (bot.TyreTempFront - road)) * dt;
        bot.TyreTempRear += (heatR - cool * (bot.TyreTempRear - road)) * dt;
        bot.TyreTempFront = Math.Clamp(bot.TyreTempFront, road - 5, 160);
        bot.TyreTempRear = Math.Clamp(bot.TyreTempRear, road - 5, 160);
    }

    /// <summary>Grip of the tyres from their temperature (the worse axle counts more).</summary>
    public static float TyreTemperatureGrip(RaceBot bot)
    {
        float f = TyreTempGrip(bot, bot.TyreTempFront), r = TyreTempGrip(bot, bot.TyreTempRear);
        return 0.6f * MathF.Min(f, r) + 0.4f * (f + r) / 2;
    }

    /// <summary>Wear multiplier: overheated tyres wear a lot faster, cold ones grain a little.</summary>
    private static float TyreTempWear(RaceBot bot)
    {
        float t = MathF.Max(bot.TyreTempFront, bot.TyreTempRear);
        float hot = t > 100 ? 1 + (t - 100) * 0.03f : 1;
        float cold = MathF.Min(bot.TyreTempFront, bot.TyreTempRear) < 55 ? 1.1f : 1;
        return hot * cold;
    }

    /// <summary>Out-lap in practice / qualifying with cold tyres: weave a little on the straights to warm them up.</summary>
    private bool WantsTyreWarmWeave(RaceBot me, float myS)
    {
        if (me.Phase != BotPhase.Racing || me.InPitLane) return false;
        if (Settings.SafetyCar)
        {
            // behind the safety car: keep the tyres warm on every straight
            if (me.Speed < 12) return false;
            return MathF.Abs(Line.CurvatureAt(myS)) < 1 / 300f && MathF.Abs(Line.CurvatureAt(myS + 40)) < 1 / 300f;
        }
        if (Settings.IsRace || me.TimingValid) return false;
        if (MathF.Min(me.TyreTempFront, me.TyreTempRear) > 65 || me.Speed < 20) return false;
        if (MathF.Abs(Line.CurvatureAt(myS)) > 1 / 400f || MathF.Abs(Line.CurvatureAt(myS + 60)) > 1 / 400f) return false;
        return true;
    }
}
