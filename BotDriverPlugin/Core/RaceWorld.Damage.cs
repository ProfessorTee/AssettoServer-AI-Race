using System.Numerics;
namespace BotDriverPlugin.Core;

/// <summary>Contacts between bots, damage (slower car, repaired in the pits) and the grip of a wet track.</summary>
public sealed partial class RaceWorld
{
    /// <summary>
    /// Two cars hit (from <see cref="ResolveContacts"/>, closing at <paramref name="closing"/> m/s): counted once per second and pair, damage
    /// where each was hit (between bots; a player's hits on a bot come with his client's collision message).
    /// </summary>
    private void ContactEvent(in Body a, in Body b, float closing, Vector2 ra, Vector2 rb)
    {
        var me = a.Bot!;
        if (me.LastContactWith == b.Id && _now - me.LastContactAt < 1) { me.LastContactAt = _now; return; }
        me.LastContactAt = _now;
        me.LastContactWith = b.Id;
        me.ContactCount++;
        var other = b.Bot;
        if (other == null) return;
        other.LastContactAt = _now;
        other.LastContactWith = me.Id;
        other.ContactCount++;
        if (!Settings.BotContacts) return;
        ContactLog?.Invoke(FormattableString.Invariant($"{_now,7:F1}s {me.Name}/{other.Name} at {a.S:F0} m, v {me.Speed * 3.6f:F0}/{other.Speed * 3.6f:F0}, off {me.Offset:F2}/{other.Offset:F2}, closing {closing:F2}"));
        AddDamage(me, Zone(a, ra), closing * 3.6f);
        AddDamage(other, Zone(b, rb), closing * 3.6f);
    }

    /// <summary>AC damage zone (0 front, 1 rear, 2 left, 3 right) of a hit at <paramref name="r"/> (line frame, from the car's centre).</summary>
    private int Zone(in Body car, Vector2 r)
    {
        float x = Vector2.Dot(r, car.U), y = Vector2.Dot(r, car.V);
        if (MathF.Abs(x) / car.HalfL > MathF.Abs(y) / car.HalfW) return x > 0 ? 0 : 1;
        return (y > 0) == Line.RightIsPlus ? 3 : 2;
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

    /// <summary>Grip on the current track (1 = dry). Slicks lose a lot in the wet and on standing water.</summary>
    public float RainGrip(RaceBot bot)
    {
        // the server already lowers the track grip for everybody when RainTrackGripReductionPercent is set
        if (Settings.ServerRainReduction > 0) return 1;
        float w = Math.Clamp(Settings.Wetness, 0, 1), water = Math.Clamp(Settings.Water, 0, 1);
        return Math.Clamp(1 - Settings.RainGripLoss * (0.15f * w + 0.25f * water), 0.4f, 1f);
    }
}
