using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using BotDriverPlugin.Core;
using SharedConfig;
using Serilog;

namespace BotDriverPlugin;

/// <summary>
/// Puts the bots where the session wants them: the start grid (order after qualifying, or by the grid setting), the pit boxes,
/// out of the pits one after the other in practice / qualifying, back into the session when a player leaves their car, and into the
/// box at the end of a session (with an estimated lap time for the lap they were on).
/// </summary>
public sealed class GridDirector
{
    private readonly BotRace _race;
    private readonly FieldStrength _field;
    private readonly ConfigWriter _configWriter;

    public GridDirector(BotRace race, FieldStrength field, ConfigWriter configWriter)
    {
        _race = race;
        _field = field;
        _configWriter = configWriter;
    }

    private BotDriverConfiguration _config => _race.Config;
    private ACServerConfiguration _serverConfig => _race.ServerConfig;
    private EntryCarManager _entryCarManager => _race.EntryCarManager;
    private SessionManager _sessionManager => _race.SessionManager;
    private RaceWorld? _world => _race.World;
    private TrackData? _track => _race.Track;
    private List<BotSlot> _slots => _race.Slots;
    private Dictionary<byte, BotSlot> _slotsBySessionId => _race.SlotsBySessionId;
    private Random _rng => _race.Rng;
    private SessionType _sessionType { get => _race.SessionType; set => _race.SessionType = value; }
    private bool _raceStarted { get => _race.RaceStarted; set => _race.RaceStarted = value; }
    private double Now => _race.Now;
    private static string FormatLap(float seconds) => BotRace.FormatLap(seconds);

    /// <summary>A new session: grid or pit boxes, fuel and tyres, pit window, who drives out when.</summary>
    public void SetupSession(SessionState session, SessionState? previous)
    {
        if (_world == null) return;
        _sessionType = session.Configuration.Type;
        _raceStarted = false;
        double now = Now;

        // names in the results, so leaderboards and result files show the bot names
        if (session.Results != null)
        {
            foreach (var slot in _slots.Where(s => s.Active))
            {
                if (session.Results.TryGetValue(slot.EntryCar.SessionId, out var result))
                {
                    result.Name = slot.Bot.Name;
                    result.NationCode = slot.Nation;
                }
            }
        }

        if (_sessionType == SessionType.Race)
            ArrangeGrid(session, previous);

        var grid = session.Grid?.ToList();
        foreach (var slot in _slots)
        {
            slot.GridIndex = grid?.FindIndex(c => c.SessionId == slot.EntryCar.SessionId) ?? slot.EntryCar.SessionId;
            if (slot.GridIndex < 0) slot.GridIndex = slot.EntryCar.SessionId;
        }

        // mandatory pit window (only for races over a number of laps)
        var cfg = session.Configuration;
        bool lapRace = _sessionType == SessionType.Race && !cfg.IsTimedRace;
        _world.Settings.PitWindowStart = lapRace ? _serverConfig.Server.PitWindowStart : 0;
        _world.Settings.BlueFlags = _config.BlueFlags && _sessionType == SessionType.Race;
        _world.Settings.RaceStartTime = _sessionType == SessionType.Race ? double.PositiveInfinity : double.NegativeInfinity;
        _world.Settings.PitWindowEnd = lapRace ? _serverConfig.Server.PitWindowEnd : 0;

        var driving = new List<RaceBot>();
        foreach (var slot in _slots.Where(s => s.Active))
        {
            // full service between sessions: fuel for the session, new tyres
            float fuelLaps = _sessionType switch
            {
                SessionType.Race => RemainingLaps(slot, session) + 0.5f,
                SessionType.Qualifying => _config.QualifyingFuelLaps,
                _ => _config.PracticeFuelLaps
            };
            _world.ResetCarCondition(slot.Bot, _world.FuelForLaps(slot.Bot, fuelLaps));
            ParkInPitBox(slot);
            switch (_sessionType)
            {
                case SessionType.Race:
                    PlaceOnGrid(slot);
                    break;
                case SessionType.Practice when _config.Practice == BotSessionMode.Drive:
                case SessionType.Qualifying when _config.Qualifying == BotSessionMode.Drive:
                    driving.Add(slot.Bot);
                    break;
            }
        }

        _pitQueue.Clear();
        _estimated.Clear();
        if (driving.Count > 0)
        {
            if (_config.SessionStart == BotSessionStart.Pits && _world.PitLane != null && driving.All(b => _world.PitBoxDistance(b) >= 0))
            {
                // start from the pit boxes like the players; in qualifying the players get the pit lane first
                _pitQueue.AddRange(driving.OrderBy(_ => _rng.Next()));
                _nextPitRelease = now + (_sessionType == SessionType.Qualifying ? _config.QualifyingBotDelaySeconds : _config.PracticeBotDelaySeconds);
            }
            else
            {
                _world.SpreadOnTrack(driving, now);
            }
        }

        Log.Information("BotDriver: session {Session} ({Type}), {Count} bots {Mode}", session.Configuration.Name, _sessionType,
            _slots.Count(s => s.Active), _sessionType == SessionType.Race ? "on the grid" : _pitQueue.Count > 0 ? "leaving the pits" : driving.Count > 0 ? "on track" : "parked");
    }

    /// <summary>Content Manager style starting position of the players, plus random bot order, for races that don't follow a qualifying.</summary>
    private void ArrangeGrid(SessionState session, SessionState? previous)
    {
        if (session.Grid == null) return;
        if (_config.BotGridOrder != BotGridOrder.Qualifying)
        {
            // players keep their spots (qualifying result or start position setting), the bot spots are refilled
            ArrangePlayers(session, previous);
            var list = session.Grid.ToList();
            var botCars = list.Where(IsActiveBot).ToList();
            if (botCars.Count == 0) return;
            botCars = _config.BotGridOrder == BotGridOrder.SlowestFirst
                ? botCars.OrderByDescending(ExpectedLap).ToList()
                : botCars.OrderBy(_ => _rng.Next()).ToList();
            session.Grid = MergeKeepingPlayerSpots(list, botCars);
            Log.Information("BotDriver: grid order {Order}: {Bots}", _config.BotGridOrder,
                string.Join(", ", botCars.Select(c => $"{_slotsBySessionId[c.SessionId].Bot.Name} {FormatLap(ExpectedLap(c))}")));
            return;
        }
        ArrangePlayers(session, previous);
    }

    private bool IsActiveBot(IEntryCar<IClient> c) => _slotsBySessionId.TryGetValue(c.SessionId, out var s) && s.Active;

    /// <summary>Expected lap time of a bot, from its strength and car (slowest = longest).</summary>
    private float ExpectedLap(IEntryCar<IClient> c)
    {
        var bot = _slotsBySessionId[c.SessionId].Bot;
        return _field.TargetLap(bot) ?? 1000 - bot.Driver.Level;
    }

    private void ArrangePlayers(SessionState session, SessionState? previous)
    {
        if (session.Grid == null) return;
        bool fromQualifying = previous is { Configuration.Type: SessionType.Qualifying or SessionType.Practice } && previous.Results != null;
        bool fromRace = previous is { Configuration.Type: SessionType.Race };
        if (fromRace) return;
        if (fromQualifying)
        {
            // qualifying result: cars with a time in order, then bots without a time (quickest first),
            // then players without a time at the back (skipped the qualifying), free slots last
            bool HasTime(IEntryCar<IClient> c) => previous!.Results!.TryGetValue(c.SessionId, out var r) && r.BestLap < 999_999_999u;
            var all = session.Grid.ToList();
            var timed = all.Where(HasTime).ToList(); // AssettoServer already sorted them by best lap
            var noTimeBots = all.Where(c => !HasTime(c) && IsActiveBot(c)).OrderBy(ExpectedLap).ToList();
            var noTimePlayers = all.Where(c => !HasTime(c) && !IsActiveBot(c) && c.Client != null).ToList();
            var rest = all.Except(timed).Except(noTimeBots).Except(noTimePlayers).ToList();
            session.Grid = [..timed, ..noTimeBots, ..noTimePlayers, ..rest];
            if (noTimeBots.Count + noTimePlayers.Count > 0)
                Log.Information("BotDriver: grid after qualifying: {Timed} with a time, {Bots} bots and {Players} players without a time at the back",
                    timed.Count(c => c.Client != null || IsActiveBot(c)), noTimeBots.Count, noTimePlayers.Count);
            return;
        }

        var grid = session.Grid.ToList();
        var bots = grid.Where(c => _slotsBySessionId.TryGetValue(c.SessionId, out var s) && s.Active).ToList();
        // connected players first, free player slots go to the back of their group so the grid has no holes
        var others = grid.Where(c => !bots.Contains(c)).OrderBy(c => c.Client == null ? 1 : 0).ToList();
        var freeSlots = others.Where(c => c.Client == null).ToList();
        if (bots.Count == 0) return;

        if (_config.RandomizeBotGrid)
            bots = bots.OrderBy(_ => _rng.Next()).ToList();

        // players (connected or free slots) keep their relative order
        var players = others.Where(c => c.Client != null).ToList();
        List<IEntryCar<IClient>> result = _config.PlayerGridPosition switch
        {
            PlayerGridPosition.First => [..players, ..bots, ..freeSlots],
            PlayerGridPosition.Last => [..bots, ..players, ..freeSlots],
            PlayerGridPosition.Middle => [..bots.Take(bots.Count / 2), ..players, ..bots.Skip(bots.Count / 2), ..freeSlots],
            PlayerGridPosition.Random => [..grid.Where(c => c.Client != null || bots.Contains(c)).OrderBy(_ => _rng.Next()), ..freeSlots],
            _ => _config.RandomizeBotGrid ? MergeKeepingPlayerSpots(grid, bots) : grid
        };
        session.Grid = result;
    }

    private List<IEntryCar<IClient>> MergeKeepingPlayerSpots(List<IEntryCar<IClient>> grid, List<IEntryCar<IClient>> shuffledBots)
    {
        var result = new List<IEntryCar<IClient>>(grid.Count);
        int b = 0;
        foreach (var car in grid)
            result.Add(_slotsBySessionId.TryGetValue(car.SessionId, out var s) && s.Active ? shuffledBots[b++] : car);
        return result;
    }

    public void PlaceOnGrid(BotSlot slot)
    {
        var world = _world!;
        var spots = _track!.Info.StartGrid;
        int i = spots.FindIndex(g => g.Index == slot.GridIndex);
        if (i >= 0)
            world.PlaceAtWorld(slot.Bot, spots[i].Position, BotPhase.Grid);
        else
            world.PlaceAtGridSlot(slot.Bot, slot.GridIndex);
    }

    public void ParkInPitBox(BotSlot slot)
    {
        var pits = _track!.Info.PitBoxes;
        int pi = pits.FindIndex(p => p.Index == slot.EntryCar.SessionId);
        if (pi >= 0)
        {
            _world!.Park(slot.Bot, pits[pi].Position, pits[pi].Forward);
        }
        else
        {
            // no pit box: park next to the track near the start line
            float s = _track.StartLineS - 30 - slot.EntryCar.SessionId * 8;
            var line = _track.Line;
            int i = line.IndexAt(s);
            var pos = line.PositionAt(s, line.RoomPlus[i] + 3);
            _world!.Park(slot.Bot, pos, line.ForwardAt(s));
        }
    }

    /// <summary>A bot that comes back mid-session (slot released by a player).</summary>
    public void PlaceForCurrentSession(BotSlot slot)
    {
        ParkInPitBox(slot);
        if (_sessionType == SessionType.Race)
        {
            if (!_raceStarted) PlaceOnGrid(slot);
            // joining a running race is not possible: stay parked
        }
        else if ((_sessionType == SessionType.Practice && _config.Practice == BotSessionMode.Drive)
                 || (_sessionType == SessionType.Qualifying && _config.Qualifying == BotSessionMode.Drive))
        {
            if (_config.SessionStart == BotSessionStart.Pits && _world!.PitLane != null && _world.PitBoxDistance(slot.Bot) >= 0)
            {
                // back into the session from the pit box
                if (!_pitQueue.Contains(slot.Bot)) _pitQueue.Add(slot.Bot);
                return;
            }
            // somewhere on the lap; overlaps with other cars are pushed apart by the world
            double s = _track!.StartLineS + 200 + _rng.NextDouble() * (_track.Line.Length - 400);
            _world!.PlaceAt(slot.Bot, s, 0, BotPhase.Racing);
            slot.Bot.Speed = 30;
            slot.Bot.LapStartTime = Now;
        }
    }

    // ------------------------------------------------------------------ pit starts and the end of practice / qualifying

    private readonly List<RaceBot> _pitQueue = [];
    private double _nextPitRelease;
    private readonly HashSet<int> _estimated = [];

    /// <summary>Lets the waiting bots out of their boxes one after the other, never into a player who is on his way out.</summary>
    public void ReleaseFromPits(RaceWorld world, double now)
    {
        if (_pitQueue.Count == 0 || now < _nextPitRelease || world.PitLane == null) return;
        var bot = _pitQueue[0];
        if (!_slotsBySessionId.TryGetValue((byte)bot.Id, out var slot) || !slot.Active || bot.Phase != BotPhase.Parked)
        {
            _pitQueue.RemoveAt(0);
            return;
        }
        if (PitLaneBusy(world, world.PitBoxDistance(bot)))
        {
            _nextPitRelease = now + 1;
            return;
        }
        _pitQueue.RemoveAt(0);
        world.ReleaseFromPitBox(bot);
        _nextPitRelease = now + _config.PitReleaseIntervalSeconds * (0.7 + 0.6 * _rng.NextDouble());
    }

    /// <summary>A player (or bot) in the pit lane around/behind the box that would be in the way.</summary>
    private bool PitLaneBusy(RaceWorld world, float boxS)
    {
        var lane = world.PitLane!;
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (car.Client is not { HasSentFirstUpdate: true }) continue;
            if (car.Status.Velocity.LengthSquared() < 4) continue; // standing in the box: not in the way
            var pos = car.Status.Position;
            var (s, off) = lane.Project(pos);
            if (MathF.Abs(off) > 12) continue; // not in the pit lane / boxes
            if (s > boxS - 60 && s < boxS + 40) return true;
        }
        foreach (var other in world.Bots)
        {
            if (!other.InPitLane) continue;
            if (other.PitS > boxS - 40 && other.PitS < boxS + 25) return true;
        }
        return false;
    }

    /// <summary>
    /// Practice / qualifying is over: bots that are still out don't hold the session up. A bot on a timed lap gets that lap estimated
    /// (time so far plus the rest at its usual pace), everybody else goes straight to the box.
    /// </summary>
    public void FinishSessionEarly(SessionState session)
    {
        if (!_config.EstimateLapsAtSessionEnd || _sessionType == SessionType.Race || !session.SessionOverFlag || session.Results == null) return;
        _pitQueue.Clear();
        foreach (var slot in _slots)
        {
            if (!slot.Active || !_estimated.Add(slot.EntryCar.SessionId)) continue;
            var bot = slot.Bot;
            if (!session.Results.TryGetValue(slot.EntryCar.SessionId, out var result) || result.HasCompletedLastLap) continue;
            if (bot.Phase == BotPhase.Racing && bot.TimingValid && !bot.InPitLane && !bot.PittedThisLap)
            {
                var line = _track!.Line;
                float done = line.WrapS((float)bot.Distance - _track.StartLineS) / line.Length;
                float usual = bot.BestLapSeconds < 1e6f ? bot.BestLapSeconds
                    : _field.TargetLap(bot) is { } target ? target * 1.02f : 0;
                if (usual > 0 && done > 0.02f)
                {
                    float elapsed = (float)(Now - bot.LapStartTime);
                    float estimate = elapsed + (1 - done) * usual * (1 + 0.012f * ((float)_rng.NextDouble() - 0.3f));
                    uint ms = (uint)Math.Round(estimate * 1000);
                    _sessionManager.OnAiLapCompleted(slot.EntryCar, ms);
                    Log.Information("BotDriver: {Name} lap estimated at the end of the session: {Time} ({Done:P0} driven)", bot.Name, FormatLap(estimate), done);
                }
            }
            result.HasCompletedLastLap = true;
            ParkInPitBox(slot);
        }
    }

    /// <summary>Laps a bot still has to complete in this session including the current one (int.MaxValue for practice / qualifying).</summary>
    public int RemainingLaps(BotSlot slot, SessionState session)
    {
        var cfg = session.Configuration;
        if (cfg.Type != SessionType.Race) return int.MaxValue;
        if (!cfg.IsTimedRace) return Math.Max(0, cfg.Laps - slot.Bot.LapsCompleted);

        float lapTime = slot.Bot.LastLapSeconds > 0 ? slot.Bot.LastLapSeconds
            : _field.TargetLap(slot.Bot) is { } target ? target * 1.03f : 480;
        double timeLeft = _raceStarted ? session.TimeLeftMilliseconds / 1000.0 : cfg.Time * 60.0;
        int laps = (int)Math.Ceiling(timeLeft / Math.Max(60, lapTime)) + 1;
        if (_serverConfig.Server.HasExtraLap) laps++;
        return Math.Max(1, laps);
    }

    /// <summary>A bot that was switched off by an admin doesn't wait for its turn to leave the pits any more.</summary>
    public void Unqueue(RaceBot bot) => _pitQueue.Remove(bot);

    /// <summary>Chequered flag for a bot: cool-down lap in a race, straight into the box in practice / qualifying.</summary>
    public void CheckFinished(SessionState session)
    {
        if (session.Results == null) return;
        foreach (var slot in _slots)
        {
            if (!slot.Active) continue;
            var bot = slot.Bot;
            if (bot.Phase != BotPhase.Racing) continue;
            if (session.Results.TryGetValue(slot.EntryCar.SessionId, out var result) && result.HasCompletedLastLap)
            {
                if (_sessionType == SessionType.Race) bot.Phase = BotPhase.CoolDown;
                else ParkInPitBox(slot);
            }
        }
    }

    public bool SetGridOrder(string order)
    {
        if (!Enum.TryParse<BotGridOrder>(order, true, out var o)) return false;
        _config.BotGridOrder = o;
        _configWriter.Set("BotGridOrder", o);
        Log.Information("BotDriver: grid order for the next race: {Order}", o);
        return true;
    }
}
