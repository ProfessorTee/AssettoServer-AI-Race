using AssettoServer.Shared.Network.Packets.Outgoing;
using BotDriverPlugin.Core;
using Serilog;

namespace BotDriverPlugin;

/// <summary>Admin controls: number of bots, safety car, debug logging.</summary>
public sealed partial class BotDriverService
{
    private int? _botLimit;
    private double _nextDebug;

    public bool DebugOn => _race.Debug;

    /// <summary>At most <paramref name="limit"/> bots (null = all). Bots over the limit leave the track, their cars stay free for players.</summary>
    public string SetBotLimit(int? limit)
    {
        lock (_lock)
        {
            if (_world == null) return T("Bots are not active.", "Bots sind nicht aktiv.");
            _botLimit = limit is < 0 ? null : limit;
            _configWriter.Set("MaxBots", _botLimit ?? -1);
            var (on, off) = ApplyBotLimit();
            Log.Information("BotDriver: bot limit {Limit}: {On} bots on track, {Off} switched off", _botLimit?.ToString() ?? "all", on, off);
            return _botLimit == null
                ? T($"BotDriver: all bots on ({on}).", $"BotDriver: alle Bots an ({on}).")
                : T($"BotDriver: {on} bots on track, {off} switched off.", $"BotDriver: {on} Bots auf der Strecke, {off} ausgeschaltet.");
        }
    }

    private (int On, int Off) ApplyBotLimit()
    {
        int allowed = _botLimit ?? int.MaxValue, on = 0, off = 0;
        // the first slots stay
        foreach (var slot in _slots.OrderBy(s => s.EntryCar.SessionId))
        {
            if (slot.EntryCar.Client != null) continue; // a player has this car
            if (on < allowed)
            {
                if (slot.Benched || !slot.Active)
                {
                    slot.Benched = false;
                    TakeSlot(slot, broadcast: true);
                    _grid.PlaceForCurrentSession(slot);
                }
                on++;
            }
            else
            {
                if (slot.Active)
                {
                    ReleaseSlot(slot);
                    _grid.Unqueue(slot.Bot);
                    _entryCarManager.BroadcastPacket(new CarDisconnected { SessionId = slot.EntryCar.SessionId });
                }
                slot.Benched = true;
                off++;
            }
        }
        return (on, off);
    }

    /// <summary>Debug logging on/off; optionally a detailed trace (four times a second) of one bot, by name or car number.</summary>
    public string SetDebug(bool on, string? bot)
    {
        lock (_lock)
        {
            _race.Debug = on;
            EndTelemetryFile();
            if (_world == null) return on ? "BotDriver debug on" : "BotDriver debug off";
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
            Log.Information("BotDriver: debug {State}{Traced}", on ? "on" : "off", traced);
            return $"BotDriver debug {(on ? "on" : "off")}{traced}";
        }
    }

    /// <summary>Every 10 s one line per bot (debug only).</summary>
    private void DebugTick(RaceWorld world, double now)
    {
        if (!DebugOn || now < _nextDebug) return;
        _nextDebug = now + 10;
        Log.Information("BotDriver debug: session {Session}, race started {Started}, bot limit {Limit}, {Players} players",
            _sessionType, _raceStarted, _botLimit?.ToString() ?? "all", _entryCarManager.ConnectedCars.Count);
        foreach (var slot in _slots)
        {
            var b = slot.Bot;
            Log.Information("BotDriver debug: #{Id} {Name}{State} phase {Phase} pit {Pit} lap {Lap} s {S:F0} v {V:F0}/{T:F0} km/h off {Off:F1}->{Tgt:F1} " +
                            "gear {Gear} tyres {Tf:F0}/{Tr:F0} °C fuel {Fuel:F1} level {Level:P1} contacts {Contacts}",
                slot.EntryCar.SessionId, b.Name, slot.Benched ? " (off)" : slot.Active ? "" : " (player)", b.Phase, b.Pit, b.LapsCompleted,
                world.Line.WrapS((float)b.Distance), b.Speed * 3.6f, b.TargetSpeed * 3.6f, b.Offset, b.TargetOffset, b.Gear, b.TyreTempFront, b.TyreTempRear,
                b.Fuel, b.Driver.Pace, b.ContactCount);
        }
    }

    private readonly Dictionary<int, double> _headingLogged = new();

    /// <summary>Debug: a bot shown more than 30° off the track's direction (at most every 5 s per bot).</summary>
    private void CheckHeading(BotSlot slot, in BotPose pose, double now)
    {
        var bot = slot.Bot;
        float deg = _world!.HeadingError(bot, pose);
        if (deg < 30 || (_headingLogged.TryGetValue(bot.Id, out var at) && now - at < 5)) return;
        _headingLogged[bot.Id] = now;
        Log.Warning("BotDriver debug: {Name} shown {Deg:F0}° off the track's direction ({Phase}, {Speed:F1} m/s, sideways {Lat:F1} m/s, yaw {Yaw:F2}, offset {Offset:F1} m)",
            bot.Name, deg, bot.Phase, bot.Speed, bot.LateralSpeed, bot.Yaw, bot.Offset);
    }
}
