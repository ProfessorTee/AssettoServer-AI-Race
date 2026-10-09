namespace BotDriverPlugin.Core;

/// <summary>
/// Racecraft around the fight itself: where to try again after a failed attack, the dirty air behind another car, saving the
/// tyres when alone, remembering who hit you, and the incident log for the admin page.
/// </summary>
public sealed partial class RaceWorld
{
    /// <summary>Something worth knowing for the admin page: (car id, other car id or -1, kind, text).</summary>
    public event Action<int, int, string, string>? Incident;

    internal void RaiseIncident(int carId, int otherId, string kind, string text) => Incident?.Invoke(carId, otherId, kind, text);

    // ------------------------------------------------------------------ the next attack after a failed one

    /// <summary>The biggest braking zone ahead (the car in front is weakest there, the dive works best): the next attack goes there.</summary>
    private void PlanAttackSpot(RaceBot me, int targetId, float myS)
    {
        const float step = 10;
        float range = MathF.Min(2500, Line.Length * 0.8f);
        int n = (int)(range / step);
        var v = new float[n];
        for (int i = 0; i < n; i++)
        {
            float s = myS + 150 + i * step;
            v[i] = me.Car.CornerLimit(Line.CurvatureAt(s), Line.VerticalCurvature[Line.IndexAt(s)], 1);
        }
        float bestDrop = 8; // m/s: less is no real braking zone
        int best = -1;
        for (int i = 1; i < n - 1; i++)
        {
            if (v[i] > v[i - 1] || v[i] > v[i + 1]) continue; // the slowest point of a corner
            float before = 0;
            for (int k = Math.Max(0, i - 40); k < i; k++) before = MathF.Max(before, v[k]);
            if (before - v[i] > bestDrop) { bestDrop = before - v[i]; best = i; }
        }
        me.AttackSpotTarget = targetId;
        me.AttackSpotSetAt = _now;
        me.AttackSpotS = best < 0 ? float.NaN : Line.WrapS(myS + 150 + best * step);
        if (best >= 0) Diag("attack spot planned");
    }

    /// <summary>Distance (m) forward to the planned attack spot, NaN without one.</summary>
    private float AttackSpotAhead(RaceBot me, float myS) => float.IsNaN(me.AttackSpotS) ? float.NaN : Line.WrapS(me.AttackSpotS - myS);

    // ------------------------------------------------------------------ dirty air, saving tyres

    private void UpdateDirtyAirAndSaving(RaceBot me, Neighbor? ahead, float aheadGap, float behindGap)
    {
        // close behind another car the wings work in its wake: less downforce in the corners, the tyres slide and get hot
        float dirty = 0;
        if (ahead is { } a && me.Car.Downforce > 0.0005f && me.Phase == BotPhase.Racing && !me.InPitLane)
        {
            float tGap = aheadGap / MathF.Max(10, me.Speed);
            float lat = MathF.Abs(a.Offset - me.Offset);
            dirty = Math.Clamp((1.2f - tGap) / 0.9f, 0, 1) * Math.Clamp(1 - (lat - 0.6f) / 1.6f, 0, 1);
        }
        me.DirtyAir += (dirty - me.DirtyAir) * MathF.Min(1, _stepDt / 0.4f);

        // alone in a long race: a little off the limit, the tyres last longer (the dive bomber never does that)
        float v = MathF.Max(15, me.Speed);
        bool may = Settings.IsRace && Settings.TyreWearRate > 0 && me.Phase == BotPhase.Racing && !me.InPitLane && me.Clone == null
                   && me.Driver.Personality.Attack < 0.9f && _now - Settings.RaceStartTime > 60 && me.RemainingLaps is > 3 and < int.MaxValue;
        float need = me.SavingTyres ? 1.8f : 2.5f; // a little hysteresis
        me.SavingTyres = may && aheadGap / v > need && behindGap / v > need;
    }

    /// <summary>Grip lost in dirty air: up to a third of the downforce, so only in fast corners.</summary>
    internal static float DirtyAirGrip(RaceBot bot)
    {
        if (bot.DirtyAir <= 0.01f) return 1;
        var car = bot.Car;
        float aero = car.Downforce * bot.Speed * bot.Speed;
        float share = aero / (car.LateralGrip * CarSpec.G + aero);
        return 1 - 0.3f * bot.DirtyAir * share;
    }

    // ------------------------------------------------------------------ form of the day

    /// <summary>
    /// Nobody drives at exactly the same level every day: a little quicker or slower for the whole race weekend (inconsistent drivers vary
    /// more), so the order isn't the same in every race. A clone drives the player's recorded laps as they are.
    /// </summary>
    public void RollDayForm(RaceBot bot)
    {
        if (bot.Clone != null) { bot.DayForm = 0; return; }
        float sigma = 0.004f + 0.02f * (1 - bot.Driver.Consistency);
        bot.DayForm = Math.Clamp((float)NextGaussian() * sigma, -2 * sigma, 2 * sigma);
    }

    // ------------------------------------------------------------------ who hit whom

    /// <summary><paramref name="me"/> was hit by <paramref name="otherId"/>: remembered for three minutes.</summary>
    internal void Grudge(RaceBot me, int otherId)
    {
        me.GrudgeId = otherId;
        me.GrudgeUntil = _now + 180;
    }

    /// <summary>
    /// Against the car that hit him: a fighter attacks and defends harder (+), a careful driver keeps away from it (-), 0 for anybody else.
    /// </summary>
    private float GrudgeBias(RaceBot me, int otherId)
    {
        if (me.GrudgeId != otherId || _now > me.GrudgeUntil || me.Clone != null) return 0;
        return me.Driver.Personality.Attack < 0.4f ? -0.3f : 0.3f;
    }

    // ------------------------------------------------------------------ for the debug telemetry

    /// <summary>What the bot is up to right now, in a few words (debug telemetry).</summary>
    internal string Intention(RaceBot b)
    {
        if (b.Phase != BotPhase.Racing) return b.Phase.ToString();
        if (b.InPitLane) return $"pit {b.Pit} {b.PitReason}";
        if (b.Mistake != MistakeKind.None) return $"mistake {b.Mistake}{(b.Mistake == MistakeKind.Spin ? " " + b.SpinPhase : "")}";
        var parts = new List<string>();
        if (b.OvertakeTargetId >= 0) parts.Add($"attack #{b.OvertakeTargetId}{(b.Diving ? " dive" : "")}{(b.AttackIsCounter ? " counter" : "")}");
        if (_now < b.DefendUntil) parts.Add("defend");
        if (b.BlockedById >= 0) parts.Add($"held up by #{b.BlockedById}");
        if (!float.IsNaN(b.AttackSpotS)) parts.Add($"attack spot {b.AttackSpotS:F0}");
        if (b.GrudgeId >= 0 && _now < b.GrudgeUntil) parts.Add($"grudge #{b.GrudgeId}");
        if (_now < b.CautiousUntil) parts.Add("cautious");
        if (b.SavingTyres) parts.Add("saving tyres");
        if (b.Weaving) parts.Add("weaving");
        if (b.PlanActive) parts.Add("line plan");
        if (b.Pit == PitPhase.Requested) parts.Add($"pit requested {b.PitReason}");
        return parts.Count == 0 ? "drive" : string.Join(", ", parts);
    }
}
