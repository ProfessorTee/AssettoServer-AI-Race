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
    /// The compound for the next stint, like the AC AI (RaceManager / ai_tyres.ini): in qualifying the grippiest one, otherwise the car's
    /// preferred one (tyres.ini COMPOUND_DEFAULT); only what LEGAL_TYRES allows, never rain tyres (players don't get any either).
    /// </summary>
    public void ChooseTyres(RaceBot bot, float stintLaps, bool coldStart = false)
    {
        var car = bot.Car;
        var legal = string.IsNullOrWhiteSpace(bot.LegalTyres) ? null
            : bot.LegalTyres.Split(';', ',').Select(x => x.Trim()).Where(x => x != "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = car.Compounds.Where(c => !c.IsWet && (legal == null || legal.Contains(c.ShortName))).ToList();
        if (options.Count == 0) { bot.Tyres = null; return; }
        if (Settings.Qualifying) { bot.Tyres = options.MaxBy(c => c.Grip); return; }
        bot.Tyres = options.FirstOrDefault(c => c == car.DefaultCompound) ?? options.MaxBy(c => c.Grip);
    }

    public void SetTyreTemperature(RaceBot bot, float temperature)
    {
        bot.TyreTempFront = temperature;
        bot.TyreTempRear = temperature;
        if (HeatOf(bot) is { } heat && bot.Car.Physics is { } d)
            for (int i = 0; i < 4; i++) heat[i].Set(temperature, i < 2 ? d.Front : d.Rear);
    }

    /// <summary>The bot's tyres in the vehicle model (made with its first pressures on first use), null without a vehicle model.</summary>
    internal Physics.TyreHeat[]? HeatOf(RaceBot bot)
    {
        if (bot.Heat != null || bot.Car.Physics is not { } d) return bot.Heat;
        bot.Heat = [new(d.Front.PressureStatic), new(d.Front.PressureStatic), new(d.Rear.PressureStatic), new(d.Rear.PressureStatic)];
        float t = MathF.Max(Settings.AmbientTemp, Settings.RoadTemp);
        for (int i = 0; i < 4; i++) bot.Heat[i].Set(t, i < 2 ? d.Front : d.Rear);
        ChoosePressures(bot, learn: false);
        return bot.Heat;
    }

    /// <summary>
    /// The cold pressures for the next stint, as the driver and his engineer set them: hot at PRESSURE_IDEAL, except the axle the driver
    /// wants a little off it for his style (up to 1.5 psi over: a looser rear for a driver who wants the car to turn, a softer front for one
    /// who wants it calm). The first time from the car's default (tyres.ini PRESSURE_STATIC, set for the ideal), later corrected by the
    /// average hot pressure the last stint ran at. Weaker drivers get it less right (±3 psi × (1 − level)). Like AC: only on new tyres.
    /// </summary>
    public void ChoosePressures(RaceBot bot, bool learn)
    {
        if (bot.Heat is not { } heat || bot.Car.Physics is not { } d) return;
        if (float.IsNaN(bot.PressureStyle))
            bot.PressureStyle = Math.Clamp((bot.Driver.Aggression / 0.95f - 0.5f) * 1.2f + (_rng.NextSingle() - 0.5f) * 0.8f, -1, 1);
        float miss = (1 - Math.Clamp(bot.Driver.Pace, 0, 1)) * 3;
        for (int i = 0; i < 4; i++)
        {
            var t = i < 2 ? d.Front : d.Rear;
            var h = heat[i];
            float off = i < 2 ? MathF.Max(0, -bot.PressureStyle) : MathF.Max(0, bot.PressureStyle);
            float target = t.PressureIdeal + off * 1.5f;
            float set;
            if (!learn) set = t.PressureStatic + target - t.PressureIdeal;
            else if (h.PressureTime > 60) set = h.StaticPressure + target - (float)(h.PressureSum / h.PressureTime);
            else continue;
            // (Box-Muller: a normal error)
            float g = MathF.Sqrt(-2 * MathF.Log(1 - _rng.NextSingle())) * MathF.Cos(2 * MathF.PI * _rng.NextSingle());
            h.StaticPressure = Math.Clamp(set + g * miss, MathF.Max(5, t.PressureStatic - 8), t.PressureStatic + 8);
            h.PressureSum = h.PressureTime = 0;
        }
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
        float slide = bot.RearSlip > 1 ? 1.2f : 0f;
        float lock_ = bot.FrontLock ? 1.2f : 0f;
        const float h = 0.061f;
        const float wake = 1;
        float heatF = h * v * (0.2f + 1.2f * MathF.Max(latUsage, brakeUsage) + lock_) * wake;
        float heatR = h * v * (0.2f + 1.2f * MathF.Max(latUsage, driveUsage) + slide) * wake;
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
}
