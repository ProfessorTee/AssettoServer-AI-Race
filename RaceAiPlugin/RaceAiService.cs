using System.Collections.Concurrent;
using System.Numerics;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Weather;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Outgoing;
using Microsoft.Extensions.Hosting;
using RaceAiPlugin.Core;
using Serilog;

namespace RaceAiPlugin;

/// <summary>
/// Runs the racing AI: owns the <see cref="RaceWorld"/>, maps bots to entry list slots, follows the server's sessions
/// (grid, race start, chequered flag) and feeds laps back into the official timing.
/// </summary>
public sealed partial class RaceAiService : IHostedService
{
    private readonly RaceAiConfiguration _config;
    private readonly ACServerConfiguration _serverConfig;
    private readonly EntryCarManager _entryCarManager;
    private readonly SessionManager _sessionManager;
    private readonly ACServer _server;
    private readonly WeatherManager _weatherManager;

    private readonly object _lock = new();
    private readonly ConcurrentQueue<(byte BotSessionId, Vector3 OtherPosition, float Speed)> _contacts = new();
    private readonly Dictionary<byte, BotSlot> _slotsBySessionId = new();
    private readonly List<BotSlot> _slots = [];
    private readonly Random _rng = new();
    private readonly Dictionary<CarSpec, StrengthCalibration> _calibrations = new();
    private float? _referenceBestLap;

    private static string FormatLap(float seconds) => TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.fff");

    private RaceWorld? _world;
    private TrackData? _track;
    private bool _raceStarted;
    private SessionType _sessionType;
    private readonly Dictionary<(byte Bot, byte Player), float> _lastRelative = new();

    public IReadOnlyList<BotSlot> Slots => _slots;
    public RaceWorld? World => _world;
    public bool Enabled => _world != null;

    public RaceAiService(RaceAiConfiguration config,
        ACServerConfiguration serverConfig,
        EntryCarManager entryCarManager,
        SessionManager sessionManager,
        ACServer server,
        WeatherManager weatherManager)
    {
        _config = config;
        _serverConfig = serverConfig;
        _entryCarManager = entryCarManager;
        _sessionManager = sessionManager;
        _server = server;
        _weatherManager = weatherManager;
    }

    /// <summary>Entry list slots that are bots, as configured (also used by the slot filter before the service has started).</summary>
    public HashSet<int> ConfiguredBotSlots()
    {
        var slots = new HashSet<int>(_config.BotSlots);
        if (slots.Count == 0)
        {
            for (int i = 0; i < _serverConfig.EntryList.Cars.Count; i++)
            {
                if (_serverConfig.EntryList.Cars[i].AiMode != AiMode.None)
                    slots.Add(i);
            }
        }
        return slots;
    }

    private bool _started;

    public bool PlayersCanTakeBotSlots => _config.PlayersCanTakeBotSlots;

    /// <summary>Used by the slot filter: slot is driven by a bot.</summary>
    public bool IsBotSlot(EntryCar entryCar)
    {
        if (!_started) return ConfiguredBotSlots().Contains(entryCar.SessionId);
        return _world != null && _slotsBySessionId.TryGetValue(entryCar.SessionId, out var s) && s.Active;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            return StartInternal();
        }
        finally
        {
            _started = true;
        }
    }

    private Task StartInternal()
    {
        if (_serverConfig.Extra.EnableAi)
        {
            Log.Warning("Race AI: EnableAi (traffic AI) is switched on in extra_cfg.yml. Racing bots and traffic must not share slots; " +
                        "bot slots are taken away from the traffic AI");
        }

        var botSlots = ConfiguredBotSlots().Where(i => i < _entryCarManager.EntryCars.Length).OrderBy(i => i).ToList();
        if (botSlots.Count == 0)
        {
            Log.Warning("Race AI: no bot slots. Set BotSlots in plugin_race_ai_cfg.yml or mark entries with AI=fixed in entry_list.ini");
            return Task.CompletedTask;
        }

        var trackName = _serverConfig.CSPTrackOptions.Track;
        try
        {
            _track = TrackData.Load(trackName, _serverConfig.Server.TrackConfig, _config);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Race AI: could not load track data for {Track}, racing AI disabled", trackName);
            return Task.CompletedTask;
        }

        var settings = new RaceWorldSettings
        {
            StartLineS = _track.StartLineS,
            ParkHeightAdjust = _config.ParkHeightAdjust,
            HighBeamRange = _config.HighBeamRange,
            SpotHeightOffset = RaceWorld.MeasureSpotHeight(_track.Line, _track.Info.StartGrid.Select(g => g.Position)),
            EdgeMargin = _config.EdgeMargin,
            SideMargin = _config.SideMargin,
            HeightOffset = _config.HeightOffset,
            CoolDownPace = _config.CoolDownPace,
            UseTrackHints = _config.UseTrackHints,
            SlipstreamStrength = 0.35f * _config.SlipstreamStrength,
            YellowFlags = _config.YellowFlags,
            ImpatienceTime = _config.ImpatienceSeconds,
            FuelRate = _config.Fuel ? _serverConfig.Server.FuelConsumptionRate : 0,
            TyreWearRate = _config.TyreWear ? _serverConfig.Server.TyreConsumptionRate : 0,
            TyreWearScale = 0.15f * _config.TyreWearFactor,
            TyreChangeGrip = _config.TyreChangeGrip,
            PitStops = _config.PitStops,
            PitSpeedLimit = _config.PitSpeedKmh / 3.6f,
            HumanErrors = _config.HumanErrors,
            Spins = _config.HumanErrors && _config.Spins,
            GrassMoments = _config.GrassMoments,
            BotContacts = _config.BotContacts,
            Damage = _config.BotDamage && _serverConfig.Server.MechanicalDamageRate > 0,
            DamageRate = _serverConfig.Server.MechanicalDamageRate * _config.BotDamageFactor,
            RainGripLoss = _config.RainGripLoss,
            ServerRainReduction = (float)_serverConfig.Extra.RainTrackGripReductionPercent,
            RainCaution = _config.RainCaution
        };
        Log.Information("Race AI: human errors {Errors} (below {Below} %), spins {Spins}, grass {Grass}, contacts {Contacts}, damage {Damage} ({Rate:P0})",
            settings.HumanErrors ? "on" : "off", _config.HumanErrorsBelow, settings.Spins ? "on" : "off", settings.GrassMoments ? "on" : "off",
            settings.BotContacts ? "on" : "off", settings.Damage ? "on" : "off", settings.DamageRate);
        var world = new RaceWorld(_track.Line, settings)
        {
            PitLane = _config.PitStops ? _track.PitLane : null,
            UnstuckAfter = _config.UnstuckSeconds,
            GhostAfter = _config.GhostAfterSeconds
        };
        _sectorSplits = _track.Info.SectorLines.Select(p => _track.Line.WrapS(_track.Line.Project(p).S - _track.StartLineS)).OrderBy(x => x).ToList();
        if (_config.PitStops && _track.PitLane == null)
            Log.Warning("Race AI: no pit_lane.ai found, bots will not make pit stops");
        Log.Information("Race AI: fuel rate {Fuel:P0}, tyre wear rate {Wear:P0}, pit stops {Pits}",
            settings.FuelRate, settings.TyreWearRate, world.PitLane != null ? "on" : "off");

        var carRoots = TrackData.ContentRoots(_config).Select(r => Path.Join(r, "cars")).ToList();
        var specCache = new Dictionary<(string, float, int), CarSpec>();
        var names = _config.Names.Count > 0 ? _config.Names.ToList() : BotNames.Default.ToList();
        List<string> nations = _config.Names.Count > 0 ? new List<string>() : BotNames.DefaultNations.ToList();
        int nameIndex = 0;
        var strengths = StrengthCalibration.Distribute(botSlots.Count, _config.AiStrength, _config.AiStrengthSpread,
            _config.AiStrengthDistribution == StrengthDistribution.Random, _rng);
        int botIndex = 0;

        foreach (var slotIndex in botSlots)
        {
            var entryCar = _entryCarManager.EntryCars[slotIndex];
            var entry = _serverConfig.EntryList.Cars[slotIndex];
            var driverCfg = _config.Drivers.FirstOrDefault(d => d.Slot == slotIndex);

            var key = (entryCar.Model, entryCar.Ballast, entryCar.Restrictor);
            if (!specCache.TryGetValue(key, out var spec))
            {
                var root = carRoots.FirstOrDefault(r => Directory.Exists(Path.Join(r, entryCar.Model))) ?? carRoots.FirstOrDefault() ?? "content/cars";
                spec = CarDataLoader.Load(root, entryCar.Model, entryCar.Ballast, entryCar.Restrictor, msg => Log.Warning("Race AI: {Message}", msg));
                specCache[key] = spec;
                var cal = StrengthCalibration.Measure(_track.Line, spec, settings);
                _calibrations[spec] = cal;
                Log.Information("Race AI: car {Model} ({Source}): top {Top:F0} km/h, grip {Grip:F2} g, 100 % = {Best}, {Fuel:F1} l/lap, tyres {Compound}, mistakes {Loss:F0} s/lap at most",
                    spec.Model, spec.Source, spec.TopSpeed * 3.6f, spec.LateralGrip, FormatLap(cal.BestLap), spec.CalibratedFuelPerLap, spec.TyreCompound, cal.ErrorLossFull);
            }

            float strength = driverCfg?.Strength ?? strengths[botIndex++];
            var calibration = _calibrations[spec];
            float aggression = driverCfg?.Aggression
                               ?? Math.Clamp(_config.AiAggression + ((float)_rng.NextDouble() * 2 - 1) * _config.AiAggressionVariation, 0, 100);

            string name;
            string nation = driverCfg?.Nation ?? "";
            if (!string.IsNullOrWhiteSpace(driverCfg?.Name)) name = driverCfg.Name;
            else if (!string.IsNullOrWhiteSpace(entry.DriverName)) name = entry.DriverName;
            else
            {
                int n = nameIndex++ % names.Count;
                name = names[n];
                if (string.IsNullOrEmpty(nation) && n < nations.Count) nation = nations[n];
            }

            var personality = PickPersonality(driverCfg?.Personality);
            aggression = Math.Clamp(aggression + personality.Aggression * 100, 0, 100);
            var bot = new RaceBot
            {
                Id = entryCar.SessionId,
                Name = _config.NamePrefix + name,
                Car = spec,
                Driver = DriverProfile.FromStrength(strength, 0, aggression)
            };
            bot.Driver.Personality = personality;
            ApplyStrength(bot, strength, calibration);
            world.Bots.Add(bot);
            if (_track.Info.PitBoxes.FirstOrDefault(p => p.Index == slotIndex) is var box && box.Index == slotIndex && _track.Info.PitBoxes.Count > 0)
                world.SetPitBox(bot, box.Position);

            var slot = new BotSlot(entryCar, bot, nation);
            _slots.Add(slot);
            _slotsBySessionId[entryCar.SessionId] = slot;
            TakeSlot(slot, broadcast: false);

            Log.Information("Race AI: slot {Slot} {Model} -> {Name} (strength {Strength:F1} %, aggression {Aggression:F0}, {Personality})",
                slotIndex, entryCar.Model, bot.Name, strength, aggression, personality.Name);
        }

        if (_config.AiStrengthReference == StrengthReference.Field && _calibrations.Count > 0)
        {
            var bests = _calibrations.Values.Select(c => c.BestLap).OrderBy(x => x).ToList();
            _referenceBestLap = bests[bests.Count / 2];
            foreach (var bot in world.Bots)
                ApplyStrength(bot, bot.Driver.Level, _calibrations[bot.Car]);
            Log.Information("Race AI: 100 % = {Lap} (median best lap of the bot cars); e.g. 95 % = {Lap95}, 90 % = {Lap90}",
                FormatLap(_referenceBestLap.Value), FormatLap(_referenceBestLap.Value / 0.95f), FormatLap(_referenceBestLap.Value / 0.9f));
        }

        foreach (var bot in world.Bots.OrderByDescending(b => b.Driver.Level))
            Log.Information("Race AI:   {Name,-22} {Strength,5:F1} %  target lap {Lap}{Errors}", bot.Name, bot.Driver.Level,
                FormatLap(_calibrations[bot.Car].LapTimeFor(bot.Driver.Level, _referenceBestLap)),
                bot.Driver.Errors > 0 ? $"  (human errors {bot.Driver.Errors:P0})" : "");

        world.LapCompleted += OnBotLapCompleted;
        world.YellowFlag += OnYellowFlag;
        world.PitStopCompleted += OnBotPitStop;
        _world = world;

        _sessionManager.SessionChanged += OnSessionChanged;
        _entryCarManager.ClientConnected += OnClientConnected;
        _entryCarManager.ClientDisconnected += OnClientDisconnected;
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (car.Client != null) car.Client.Collision += OnCollision;
        }

        lock (_lock)
        {
            SetupSession(_sessionManager.CurrentSession, null);
        }

        _server.Update += OnUpdate;
        Log.Information("Race AI: {Count} bots ready on {Track} ({Length:F1} km)", _slots.Count, trackName, _track.Line.Length / 1000);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _server.Update -= OnUpdate;
        return Task.CompletedTask;
    }

    private void TakeSlot(BotSlot slot, bool broadcast)
    {
        var car = slot.EntryCar;
        car.AiName = slot.Bot.Name;
        car.ExternalAiController = slot;
        car.AiControlled = true;
        slot.Active = true;
        if (broadcast)
            _entryCarManager.BroadcastPacket(new CarConnected { SessionId = car.SessionId, Name = slot.Bot.Name, Nation = slot.Nation });
    }

    private void ReleaseSlot(BotSlot slot)
    {
        slot.Active = false;
        slot.EntryCar.AiControlled = false;
        slot.EntryCar.ExternalAiController = null;
        _world?.Park(slot.Bot, slot.Bot.ParkPosition, slot.Bot.ParkForward);
        slot.Bot.Phase = BotPhase.Hidden;
    }

    // ------------------------------------------------------------------ events (any thread)

    private void OnClientConnected(ACTcpClient client, EventArgs args)
    {
        client.Collision += OnCollision;
        if (_slotsBySessionId.TryGetValue(client.SessionId, out var slot))
        {
            lock (_lock)
            {
                if (slot.Active)
                {
                    Log.Information("Race AI: {Player} took over bot slot {Slot} ({Model}), bot {Bot} left", client.Name, client.SessionId, slot.EntryCar.Model, slot.Bot.Name);
                    _entryCarManager.BroadcastChat(T($"{slot.Bot.Name} made room for {client.Name}", $"{slot.Bot.Name} macht Platz für {client.Name}"));
                }
                ReleaseSlot(slot);
            }
        }
    }

    private void OnClientDisconnected(ACTcpClient client, EventArgs args)
    {
        client.Collision -= OnCollision;
        lock (_lock)
        {
            _world?.RemoveExternal(client.SessionId);
            if (_slotsBySessionId.TryGetValue(client.SessionId, out var slot) && !slot.Active)
            {
                TakeSlot(slot, broadcast: true);
                PlaceForCurrentSession(slot, late: true);
                Log.Information("Race AI: {Player} left slot {Slot}, bot {Bot} is back", client.Name, client.SessionId, slot.Bot.Name);
            }
        }
    }

    private void OnCollision(ACTcpClient sender, CollisionEventArgs args)
    {
        if (args.TargetCar != null && _slotsBySessionId.ContainsKey(args.TargetCar.SessionId))
            _contacts.Enqueue((args.TargetCar.SessionId, sender.EntryCar.Status.Position, args.Speed / 3.6f));
    }

    private void OnSessionChanged(SessionManager sender, SessionChangedEventArgs args)
    {
        lock (_lock)
        {
            SetupSession(args.NextSession, args.PreviousSession);
        }
    }

    // ------------------------------------------------------------------ sessions

    private void SetupSession(SessionState session, SessionState? previous)
    {
        if (_world == null) return;
        _sessionType = session.Configuration.Type;
        _raceStarted = false;
        _lastRelative.Clear();
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

        Log.Information("Race AI: session {Session} ({Type}), {Count} bots {Mode}", session.Configuration.Name, _sessionType,
            _slots.Count(s => s.Active), _sessionType == SessionType.Race ? "on the grid" : _pitQueue.Count > 0 ? "leaving the pits" : driving.Count > 0 ? "on track" : "parked");
    }

    /// <summary>Content Manager style starting position of the players, plus random bot order, for races that don't follow a qualifying.</summary>
    private void ArrangeGrid(SessionState session, SessionState? previous)
    {
        if (session.Grid == null) return;
        bool fromQualifying = previous is { Configuration.Type: SessionType.Qualifying or SessionType.Practice }
                              && previous.Results != null && previous.Results.Values.Any(r => r.NumLaps > 0);
        bool fromRace = previous is { Configuration.Type: SessionType.Race };
        if (fromQualifying || fromRace) return;

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

    private void PlaceOnGrid(BotSlot slot)
    {
        var world = _world!;
        var spots = _track!.Info.StartGrid;
        var spot = spots.FirstOrDefault(g => g.Index == slot.GridIndex);
        if (spots.Count > 0 && spot.Index == slot.GridIndex)
            world.PlaceAtWorld(slot.Bot, spot.Position, BotPhase.Grid);
        else
            world.PlaceAtGridSlot(slot.Bot, slot.GridIndex);
    }

    private void ParkInPitBox(BotSlot slot)
    {
        var pits = _track!.Info.PitBoxes;
        var pit = pits.FirstOrDefault(p => p.Index == slot.EntryCar.SessionId);
        if (pits.Count > 0 && pit.Index == slot.EntryCar.SessionId)
        {
            _world!.Park(slot.Bot, pit.Position, pit.Forward);
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
    private void PlaceForCurrentSession(BotSlot slot, bool late)
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
        _ = late;
    }

    // ------------------------------------------------------------------ pit starts and the end of practice / qualifying

    private readonly List<RaceBot> _pitQueue = [];
    private double _nextPitRelease;
    private readonly HashSet<int> _estimated = [];

    /// <summary>Lets the waiting bots out of their boxes one after the other, never into a player who is on his way out.</summary>
    private void ReleaseFromPits(RaceWorld world, double now)
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
    private void FinishSessionEarly(SessionState session)
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
                    : _calibrations.TryGetValue(bot.Car, out var cal) ? cal.LapTimeFor(bot.Driver.Level, _referenceBestLap) * 1.02f : 0;
                if (usual > 0 && done > 0.02f)
                {
                    float elapsed = (float)(Now - bot.LapStartTime);
                    float estimate = elapsed + (1 - done) * usual * (1 + 0.012f * ((float)_rng.NextDouble() - 0.3f));
                    uint ms = (uint)Math.Round(estimate * 1000);
                    _sessionManager.OnAiLapCompleted(slot.EntryCar, ms);
                    Log.Information("Race AI: {Name} lap estimated at the end of the session: {Time} ({Done:P0} driven)", bot.Name, FormatLap(estimate), done);
                }
            }
            result.HasCompletedLastLap = true;
            ParkInPitBox(slot);
        }
    }

    private double Now => _sessionManager.ServerTimeMilliseconds / 1000.0;

    /// <summary>Laps a bot still has to complete in this session including the current one (int.MaxValue for practice / qualifying).</summary>
    private int RemainingLaps(BotSlot slot, SessionState session)
    {
        var cfg = session.Configuration;
        if (cfg.Type != SessionType.Race) return int.MaxValue;
        if (!cfg.IsTimedRace) return Math.Max(0, cfg.Laps - slot.Bot.LapsCompleted);

        float lapTime = slot.Bot.LastLapSeconds > 0 ? slot.Bot.LastLapSeconds
            : _calibrations.TryGetValue(slot.Bot.Car, out var cal) ? cal.LapTimeFor(slot.Bot.Driver.Level, _referenceBestLap) * 1.03f : 480;
        double timeLeft = _raceStarted ? session.TimeLeftMilliseconds / 1000.0 : cfg.Time * 60.0;
        int laps = (int)Math.Ceiling(timeLeft / Math.Max(60, lapTime)) + 1;
        if (_serverConfig.Server.HasExtraLap) laps++;
        return Math.Max(1, laps);
    }

    // ------------------------------------------------------------------ main loop

    private void OnUpdate(ACServer sender, EventArgs args)
    {
        var world = _world;
        if (world == null) return;

        try
        {
            lock (_lock)
            {
                Tick(world);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Race AI: error in update");
        }
    }

    private void Tick(RaceWorld world)
    {
        double now = Now;
        var session = _sessionManager.CurrentSession;

        // race start
        if (_sessionType == SessionType.Race && !_raceStarted && _sessionManager.ServerTimeMilliseconds >= session.StartTimeMilliseconds)
        {
            _raceStarted = true;
            world.StartRace(now);
            world.Settings.RaceStartTime = now;
            Log.Information("Race AI: lights out");
        }

        // player contacts
        while (_contacts.TryDequeue(out var c))
        {
            if (_slotsBySessionId.TryGetValue(c.BotSessionId, out var slot) && slot.Active)
            {
                world.OnContact(slot.Bot, c.OtherPosition, c.Speed);
                world.AddDamage(slot.Bot, ContactZone(world, slot.Bot, c.OtherPosition), c.Speed * 3.6f);
            }
        }

        // human cars
        foreach (var car in _entryCarManager.EntryCars)
        {
            var client = car.Client;
            if (client == null) continue;
            var ext = world.GetOrAddExternal(car.SessionId);
            bool active = client.HasSentFirstUpdate && !car.IsSpectator;
            world.UpdateExternal(ext, car.Status.Position, car.Status.Velocity, active);
            ext.Laps = session.Results != null && session.Results.TryGetValue(car.SessionId, out var res) ? (int)res.NumLaps : 0;
        }

        // chequered flag / end of session
        if (session.Results != null)
        {
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

        // track grip (dynamic track, and rain if the server's RainTrackGripReductionPercent is set) slows the bots down like everybody else;
        // the wet grip is worked out from wetness and standing water
        var weather = _weatherManager.CurrentWeather;
        float grip = weather.TrackGrip > 0.3f ? weather.TrackGrip : 1f;
        world.Settings.GripFactor = Math.Clamp(grip, 0.4f, 1.05f);
        world.Settings.Wetness = Math.Clamp(weather.RainWetness, 0, 1);
        world.Settings.Water = Math.Clamp(weather.RainWater, 0, 1);
        world.Settings.RainIntensity = Math.Clamp(weather.RainIntensity, 0, 1);

        foreach (var slot in _slots)
            slot.Bot.RemainingLaps = RemainingLaps(slot, session);

        ReleaseFromPits(world, now);
        FinishSessionEarly(session);
        world.Advance(now);

        long serverTime = _sessionManager.ServerTimeMilliseconds;
        var lights = Lights();
        if (world.SignalTestPhase(now) != 0) lights = CarStatusFlags.LightsOn | CarStatusFlags.HighBeamsOff;
        var wipers = Wipers();
        foreach (var slot in _slots)
        {
            if (!slot.Active) continue;
            var pose = world.GetPose(slot.Bot);
            slot.WriteStatus(pose, serverTime, lights, wipers, _config.FlashLights || world.SignalTestPhase(now) != 0, _config.FlashLightsDaytime,
                _config.HighBeams || world.SignalTestPhase(now) != 0);
            bool ghost = slot.Bot.GhostUntil > now;
            if (ghost != slot.Ghosted)
            {
                slot.Ghosted = ghost;
                slot.EntryCar.SetCollisions(!ghost);
                if (ghost) Log.Information("Race AI: {Name} stuck for {Seconds:F0} s, ghost for a moment to get out", slot.Bot.Name, _config.GhostAfterSeconds);
            }
            if (slot.SentDamageVersion != slot.Bot.DamageVersion)
            {
                slot.SentDamageVersion = slot.Bot.DamageVersion;
                SendDamage(slot);
            }
        }
        AnnounceSignalTest(world, now);

        if (_config.AnnounceOvertakes && _sessionType == SessionType.Race && _raceStarted)
            AnnounceOvertakes(world);
    }

    private void SendDamage(BotSlot slot)
    {
        var zones = new DamageZoneLevel();
        for (int i = 0; i < DamageZoneLevel.Length; i++) zones[i] = slot.Bot.DamageZones[i];
        slot.EntryCar.Status.DamageZoneLevel = zones;
        _entryCarManager.BroadcastPacket(new DamageUpdate { SessionId = slot.EntryCar.SessionId, DamageZoneLevel = zones });
    }

    /// <summary>Damage zone of a hit from <paramref name="other"/>: 0 front, 1 rear, 2 left, 3 right.</summary>
    private static int ContactZone(RaceWorld world, RaceBot bot, Vector3 other)
    {
        var line = world.Line;
        var p = line.Project(other, line.IndexAt(line.WrapS((float)bot.Distance)));
        float ds = line.Delta(line.WrapS((float)bot.Distance), p.S);
        float dOff = p.Offset - bot.Offset;
        if (MathF.Abs(ds) > bot.Car.Length * 0.35f) return ds > 0 ? 0 : 1;
        return (dOff > 0) == line.RightIsPlus ? 3 : 2;
    }

    private int _lastSignalPhase;

    public bool StartSignalTest()
    {
        lock (_lock)
        {
            if (_world == null) return false;
            _world.StartSignalTest(Now);
            return true;
        }
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
        _entryCarManager.BroadcastChat(what != null ? T($"Race AI signal test: {what}", $"Race-AI Licht-Test: {GermanSignal(phase)}")
            : T("Race AI signal test finished", "Race-AI Licht-Test beendet"));
    }

    private string T(string en, string de) => _config.ChatLanguage == "de" ? de : en;

    private static string GermanSignal(int phase) => phase switch
    {
        1 => "Blinker links", 2 => "Blinker rechts", 3 => "Warnblinker", 4 => "Lichthupe", 5 => "Bremslicht", 6 => "Fernlicht", _ => ""
    };

    private List<float> _sectorSplits = [];
    private readonly Dictionary<int, double> _lastYellowBySector = new();

    /// <summary>Timing sector (1-based) of a racing line position.</summary>
    private int SectorAt(float s)
    {
        float fromStart = _track!.Line.WrapS(s - _track.StartLineS);
        return 1 + _sectorSplits.Count(x => x < fromStart);
    }

    private void OnYellowFlag(YellowFlagEvent e)
    {
        if (!_config.YellowFlagChat || _track == null) return;
        if (_sessionType == SessionType.Race && !_raceStarted) return;
        int sector = SectorAt(e.S);
        double now = Now;
        // one message per sector every 15 s is enough when several cars are involved
        if (_lastYellowBySector.TryGetValue(sector, out var last) && now - last < 15) return;
        _lastYellowBySector[sector] = now;

        string name = e.IsBot && _slotsBySessionId.TryGetValue((byte)e.CarId, out var slot) ? slot.Bot.Name
            : _entryCarManager.EntryCars.ElementAtOrDefault(e.CarId)?.Client?.Name ?? "?";
        var section = _track.Info.SectionAt(_track.Line.WrapS(e.S - _track.StartLineS) / _track.Line.Length);
        string where = section != null ? $" ({section})" : "";
        string en = e.Kind switch { "spin" => $"{name} spun", "crash" => $"{name} crashed", _ => $"{name} stopped on track" };
        string de = e.Kind switch { "spin" => $"{name} hat sich gedreht", "crash" => $"Unfall: {name}", _ => $"{name} steht auf der Strecke" };
        _entryCarManager.BroadcastChat(T($"Yellow flag in sector {sector}{where}: {en}", $"Gelbe Fahne in Sektor {sector}{where}: {de}"));
        Log.Information("Race AI: yellow flag sector {Sector}{Where}: {What}", sector, where, en);
    }

    private readonly List<(Personality Personality, float Share)> _personalities = [];

    private Personality PickPersonality(string? name)
    {
        if (!_config.UsePersonalities) return Personality.Balanced;
        if (_personalities.Count == 0)
        {
            var list = _config.Personalities.Count > 0
                ? _config.Personalities.Select(p => (p.ToPersonality(), p.Share)).ToList()
                : Personality.Defaults();
            _personalities.AddRange(list);
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = _personalities.FirstOrDefault(p => p.Personality.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match.Personality != null) return match.Personality;
            Log.Warning("Race AI: unknown personality {Name}", name);
        }
        float total = _personalities.Sum(p => MathF.Max(0, p.Share));
        if (total <= 0) return Personality.Balanced;
        float r = (float)_rng.NextDouble() * total;
        foreach (var (p, share) in _personalities)
        {
            r -= MathF.Max(0, share);
            if (r <= 0) return p;
        }
        return _personalities[^1].Personality;
    }

    private void OnBotPitStop(RaceBot bot, float seconds, float litres, bool tyres)
    {
        string what = string.Join(" + ", new[] { tyres ? "tyres" : null, litres >= 0.5f ? $"{litres:F0} l" : null, bot.PitRepair ? "repair" : null }.Where(x => x != null));
        Log.Information("Race AI: {Name} pit stop ({Reason}): {What}, {Seconds:F1} s", bot.Name, bot.PitReason, what, seconds);
        if (_config.AnnouncePitStops && _sessionType == SessionType.Race)
            _entryCarManager.BroadcastChat(T($"{bot.Name} pit stop: {what} ({seconds:F1} s)",
                $"{bot.Name} Boxenstopp: {what.Replace("tyres", "Reifen").Replace("repair", "Reparatur")} ({seconds:F1} s)"));
    }

    private void OnBotLapCompleted(RaceBot bot, float lapSeconds)
    {
        if (!_slotsBySessionId.TryGetValue((byte)bot.Id, out var slot) || !slot.Active) return;

        if (bot.Phase == BotPhase.CoolDown)
        {
            // in-lap after the flag: back to the pits
            ParkInPitBox(slot);
            return;
        }

        if (_sessionType is not (SessionType.Race or SessionType.Practice or SessionType.Qualifying)) return;

        uint ms = (uint)Math.Round(lapSeconds * 1000);
        bool accepted = _sessionManager.OnAiLapCompleted(slot.EntryCar, ms);
        if (_config.LogLaps)
            Log.Information("Race AI: {Name} lap {Lap} {Time}{Rejected}", bot.Name, bot.LapsCompleted,
                TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss\.fff"), accepted ? "" : " (not counted)");
    }

    private CarStatusFlags Lights()
    {
        const CarStatusFlags lightFlags = CarStatusFlags.LightsOn | CarStatusFlags.HighBeamsOff;
        if (_config.DaytimeLights) return lightFlags;
        var sun = _weatherManager.CurrentSunPosition;
        if (sun == null) return lightFlags;
        return sun.Value.Altitude < 0.05 ? lightFlags : 0;
    }

    private CarStatusFlags Wipers()
        => _weatherManager.CurrentWeather.RainIntensity switch
        {
            < 0.05f => 0,
            < 0.25f => CarStatusFlags.WiperLevel1,
            < 0.5f => CarStatusFlags.WiperLevel2,
            _ => CarStatusFlags.WiperLevel3
        };

    private void AnnounceOvertakes(RaceWorld world)
    {
        var line = world.Line;
        foreach (var slot in _slots)
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
                    var player = _entryCarManager.EntryCars[ext.Id].Client;
                    var section = _track!.Info.SectionAt(line.WrapS(botS - _track.StartLineS) / line.Length);
                    if (player != null)
                    {
                        string where = section != null ? T($" at {section}", $" bei {section}") : "";
                        var (a, b) = rel > 0 ? (slot.Bot.Name, player.Name) : (player.Name, slot.Bot.Name);
                        _entryCarManager.BroadcastChat(T($"{a} overtook {b}{where}", $"{a} überholt {b}{where}"));
                    }
                }
                _lastRelative[key] = rel;
            }
        }
    }

    // ------------------------------------------------------------------ admin

    public void SetStrength(float strength, float? spread = null)
    {
        lock (_lock)
        {
            var values = StrengthCalibration.Distribute(_slots.Count, strength, spread ?? _config.AiStrengthSpread,
                _config.AiStrengthDistribution == StrengthDistribution.Random, _rng);
            for (int i = 0; i < _slots.Count; i++)
            {
                var bot = _slots[i].Bot;
                if (!_calibrations.TryGetValue(bot.Car, out var cal)) continue;
                ApplyStrength(bot, values[i], cal);
            }
        }
    }

    /// <summary>Strength in % -> consistency, human error level and the pace that gives the target lap time including the mistakes.</summary>
    private void ApplyStrength(RaceBot bot, float strength, StrengthCalibration cal)
    {
        var d = DriverProfile.FromStrength(strength, 0, 0);
        bot.Driver.Level = strength;
        bot.Driver.Consistency = d.Consistency;
        bot.Driver.Errors = _config.HumanErrors ? DriverProfile.ErrorsFor(strength, _config.HumanErrorsBelow, _config.HumanErrorsFull) : 0;
        bot.Driver.Pace = cal.PaceFor(strength, _referenceBestLap, bot.Driver.Errors);
    }

    public string? BestLapFor(CarSpec car) => _calibrations.TryGetValue(car, out var cal) ? FormatLap(cal.BestLap) : null;

    public bool SetFeature(string feature, bool on)
    {
        lock (_lock)
        {
            var s = _world?.Settings;
            if (s == null) return false;
            switch (feature)
            {
                case "errors" or "humanerrors":
                    _config.HumanErrors = on;
                    s.HumanErrors = on;
                    foreach (var slot in _slots)
                        if (_calibrations.TryGetValue(slot.Bot.Car, out var cal)) ApplyStrength(slot.Bot, slot.Bot.Driver.Level, cal);
                    break;
                case "spins": s.Spins = on; break;
                case "grass": s.GrassMoments = on; break;
                case "contacts": s.BotContacts = on; break;
                case "damage": s.Damage = on && s.DamageRate > 0; break;
                case "blueflags": _config.BlueFlags = on; s.BlueFlags = on && _sessionType == SessionType.Race; break;
                case "yellowflags": s.YellowFlags = on; break;
                case "flash": _config.FlashLights = on; break;
                case "raincaution": _config.RainCaution = on; s.RainCaution = on; break;
                case "realweather": _config.RealWeather = on; break;
                case "highbeams": _config.HighBeams = on; break;
                default: return false;
            }
            return true;
        }
    }

    public void SetAggression(float aggression)
    {
        lock (_lock)
        {
            foreach (var slot in _slots)
                slot.Bot.Driver.Aggression = Math.Clamp(aggression / 100f, 0, 1);
        }
    }
}
