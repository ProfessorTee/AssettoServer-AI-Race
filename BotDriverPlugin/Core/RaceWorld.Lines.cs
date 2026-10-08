namespace BotDriverPlugin.Core;

public enum CornerLineKind
{
    Clean,
    /// <summary>Misses the apex: stays too far outside through the middle of the corner.</summary>
    Wide,
    /// <summary>Turns in too early: clips the inside early and runs wide on the exit.</summary>
    EarlyApex,
    /// <summary>Turns in too late (careful): wide entry, tight exit.</summary>
    LateApex
}

/// <summary>
/// How a driver gets through each single corner. Amateurs don't drive every corner a little slower: some corners
/// they get right, in others they brake too early, miss the apex, turn in too early and run wide on the exit,
/// hesitate with the throttle or lift in the middle of the corner. A plan is rolled once per corner.
/// </summary>
public sealed partial class RaceWorld
{
    private const float CornerMinCurvature = 1 / 220f;

    /// <summary>0..1: how imprecise the driver is right now (same inputs as the mistakes, so the strength calibration covers it).</summary>
    private float Imprecision(RaceBot me)
        => Settings.LineErrors ? Math.Clamp(ErrorLevel(me) * me.Driver.Personality.LineErrors, 0, 1) : 0;

    /// <summary>Finds the next corner that starts within <paramref name="range"/> metres ahead.</summary>
    private bool FindCorner(float fromS, float range, out float startS, out float apexS, out float endS, out float sign)
    {
        startS = apexS = endS = sign = 0;
        float step = MathF.Max(Line.Spacing, 3f);
        float K(float s) => (Line.CurvatureAt(s - step) + Line.CurvatureAt(s) + Line.CurvatureAt(s + step)) / 3;
        float d = 0;
        while (d < range && MathF.Abs(K(fromS + d)) < CornerMinCurvature) d += step;
        if (d >= range) return false;

        sign = MathF.Sign(K(fromS + d));
        float peak = 0, peakD = d, e = d;
        while (e < d + 600)
        {
            float k = K(fromS + e) * sign;
            if (k > peak) { peak = k; peakD = e; }
            if (k < MathF.Max(CornerMinCurvature * 0.6f, peak * 0.35f)) break;
            e += step;
        }
        // back to where the corner really starts (a third of the peak)
        float b = d;
        while (b > d - 120 && K(fromS + b - step) * sign > peak * 0.33f) b -= step;
        startS = Line.WrapS(fromS + b - 15);
        apexS = Line.WrapS(fromS + peakD);
        endS = Line.WrapS(fromS + e + 25);
        return true;
    }

    /// <summary>Rolls how the next corner will be driven (called every Think).</summary>
    private void UpdateCornerPlan(RaceBot me, float myS)
    {
        if (me.Phase != BotPhase.Racing || me.InPitLane)
        {
            me.PlanActive = false;
            return;
        }
        if (me.PlanActive && Line.Delta(me.PlanEndS, myS) < 0) return; // still in (or before) the planned corner

        float v = MathF.Max(me.Speed, 20);
        // far enough ahead that the plan (with its early braking point) is known before the braking starts
        float range = v * v / (2 * 6f) + 150;
        me.PlanActive = false;
        if (!FindCorner(myS, range, out float start, out float apex, out float end, out float sign)) return;
        // just left a corner: the same one again is not a new corner
        if (me.PlanRolled && MathF.Abs(Line.Delta(me.PlanApexS, apex)) < 20) return;
        me.PlanRolled = true;

        me.PlanActive = true;
        me.PlanStartS = start;
        me.PlanApexS = apex;
        me.PlanEndS = end;
        me.PlanSign = sign;
        me.PlanApexPassedAt = double.NaN;
        me.PlanKind = CornerLineKind.Clean;
        me.PlanAmp = 0;
        me.PlanPace = 1;
        me.PlanBrake = 1;
        me.PlanLift = false;

        if (me.Clone != null)
        {
            // a clone drives the player's recorded line and speeds: his own habits instead of generated errors;
            // corner by corner a bit left or right of his average line, as much as his laps differ
            me.PlanBrakeMargin = 0;
            me.PlanExitDelay = 0;
            me.CloneZ = Math.Clamp((float)NextGaussian() * 0.8f, -2f, 2f);
            return;
        }

        // every driver below the limit: brakes a bit too early here, waits a moment before full throttle there
        float skill = me.Driver.Pace + me.PaceNoise + me.PaceBoost + me.DayForm;
        me.PlanBrakeMargin = DriverProfile.BrakeMargin(skill) * (0.3f + 1.4f * _rng.NextSingle())
                             + 6f * MathF.Max(0, -me.Driver.Personality.BrakeBehavior) * _rng.NextSingle();
        me.PlanExitDelay = DriverProfile.ExitHesitation(skill) * (0.2f + 1.6f * _rng.NextSingle());
        // greedy drivers are on the throttle earlier (and sometimes too early, see the corner exit mistakes)
        me.PlanExitDelay *= 1.3f - 0.8f * Math.Clamp(me.Driver.Personality.ExitGreed, 0, 1);

        float ip = Imprecision(me);
        if (ip <= 0.001f || !MistakesAllowed(me)) return;

        // the line
        float pErr = 0.85f * MathF.Pow(ip, 0.7f);
        if (_rng.NextSingle() < pErr)
        {
            float r = _rng.NextSingle();
            me.PlanKind = r < 0.45f ? CornerLineKind.Wide : r < 0.8f ? CornerLineKind.EarlyApex : CornerLineKind.LateApex;
            float len = Line.Delta(start, end);
            me.PlanAmp = MathF.Min(0.35f + 1.9f * ip * (0.3f + 0.7f * _rng.NextSingle()), 0.05f * len + 0.4f);
        }

        // how brave through this corner: most corners a little below the driver's best, some clearly too careful
        float g = MathF.Abs((float)NextGaussian());
        me.PlanPace = 1 - ip * MathF.Min(0.12f, 0.035f * g);
        if (_rng.NextSingle() < 0.18f * ip) me.PlanPace -= 0.04f + 0.06f * _rng.NextSingle();

        // braking: amateurs mostly brake too early and too softly, rarely a bit too late (the big ones are LateBrake mistakes)
        float br = _rng.NextSingle();
        me.PlanBrake = br < 0.12f * ip ? 1 + 0.06f * ip : 1 - ip * 0.15f * _rng.NextSingle();
        me.PlanBrakeMargin += ip * 30f * _rng.NextSingle() * _rng.NextSingle();

        // exit: waits for the car to settle before full throttle, or lifts once in the middle of the corner
        if (_rng.NextSingle() < 0.5f * ip) me.PlanExitDelay += ip * (0.2f + 0.6f * _rng.NextSingle());
        me.PlanLift = _rng.NextSingle() < 0.22f * ip;
        me.PlanLiftAt = double.NaN;
    }

    private static float Bell(float u, float center, float halfWidth)
    {
        float x = (u - center) / halfWidth;
        if (x <= -1 || x >= 1) return 0;
        float c = MathF.Cos(x * MathF.PI / 2);
        return c * c;
    }

    /// <summary>Lateral deviation from the intended line at <paramref name="s"/> caused by the corner plan (+ = towards +offset).</summary>
    internal float CornerShift(RaceBot me, float s)
    {
        if (!me.PlanActive || me.PlanKind == CornerLineKind.Clean) return 0;
        float len = Line.Delta(me.PlanStartS, me.PlanEndS);
        if (len < 10) return 0;
        float u = Line.Delta(me.PlanStartS, s) / len;
        if (u <= 0 || u >= 1) return 0;
        // u of the apex in the corner
        float ua = Math.Clamp(Line.Delta(me.PlanStartS, me.PlanApexS) / len, 0.2f, 0.8f);
        float inside = me.PlanSign, a = me.PlanAmp;
        return me.PlanKind switch
        {
            CornerLineKind.Wide => -inside * a * Bell(u, ua, MathF.Min(ua, 1 - ua)),
            CornerLineKind.EarlyApex => inside * a * (0.6f * Bell(u, ua * 0.6f, ua * 0.6f) - 1.1f * Bell(u, (ua + 1) / 2, (1 - ua) * 0.75f)),
            CornerLineKind.LateApex => inside * a * (-0.8f * Bell(u, ua * 0.5f, ua * 0.5f) + 0.5f * Bell(u, (ua + 1) / 2, (1 - ua) * 0.6f)),
            _ => 0
        };
    }

    /// <summary>Extra curvature of the path from the plan's line (second derivative of the shift).</summary>
    private float CornerShiftCurvature(RaceBot me, float s)
    {
        if (!me.PlanActive || me.PlanKind == CornerLineKind.Clean) return 0;
        const float h = 6f;
        return (CornerShift(me, s + h) - 2 * CornerShift(me, s) + CornerShift(me, s - h)) / (h * h);
    }

    private bool InPlannedCorner(RaceBot me, float s)
        => me.PlanActive && Line.Delta(me.PlanStartS, s) >= 0 && Line.Delta(s, me.PlanEndS) >= 0;

    /// <summary>Speed caps from the plan on the way out of the corner (end of Think).</summary>
    private void CornerExecution(RaceBot me, float myS, ref float vTarget)
    {
        if (!me.PlanActive || me.Mistake != MistakeKind.None) return;
        float toApex = Line.Delta(myS, me.PlanApexS);
        if (double.IsNaN(me.PlanApexPassedAt) && toApex <= 0) me.PlanApexPassedAt = _now;

        // a lift in the middle of the corner: the car felt nervous
        if (me.PlanLift && toApex < 12 && me.Speed > 10)
        {
            if (double.IsNaN(me.PlanLiftAt))
            {
                me.PlanLiftAt = _now;
                me.PlanLiftSpeed = me.Speed * 0.95f;
            }
            if (_now - me.PlanLiftAt < 0.45)
            {
                vTarget = MathF.Min(vTarget, me.PlanLiftSpeed);
                me.LiftOnlyUntil = _now + 0.1;
            }
            else me.PlanLift = false;
        }

        // hesitating on the throttle after the apex: holds the speed for a moment
        if (me.PlanExitDelay > 0 && !double.IsNaN(me.PlanApexPassedAt) && _now - me.PlanApexPassedAt < me.PlanExitDelay)
        {
            if (vTarget > me.Speed) me.LiftOnlyUntil = _now + 0.1;
            vTarget = MathF.Min(vTarget, me.Speed + 0.15f);
        }
    }
}
