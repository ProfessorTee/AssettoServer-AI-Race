namespace RaceAiPlugin.Core;

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
        // a wide window: at 40 °C ~7 % less grip, at 120 °C ~4 % less
        float k = d < 0 ? 3.5e-5f : 3.5e-5f;
        return Math.Clamp(1 - k * d * d, 0.85f, 1f);
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
        float f = TyreTempGrip(bot.TyreTempFront), r = TyreTempGrip(bot.TyreTempRear);
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
        if (me.Phase != BotPhase.Racing || me.InPitLane || Settings.IsRace || me.TimingValid) return false;
        if (MathF.Min(me.TyreTempFront, me.TyreTempRear) > 65 || me.Speed < 20) return false;
        if (MathF.Abs(Line.CurvatureAt(myS)) > 1 / 400f || MathF.Abs(Line.CurvatureAt(myS + 60)) > 1 / 400f) return false;
        return true;
    }
}
