using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Outgoing;
using BotDriverPlugin.Core;
using Serilog;

namespace BotDriverPlugin;

/// <summary>
/// A player loses the connection during a race: a bot takes his car over where he was (same lap, place and speed) and drives it to
/// the flag, so the race goes on without a hole. With recordings of the player (DriverRecorder) it drives like him, else as a normal
/// bot as fast as his best lap of the session. He doesn't get the car back (CSP can't hand a running car over to a player).
/// </summary>
public sealed class CarTakeover
{
    private readonly BotRace _race;
    private readonly FieldStrength _field;
    private readonly Func<CloneLibrary?> _clones;

    public CarTakeover(BotRace race, FieldStrength field, Func<CloneLibrary?> clones)
    {
        _race = race;
        _field = field;
        _clones = clones;
    }

    private BotDriverConfiguration Config => _race.Config;

    /// <summary>The player of <paramref name="client"/> just left: a bot takes his car if he was racing. Under the race lock.</summary>
    public BotSlot? TryTakeOver(ACTcpClient client)
    {
        var world = _race.World;
        var track = _race.Track;
        if (world == null || track == null || !Config.TakeOverDisconnectedPlayers) return null;
        if (_race.SessionType != SessionType.Race || !_race.RaceStarted) return null;
        var car = client.EntryCar;
        if (_race.SlotsBySessionId.ContainsKey(car.SessionId)) return null; // a bot slot: the bot comes back anyway
        var session = _race.SessionManager.CurrentSession;
        if (session.Results == null || !session.Results.TryGetValue(car.SessionId, out var result) || result.HasCompletedLastLap) return null;
        var ext = world.Externals.FirstOrDefault(e => e.Id == car.SessionId);
        if (ext is not { Valid: true } || MathF.Abs(ext.Offset) > 15) return null; // not on the track (pits, off somewhere)

        // his car: the bots' data of that model (calibrated at the start); a model no bot drives can only be taken over by his clone
        var key = (car.Model, car.Ballast, car.Restrictor);
        if (!_race.SpecCache.TryGetValue(key, out var spec))
        {
            var root = _race.CarRoots.FirstOrDefault(r => Directory.Exists(Path.Join(r, car.Model))) ?? _race.CarRoots.FirstOrDefault() ?? "content/cars";
            spec = CarDataLoader.LoadForTrack(root, car.Model, track.Line, car.Ballast, car.Restrictor, msg => Log.Warning("BotDriver: {Message}", msg));
            _race.SpecCache[key] = spec;
        }
        var profile = _clones()?.Cached(client.Guid.ToString(), car.Model); // built when he joined (no disk access under the lock)
        if (profile == null && !_field.HasCalibration(spec))
        {
            Log.Information("BotDriver: {Player} left, no bot drives a {Car} here, the car stays empty", client.Name, car.Model);
            return null;
        }

        string playerName = client.Name ?? profile?.PlayerName ?? "?";
        // as fast as he was: his best lap of this race, else the field's strength (a clone drives his own recorded pace)
        float level = Config.AiStrength;
        if (profile == null && result.BestLap is > 10_000 and < 999_999_999u)
            level = Math.Clamp(100f * (_field.ReferenceBestLap ?? _field.Calibration(spec).BestLap) / (result.BestLap / 1000f), 60, 105);
        var bot = new RaceBot
        {
            Id = car.SessionId,
            Name = playerName + Config.TakeoverNameSuffix,
            Car = spec,
            Driver = profile != null
                ? new DriverProfile { Pace = 1, Aggression = Math.Clamp(Config.AiAggression / 100f, 0, 1), Consistency = 0.97f }
                : DriverProfile.FromStrength(level, 0, Config.AiAggression),
            Clone = profile
        };
        if (profile != null) _field.ApplyClone(bot, profile);
        else _field.ApplyStrength(bot, level, _field.Calibration(spec));
        world.Bots.Add(bot);
        if (track.Info.PitBoxes.FindIndex(p => p.Index == car.SessionId) is var bi and >= 0)
            world.SetPitBox(bot, track.Info.PitBoxes[bi].Position);

        // where he was: same lap, same place, same speed
        var line = track.Line;
        int laps = (int)result.NumLaps;
        world.ResetCarCondition(bot, world.FuelForLaps(bot, RemainingLaps(bot, session) + 0.5f));
        float fromStart = line.WrapS(ext.S - track.StartLineS);
        world.PlaceAt(bot, track.StartLineS + (double)laps * line.Length + fromStart, ext.Offset, BotPhase.Racing);
        bot.Speed = ext.Speed;
        bot.LapsCompleted = laps;
        bot.LapIndex = laps;
        bot.StartCrossed = true;
        bot.TimingValid = true;
        bot.RaceStartTime = world.Settings.RaceStartTime;
        float lapTime = profile?.AverageLap ?? _field.TargetLap(bot) ?? 120;
        bot.LapStartTime = _race.Now - fromStart / line.Length * lapTime;
        bot.CautiousUntil = _race.Now + 1;

        var slot = new BotSlot(car, bot, "") { TakeoverGuid = client.Guid, TakeoverPlayer = playerName };
        _race.Slots.Add(slot);
        _race.SlotsBySessionId[car.SessionId] = slot;
        car.AiName = playerName; // the results keep his name
        car.ExternalAiController = slot;
        car.AiControlled = true;
        slot.Active = true;
        _race.EntryCarManager.BroadcastPacket(new CarConnected { SessionId = car.SessionId, Name = bot.Name, Nation = client.NationCode ?? "" });
        _race.EntryCarManager.BroadcastChat(_race.T($"{playerName} lost the connection, a bot drives his car to the flag (lap {laps + 1})",
            $"{playerName} hat die Verbindung verloren, ein Bot fährt sein Auto ins Ziel (Runde {laps + 1})"));
        Log.Information("BotDriver: {Player} disconnected in lap {Lap}, a bot ({How}) drives his {Car} on",
            playerName, laps + 1, profile != null ? $"his clone, {profile.LapsUsed} recorded laps" : $"{bot.Driver.Level:F1} %", car.Model);
        return slot;
    }

    private static int RemainingLaps(RaceBot bot, SessionState session)
    {
        var cfg = session.Configuration;
        if (!cfg.IsTimedRace) return Math.Max(1, cfg.Laps - bot.LapsCompleted);
        double timeLeft = session.TimeLeftMilliseconds / 1000.0;
        return Math.Max(1, (int)Math.Ceiling(timeLeft / Math.Max(60, bot.Clone?.AverageLap ?? 480)) + 1);
    }

    /// <summary>The race is over (or a player joins the car): the bot leaves the car. Under the race lock.</summary>
    public void End(BotSlot slot)
    {
        slot.Active = false;
        slot.EntryCar.ExternalAiController = null;
        slot.EntryCar.AiControlled = false;
        slot.EntryCar.AiName = null;
        _race.World?.Bots.Remove(slot.Bot);
        _race.Slots.Remove(slot);
        _race.SlotsBySessionId.Remove(slot.EntryCar.SessionId);
        if (slot.EntryCar.Client == null)
            _race.EntryCarManager.BroadcastPacket(new CarDisconnected { SessionId = slot.EntryCar.SessionId });
        Log.Information("BotDriver: the bot left {Player}'s car {Slot}", slot.TakeoverPlayer, slot.EntryCar.SessionId);
    }

    /// <summary>New session: every taken-over car is empty again.</summary>
    public void EndAll()
    {
        foreach (var slot in _race.Slots.Where(s => s.TakeoverGuid != null).ToList()) End(slot);
    }
}
