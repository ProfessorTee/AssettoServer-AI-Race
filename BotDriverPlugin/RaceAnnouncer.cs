using AssettoServer.Shared.Model;
using BotDriverPlugin.Core;
using Serilog;

namespace BotDriverPlugin;

/// <summary>
/// What the bots tell the chat: yellow flags, pit stops, overtakes between bots and players, the signal test. Only reads the race
/// (<see cref="BotRace"/>); keeps its own memory of what was already said.
/// </summary>
public sealed class RaceAnnouncer
{
    private readonly BotRace _race;
    private int _lastSignalPhase;
    private readonly Dictionary<int, double> _lastYellowBySector = new();
    private readonly Dictionary<(byte Bot, byte Player), float> _lastRelative = new();

    public RaceAnnouncer(BotRace race) => _race = race;

    private BotDriverConfiguration Config => _race.Config;
    private string T(string en, string de) => _race.T(en, de);
    private void Chat(string message) => _race.EntryCarManager.BroadcastChat(message);

    /// <summary>A new session: who was ahead of whom doesn't count any more.</summary>
    public void NewSession() => _lastRelative.Clear();

    /// <summary>Called every tick (under the race lock).</summary>
    public void Tick(RaceWorld world, double now)
    {
        AnnounceSignalTest(world, now);
        if (Config.AnnounceOvertakes && _race.SessionType == SessionType.Race && _race.RaceStarted)
            AnnounceOvertakes(world);
    }

    private void AnnounceSignalTest(RaceWorld world, double now)
    {
        int phase = world.SignalTestPhase(now);
        if (phase == _lastSignalPhase) return;
        _lastSignalPhase = phase;
        string? what = phase switch
        {
            1 => "left indicator",
            2 => "right indicator",
            3 => "hazard lights",
            4 => "headlight flash",
            5 => "brake lights",
            6 => "high beams",
            _ => null
        };
        Chat(what != null ? T($"Bot signal test: {what}", $"Bot-Licht-Test: {GermanSignal(phase)}")
            : T("Bot signal test finished", "Bot-Licht-Test beendet"));
    }

    private static string GermanSignal(int phase) => phase switch
    {
        1 => "Blinker links", 2 => "Blinker rechts", 3 => "Warnblinker", 4 => "Lichthupe", 5 => "Bremslicht", 6 => "Fernlicht", _ => ""
    };

    public void OnYellowFlag(YellowFlagEvent e)
    {
        var track = _race.Track;
        if (!Config.YellowFlagChat || track == null) return;
        if (_race.SessionType == SessionType.Race && !_race.RaceStarted) return;
        int sector = track.Map.SectorAt(e.S);
        double now = _race.Now;
        // one message per sector every 15 s is enough when several cars are involved
        if (_lastYellowBySector.TryGetValue(sector, out var last) && now - last < 15) return;
        _lastYellowBySector[sector] = now;

        string name = e.IsBot && _race.SlotsBySessionId.TryGetValue((byte)e.CarId, out var slot) ? slot.Bot.Name
            : _race.EntryCarManager.EntryCars.ElementAtOrDefault(e.CarId)?.Client?.Name ?? "?";
        var section = track.Info.SectionAt(track.Line.WrapS(e.S - track.StartLineS) / track.Line.Length);
        string where = section != null ? $" ({section})" : "";
        string en = e.Kind switch { "spin" => $"{name} spun", "crash" => $"{name} crashed", _ => $"{name} stopped on track" };
        string de = e.Kind switch { "spin" => $"{name} hat sich gedreht", "crash" => $"Unfall: {name}", _ => $"{name} steht auf der Strecke" };
        Chat(T($"Yellow flag in sector {sector}{where}: {en}", $"Gelbe Fahne in Sektor {sector}{where}: {de}"));
        Log.Information("BotDriver: yellow flag sector {Sector}{Where}: {What}", sector, where, en);
    }

    public void OnBotPitStop(RaceBot bot, float seconds, float litres, bool tyres)
    {
        string compound = RaceWorld.CompoundName(bot);
        string what = string.Join(" + ", new[] { tyres ? (compound != "" ? $"tyres ({compound})" : "tyres") : null, litres >= 0.5f ? $"{litres:F0} l" : null, bot.PitRepair ? "repair" : null }.Where(x => x != null));
        Log.Information("BotDriver: {Name} pit stop ({Reason}): {What}, {Seconds:F1} s", bot.Name, bot.PitReason, what, seconds);
        if (Config.AnnouncePitStops && _race.SessionType == SessionType.Race)
            Chat(T($"{bot.Name} pit stop: {what} ({seconds:F1} s)",
                $"{bot.Name} Boxenstopp: {what.Replace("tyres", "Reifen").Replace("repair", "Reparatur")} ({seconds:F1} s)"));
    }

    private void AnnounceOvertakes(RaceWorld world)
    {
        var line = world.Line;
        var track = _race.Track!;
        foreach (var slot in _race.Slots)
        {
            if (!slot.Active || slot.Bot.Phase != BotPhase.Racing) continue;
            float botS = line.WrapS((float)slot.Bot.Distance);
            foreach (var ext in world.Externals)
            {
                if (!ext.Valid) continue;
                float rel = line.Delta(ext.S, botS); // > 0: bot ahead of player
                var key = (slot.EntryCar.SessionId, (byte)ext.Id);
                // only clear positions count (6 m ahead / behind), so side-by-side battles don't spam the chat
                if (MathF.Abs(rel) > 60)
                {
                    _lastRelative.Remove(key);
                    continue;
                }
                if (MathF.Abs(rel) < 6) continue;
                if (_lastRelative.TryGetValue(key, out var old) && MathF.Sign(old) != MathF.Sign(rel))
                {
                    var player = _race.EntryCarManager.EntryCars[ext.Id].Client;
                    var section = track.Info.SectionAt(line.WrapS(botS - track.StartLineS) / line.Length);
                    if (player != null)
                    {
                        string where = section != null ? T($" at {section}", $" bei {section}") : "";
                        var (a, b) = rel > 0 ? (slot.Bot.Name, player.Name) : (player.Name, slot.Bot.Name);
                        Chat(T($"{a} overtook {b}{where}", $"{a} überholt {b}{where}"));
                    }
                }
                _lastRelative[key] = rel;
            }
        }
    }
}
