using System.Numerics;
namespace BotDriverPlugin.Core;

/// <summary>Contacts between bots, damage (slower car, repaired in the pits) and the grip of a wet track.</summary>
public sealed partial class RaceWorld
{
    /// <summary>Two bots touched (from ResolveOverlaps): a bounce apart or a push, and damage. "me" ran into "other" (or moved over into it).</summary>
    /// <summary>
    /// A hit on a car with a vehicle model, like a collision in AC's physics: an impulse of (1 + 0.3) × closing speed × reduced mass along
    /// <paramref name="n"/> (line frame: along, across; from the other car into this one) at the contact point <paramref name="r"/> (line frame,
    /// from this car's centre). It pushes, slows or speeds the car up and turns it; the AI then has to catch it. False without a vehicle model.
    /// </summary>
    internal bool Impulse(RaceBot bot, Vector2 n, Vector2 r, float closing, float otherMass)
    {
        if (bot.Phys is not { } st || bot.Car.Physics is not { } d) return false;
        if (closing <= 0) return true;
        float m = d.Mass * bot.MassRatio, iz = d.YawInertia * bot.MassRatio;
        float j = 1.3f * closing * m * otherMass / (m + otherMass);
        float c = MathF.Cos(st.Psi), s = MathF.Sin(st.Psi);
        // into the car's frame (x forward, y towards +offset)
        float jx = (n.X * c + n.Y * s) * j, jy = (-n.X * s + n.Y * c) * j;
        float rx = r.X * c + r.Y * s, ry = -r.X * s + r.Y * c;
        st.Vx += jx / m;
        st.Vy += jy / m;
        st.R += (rx * jy - ry * jx) / iz;
        return true;
    }

    /// <summary>The contact point on a car towards <paramref name="toOther"/> (line frame), on the edge of its footprint.</summary>
    private static Vector2 ContactPoint(RaceBot bot, Vector2 toOther)
    {
        float l = bot.Car.Length / 2, w = bot.Car.Width / 2;
        float k = MathF.Min(l / MathF.Max(1e-3f, MathF.Abs(toOther.X)), w / MathF.Max(1e-3f, MathF.Abs(toOther.Y)));
        return toOther * MathF.Min(k, 1e3f);
    }

    private void BotContact(RaceBot me, RaceBot other, bool sideBySide, float dOff)
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
        ContactLog?.Invoke(FormattableString.Invariant($"{_now,7:F1}s {(sideBySide ? "side" : "rear")} {me.Name}/{other.Name} at {Line.WrapS((float)me.Distance):F0} m, v {me.Speed * 3.6f:F0}/{other.Speed * 3.6f:F0}, off {me.Offset:F2}/{other.Offset:F2}, closing {closing:F2}"));

        // with vehicle models: a real hit (impulse at the contact point) instead of the kinematic bounce
        float ds = Line.Delta(Line.WrapS((float)me.Distance), Line.WrapS((float)other.Distance));
        var toOther = Vector2.Normalize(new Vector2(ds, dOff) + new Vector2(1e-4f, 0));
        float mo = (other.Car.Physics?.Mass ?? 1300) * other.MassRatio, mm = (me.Car.Physics?.Mass ?? 1300) * me.MassRatio;
        bool physical = Impulse(me, -toOther, ContactPoint(me, toOther), closing, mo);
        physical &= Impulse(other, toOther, ContactPoint(other, -toOther), closing, mm);

        if (sideBySide)
        {
            float impact = closing * 3.6f + 3;
            float bounce = 0.6f + 0.02f * impact;
            if (!physical)
            {
                me.LateralSpeed = -MathF.Sign(dOff) * bounce;
                other.LateralSpeed = MathF.Sign(dOff) * bounce;
                me.Speed *= 0.992f;
                other.Speed *= 0.992f;
            }
            bool otherIsRight = (dOff > 0) == Line.RightIsPlus;
            AddDamage(me, otherIsRight ? 3 : 2, impact);
            AddDamage(other, otherIsRight ? 2 : 3, impact);
        }
        else
        {
            // me behind, other in front
            float impact = closing * 3.6f + 2;
            if (!physical) other.Speed = MathF.Min(other.Speed + MathF.Min(1.5f, impact / 20f), MathF.Max(other.Speed, me.Speed));
            AddDamage(me, 0, impact);
            AddDamage(other, 1, impact);
        }
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
