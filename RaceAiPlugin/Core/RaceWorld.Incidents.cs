namespace RaceAiPlugin.Core;

/// <summary>A car spun, crashed or is standing on the track (for the yellow flag message).</summary>
public readonly record struct YellowFlagEvent(int CarId, bool IsBot, float S, string Kind);

/// <summary>Yellow flags and getting stuck cars going again (clusters, emergency ghosting).</summary>
public sealed partial class RaceWorld
{
    public event Action<YellowFlagEvent>? YellowFlag;

    private readonly Dictionary<int, double> _slowSince = new();
    private readonly HashSet<int> _yellowAnnounced = [];

    internal void RaiseYellow(int id, bool isBot, float s, string kind)
    {
        if (!_yellowAnnounced.Add(id)) return;
        YellowFlag?.Invoke(new YellowFlagEvent(id, isBot, s, kind));
    }

    /// <summary>Cars (bots and players) standing on the track for a few seconds get a yellow flag message once.</summary>
    private void WatchStoppedCars()
    {
        if (_now - Settings.RaceStartTime < 20) return;
        foreach (var n in _neighbors)
        {
            bool onTrack = n.IsBot ? n.Bot!.Phase == BotPhase.Racing && !n.Bot.InPitLane : !IsInPitLane(n.External!);
            if (!onTrack || n.Speed > 15)
            {
                _slowSince.Remove(n.Id);
                if (n.Speed > 15) _yellowAnnounced.Remove(n.Id);
                continue;
            }
            if (n.Speed > 3) { _slowSince.Remove(n.Id); continue; }
            if (!_slowSince.TryGetValue(n.Id, out var since)) _slowSince[n.Id] = _now;
            else if (_now - since > 3) RaiseYellow(n.Id, n.IsBot, n.S, "stopped");
        }
    }

    // ------------------------------------------------------------------ stuck cars

    /// <summary>Stopped this long next to other cars: the cluster gets sorted out (front car first, the others go round).</summary>
    public float UnstuckAfter { get; set; } = 4f;
    /// <summary>Emergency: stopped this long: the bot is a ghost for a moment (no collisions with anybody), 0 = never.</summary>
    public float GhostAfter { get; set; } = 25f;
    public float GhostDuration { get; set; } = 8f;

    private void UpdateStuck(RaceBot bot)
    {
        if (bot.Phase != BotPhase.Racing || bot.InPitLane || bot.Mistake == MistakeKind.Spin)
        {
            bot.Unstuck = false;
            return;
        }
        double stuck = double.IsNaN(bot.StoppedSince) ? 0 : _now - bot.StoppedSince;
        if (bot.Unstuck && (bot.Speed > 8 || stuck == 0 && bot.Speed > 4)) bot.Unstuck = false;
        if (!bot.Unstuck && stuck > UnstuckAfter && CarNearby(bot, 15)) bot.Unstuck = true;

        if (GhostAfter > 0 && stuck > GhostAfter && _now >= bot.GhostUntil)
        {
            bot.GhostUntil = _now + GhostDuration;
            bot.IgnorePlayersUntil = bot.GhostUntil;
            bot.GhostCount++;
        }
        // don't come back to life inside another car
        if (bot.GhostUntil > _now && bot.GhostUntil - _now < _stepDt * 2 && CarNearby(bot, 0, overlapOnly: true))
        {
            bot.GhostUntil += 1;
            bot.IgnorePlayersUntil = bot.GhostUntil;
        }
    }

    private bool CarNearby(RaceBot me, float distance, bool overlapOnly = false)
    {
        float myS = Line.WrapS((float)me.Distance);
        foreach (var o in _neighbors)
        {
            if (o.IsBot && o.Id == me.Id) continue;
            float ds = MathF.Abs(Line.Delta(myS, o.S));
            if (overlapOnly)
            {
                if (ds < (me.Car.Length + o.Length) / 2 && MathF.Abs(o.Offset - me.Offset) < (me.Car.Width + o.Width) / 2) return true;
            }
            else if (ds < distance + (me.Car.Length + o.Length) / 2) return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="me"/> should take <paramref name="o"/> into account right now (ghosts, stuck clusters).</summary>
    private bool Considers(RaceBot me, in Neighbor o, float ds)
    {
        if (me.GhostUntil > _now) return false;
        if (o.IsBot && o.Bot!.GhostUntil > _now) return false;
        if (me.Unstuck)
        {
            // in a cluster the car in front goes first: forget the ones behind; level cars: the lower id goes
            if (ds < -0.5f) return false;
            if (MathF.Abs(ds) <= 0.5f && me.Id < o.Id) return false;
        }
        return true;
    }

    /// <summary>Stuck behind a standing car: pick a free side and creep round it.</summary>
    private bool UnstuckAround(RaceBot me, in Neighbor a, float myS, float minOff, float maxOff, ref float vTarget)
    {
        if (!me.Unstuck || a.Speed > 2) return false;
        float latClear = (me.Car.Width + a.Width) / 2 + 0.3f;
        float plus = a.Offset + latClear, minus = a.Offset - latClear;
        float lo = minOff - Settings.EdgeMargin, hi = maxOff + Settings.EdgeMargin;
        bool plusOk = plus <= hi && LaneFree(me, plus, myS, -2, 20, a.Id);
        bool minusOk = minus >= lo && LaneFree(me, minus, myS, -2, 20, a.Id);
        if (!plusOk && !minusOk) return false;
        float side = plusOk && (!minusOk || MathF.Abs(plus - me.Offset) < MathF.Abs(minus - me.Offset)) ? plus : minus;
        me.TargetOffset = side;
        bool clear = MathF.Abs(me.Offset - a.Offset) > latClear - 0.3f;
        vTarget = MathF.Min(vTarget, clear ? 10f : 2.5f); // creep sideways first, then go
        return true;
    }
}
