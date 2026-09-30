using AssettoServer.Shared.Network.Packets.Outgoing;
using RaceAiPlugin.Core;
using Serilog;

namespace RaceAiPlugin;

/// <summary>Admin controls: number of bots, safety car, debug logging.</summary>
public sealed partial class RaceAiService
{
    private int? _botLimit;
    private bool _debug;
    private double _nextDebug;

    public bool DebugOn => _debug || _config.Debug;

    /// <summary>At most <paramref name="limit"/> bots (null = all). Bots over the limit leave the track, their cars stay free for players.</summary>
    public string SetBotLimit(int? limit)
    {
        lock (_lock)
        {
            if (_world == null) return T("Race AI is not active.", "Race AI ist nicht aktiv.");
            _botLimit = limit is < 0 ? null : limit;
            var (on, off) = ApplyBotLimit();
            Log.Information("Race AI: bot limit {Limit}: {On} bots on track, {Off} switched off", _botLimit?.ToString() ?? "all", on, off);
            return _botLimit == null
                ? T($"Race AI: all bots on ({on}).", $"Race AI: alle Bots an ({on}).")
                : T($"Race AI: {on} bots on track, {off} switched off.", $"Race AI: {on} Bots auf der Strecke, {off} ausgeschaltet.");
        }
    }

    private (int On, int Off) ApplyBotLimit()
    {
        int allowed = _botLimit ?? int.MaxValue, on = 0, off = 0;
        // the clones of players always drive; of the normal bots the first slots stay
        foreach (var slot in _slots.Where(s => s.TakeoverGuid == null).OrderBy(s => s.EntryCar.SessionId))
        {
            if (slot.EntryCar.Client != null) continue; // a player has this car
            if (on < allowed)
            {
                if (slot.Benched || !slot.Active)
                {
                    slot.Benched = false;
                    TakeSlot(slot, broadcast: true);
                    PlaceForCurrentSession(slot, late: true);
                }
                on++;
            }
            else
            {
                if (slot.Active)
                {
                    ReleaseSlot(slot);
                    _pitQueue.Remove(slot.Bot);
                    _entryCarManager.BroadcastPacket(new CarDisconnected { SessionId = slot.EntryCar.SessionId });
                }
                slot.Benched = true;
                off++;
            }
        }
        return (on, off);
    }

    /// <summary>Safety car on/off: slow, in order, weaving to keep the tyres warm.</summary>
    public string SetSafetyCar(bool on, float? kmh)
    {
        lock (_lock)
        {
            if (_world == null) return T("Race AI is not active.", "Race AI ist nicht aktiv.");
            if (kmh is > 30 and < 300) _world.Settings.SafetyCarSpeed = kmh.Value / 3.6f;
            _world.Settings.SafetyCar = on;
            float v = _world.Settings.SafetyCarSpeed * 3.6f;
            Log.Information("Race AI: safety car {State} ({Speed:F0} km/h)", on ? "on" : "off", v);
            _entryCarManager.BroadcastChat(on
                ? T($"SAFETY CAR: the bots slow down to {v:F0} km/h and don't overtake.", $"SAFETY CAR: die Bots fahren langsam ({v:F0} km/h) und überholen nicht.")
                : T("Safety car in: green flag, racing again!", "Safety Car kommt rein: grüne Flagge, es wird wieder gefahren!"));
            return on ? T($"Safety car on ({v:F0} km/h).", $"Safety Car an ({v:F0} km/h).") : T("Safety car off.", "Safety Car aus.");
        }
    }

    public bool SafetyCarOn => _world?.Settings.SafetyCar ?? false;

    /// <summary>Debug logging on/off; optionally a detailed trace (twice a second) of one bot, by name or car number.</summary>
    public string SetDebug(bool on, string? bot)
    {
        lock (_lock)
        {
            _debug = on;
            if (_world == null) return on ? "Race AI debug on" : "Race AI debug off";
            _world.TraceBotId = -1;
            string traced = "";
            if (on && !string.IsNullOrWhiteSpace(bot))
            {
                var slot = _slots.FirstOrDefault(s => s.Bot.Name.Contains(bot, StringComparison.OrdinalIgnoreCase)
                                                      || s.EntryCar.SessionId.ToString() == bot.Trim());
                if (slot != null)
                {
                    _world.TraceBotId = slot.Bot.Id;
                    traced = $", trace {slot.Bot.Name}";
                }
                else traced = T($", no bot '{bot}'", $", kein Bot '{bot}'");
            }
            _nextDebug = 0;
            Log.Information("Race AI: debug {State}{Traced}", on ? "on" : "off", traced);
            return $"Race AI debug {(on ? "on" : "off")}{traced}";
        }
    }

    /// <summary>Every 10 s one line per bot (debug only).</summary>
    private void DebugTick(RaceWorld world, double now)
    {
        if (!DebugOn || now < _nextDebug) return;
        _nextDebug = now + 10;
        Log.Information("Race AI debug: session {Session}, race started {Started}, safety car {Sc}, bot limit {Limit}, {Players} players",
            _sessionType, _raceStarted, world.Settings.SafetyCar, _botLimit?.ToString() ?? "all", _entryCarManager.ConnectedCars.Count);
        foreach (var slot in _slots)
        {
            var b = slot.Bot;
            Log.Information("Race AI debug: #{Id} {Name}{State} phase {Phase} pit {Pit} lap {Lap} s {S:F0} v {V:F0}/{T:F0} km/h off {Off:F1}->{Tgt:F1} " +
                            "tyres {Tf:F0}/{Tr:F0} °C fuel {Fuel:F1} mistake {Mistake} ot {Ot} swap {Swap}",
                slot.EntryCar.SessionId, b.Name, slot.Benched ? " (off)" : slot.Active ? "" : " (player)", b.Phase, b.Pit, b.LapsCompleted,
                world.Line.WrapS((float)b.Distance), b.Speed * 3.6f, b.TargetSpeed * 3.6f, b.Offset, b.TargetOffset, b.TyreTempFront, b.TyreTempRear,
                b.Fuel, b.Mistake, b.OvertakeTargetId, slot.TakeoverGuid != null ? slot.Swap.ToString() : "-");
        }
    }
}
