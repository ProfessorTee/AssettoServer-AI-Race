using SharedConfig;
using AssettoServer.Server.Extensions;
using TrackGeometry;
using System.Collections.Concurrent;
using System.Numerics;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Weather;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Outgoing;
using Microsoft.Extensions.Hosting;
using BotDriverPlugin.Core;
using Serilog;

namespace BotDriverPlugin;

/// <summary>
/// Runs the racing AI: owns the <see cref="RaceWorld"/>, maps bots to entry list slots, follows the server's sessions
/// (grid, race start, chequered flag) and feeds laps back into the official timing.
/// </summary>
public sealed partial class BotDriverService : IHostedService, IDrivenCars
{
    private readonly BotDriverConfiguration _config;
    private readonly ACServerConfiguration _serverConfig;
    private readonly EntryCarManager _entryCarManager;
    private readonly SessionManager _sessionManager;
    private readonly ACServer _server;
    private readonly WeatherManager _weatherManager;

    // the shared race state (BotRace); these names keep the service's code short
    private readonly BotRace _race;
    private readonly RaceAnnouncer _announcer;
    private readonly FieldStrength _field;
    /// <summary>Strength, personalities, switches and calibration of the bots.</summary>
    public FieldStrength Field => _field;
    private readonly GridDirector _grid;
    /// <summary>Grid, pit boxes and the bots' way into and out of a session.</summary>
    public GridDirector Grid => _grid;
    private readonly CarTakeover _takeover;
    private object _lock => _race.Lock;
    private ConfigWriter _configWriter = null!;
    private readonly ConcurrentQueue<(byte BotSessionId, byte OtherSessionId, Vector3 OtherPosition, float Speed)> _contacts = new();
    private Dictionary<byte, BotSlot> _slotsBySessionId => _race.SlotsBySessionId;
    private List<BotSlot> _slots => _race.Slots;
    private Random _rng => _race.Rng;
    private Dictionary<(string, float, int), CarSpec> _specCache => _race.SpecCache;
    private List<string> _carRoots { get => _race.CarRoots; set => _race.CarRoots = value; }
    private CloneLibrary? _clones;

    private static string FormatLap(float seconds) => TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.fff");

    private RaceWorld? _world { get => _race.World; set => _race.World = value; }
    private TrackData? _track { get => _race.Track; set => _race.Track = value; }
    private bool _raceStarted { get => _race.RaceStarted; set => _race.RaceStarted = value; }
    private SessionType _sessionType { get => _race.SessionType; set => _race.SessionType = value; }

    /// <summary>The slots right now (a copy: the list changes under the race lock).</summary>
    public List<BotSlot> Slots { get { lock (_lock) return _slots.ToList(); } }
    public RaceWorld? World => _world;
    public bool Enabled => _world != null;

    public BotDriverService(BotDriverConfiguration config,
        ACServerConfiguration serverConfig,
        EntryCarManager entryCarManager,
        SessionManager sessionManager,
        ACServer server,
        WeatherManager weatherManager,
        CSPServerScriptProvider scriptProvider,
        IEnumerable<ISharedSettings> sharedSettings)
    {
        SharedSettingsResolver.Resolve(config, sharedSettings);
        _race = new BotRace(config, serverConfig, entryCarManager, sessionManager);
        _announcer = new RaceAnnouncer(_race);
        _config = config;
        _serverConfig = serverConfig;
        _entryCarManager = entryCarManager;
        _sessionManager = sessionManager;
        _server = server;
        _weatherManager = weatherManager;
        _configWriter = new ConfigWriter(serverConfig, "plugin_bot_driver_cfg.yml", "BotDriver");
        _field = new FieldStrength(_race, _configWriter);
        _grid = new GridDirector(_race, _field, _configWriter);
        _takeover = new CarTakeover(_race, _field, () => _clones);
        // duels: a player's lap against the recorded clone
        _entryCarManager.ClientConnected += (c, _) => c.LapCompleted += OnDuelLap;
        _entryCarManager.ClientDisconnected += (c, _) => c.LapCompleted -= OnDuelLap;
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
        lock (_lock) return _world != null && _slotsBySessionId.TryGetValue(entryCar.SessionId, out var s) && s.Active;
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
            Log.Warning("BotDriver: EnableAi (traffic AI) is switched on in extra_cfg.yml. Racing bots and traffic must not share slots; " +
                        "bot slots are taken away from the traffic AI");
        }

        var botSlots = ConfiguredBotSlots().Where(i => i < _entryCarManager.EntryCars.Length).OrderBy(i => i).ToList();
        if (botSlots.Count == 0)
        {
            Log.Warning("BotDriver: no bot slots. Set BotSlots in plugin_bot_driver_cfg.yml or mark entries with AI=fixed in entry_list.ini");
            return Task.CompletedTask;
        }

        var trackName = _serverConfig.CSPTrackOptions.Track;
        try
        {
            _track = TrackData.Load(trackName, _serverConfig.Server.TrackConfig, _config, layers: _serverConfig.Layers);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BotDriver: could not load track data for {Track}, racing AI disabled", trackName);
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
            PlayerSideMargin = _config.PlayerSideMargin,
            PlayerOverlap = _config.PlayerOverlap,
            HeightOffset = _config.HeightOffset,
            CoolDownPace = _config.CoolDownPace,
            UseTrackHints = _config.UseTrackHints,
            SlipstreamStrength = 0.35f * _config.SlipstreamStrength,
            YellowFlags = _config.YellowFlags,
            ImpatienceTime = _config.ImpatienceSeconds,
            FlashStartDelay = _config.FlashStartDelaySeconds,
            PitLimiterFlash = _config.PitLimiterFlash,
            FuelRate = _config.Fuel ? _serverConfig.Server.FuelConsumptionRate : 0,
            TyreWearRate = _config.TyreWear ? _serverConfig.Server.TyreConsumptionRate : 0,
            TyreWearScale = 0.15f * _config.TyreWearFactor,
            TyreChangeGrip = _config.TyreChangeGrip,
            PitStops = _config.PitStops,
            PitSpeedLimit = _config.PitSpeedKmh / 3.6f,
            HumanErrors = _config.HumanErrors,
            Spins = _config.HumanErrors && _config.Spins,
            GrassMoments = _config.GrassMoments,
            LineErrors = _config.LineErrors,
            BotContacts = _config.BotContacts,
            Damage = _config.BotDamage && _serverConfig.Server.MechanicalDamageRate > 0,
            DamageRate = _serverConfig.Server.MechanicalDamageRate * _config.BotDamageFactor,
            RainGripLoss = _config.RainGripLoss,
            ServerRainReduction = (float)_serverConfig.Extra.RainTrackGripReductionPercent,
            RainCaution = _config.RainCaution,
            PersonalLines = _config.PersonalLines,
            RealisticStart = _config.RealisticStart,
            RubberBand = Math.Clamp(_config.RubberBanding / 100f, 0, 1),
            RubberBandAhead = Math.Clamp(_config.RubberBandingAhead / 100f, 0, 0.2f),
            RubberBandBehind = Math.Clamp(_config.RubberBandingBehind / 100f, 0, 0.2f),
            RubberBandDistance = MathF.Max(10, _config.RubberBandingDistance),
            RubberBandTime = MathF.Max(0, _config.RubberBandingTime)
        };
        Log.Information("BotDriver: human errors {Errors} (below {Below} %), spins {Spins}, grass {Grass}, contacts {Contacts}, damage {Damage} ({Rate:P0})",
            settings.HumanErrors ? "on" : "off", _config.HumanErrorsBelow, settings.Spins ? "on" : "off", settings.GrassMoments ? "on" : "off",
            settings.BotContacts ? "on" : "off", settings.Damage ? "on" : "off", settings.DamageRate);
        var world = new RaceWorld(_track.Line, settings)
        {
            PitLane = _config.PitStops ? _track.PitLane : null,
            Heights = _track.Heights,
            OffTrack = _track.OffTrack,
            Ideal = _track.Ideal,
            UnstuckAfter = _config.UnstuckSeconds,
            GhostAfter = _config.GhostAfterSeconds
        };
        if (_config.PitStops && _track.PitLane == null)
            Log.Warning("BotDriver: no pit_lane.ai found, bots will not make pit stops");
        Log.Information("BotDriver: fuel rate {Fuel:P0}, tyre wear rate {Wear:P0}, pit stops {Pits}",
            settings.FuelRate, settings.TyreWearRate, world.PitLane != null ? "on" : "off");

        var carRoots = TrackData.ContentRoots(_config).Select(r => Path.Join(r, "cars")).ToList();
        _carRoots = carRoots;
        var specCache = _specCache;
        _clones = new CloneLibrary(_config.RecordingsFolder, TrackKey(), _track.Line);
        var names = _config.Names.Count > 0 ? _config.Names.ToList() : BotNames.Default.ToList();
        List<string> nations = _config.Names.Count > 0 ? new List<string>() : BotNames.DefaultNations.ToList();
        int nameIndex = 0;
        var strengths = StrengthCalibration.Distribute(botSlots.Count, _config.AiStrength, _config.AiStrengthSpread,
            _config.AiStrengthDistribution == StrengthDistribution.Random, _rng);
        int botIndex = 0;

        // all cars first, calibrated in parallel (a few seconds per car, now one core each)
        var toCalibrate = new List<(CarSpec Spec, string Variant)>();
        foreach (var slotIndex in botSlots)
        {
            var ec = _entryCarManager.EntryCars[slotIndex];
            var k = (ec.Model, ec.Ballast, ec.Restrictor);
            if (specCache.ContainsKey(k)) continue;
            var root = carRoots.FirstOrDefault(r => Directory.Exists(Path.Join(r, ec.Model))) ?? carRoots.FirstOrDefault() ?? "content/cars";
            var sp = CarDataLoader.LoadForTrack(root, ec.Model, _track.Line, ec.Ballast, ec.Restrictor, msg => Log.Warning("BotDriver: {Message}", msg));
            specCache[k] = sp;
            toCalibrate.Add((sp, $"{ec.Ballast}/{ec.Restrictor}"));
        }
        // calibration: from the cache, or provisional (quick) now and final in the background, so the server starts right away
        var calSw = System.Diagnostics.Stopwatch.StartNew();
        var done = new System.Collections.Concurrent.ConcurrentDictionary<CarSpec, (StrengthCalibration Cal, bool Final)>();
        Parallel.ForEach(toCalibrate, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            c => done[c.Spec] = _field.StartupCalibration(c.Spec, settings, c.Variant));
        foreach (var (sp, variant) in toCalibrate)
        {
            var (cal, final) = done[sp];
            _field.SetCalibration(sp, cal, !final, variant);
            Log.Information("BotDriver: car {Model} ({Source}): top {Top:F0} km/h, grip {Grip:F2} g, 100 % = {Best}{Prov}, {Fuel:F1} l/lap, tyres {Compound}, mistakes {Loss:F0} s/lap at most{Wing}",
                sp.Model, sp.Source, sp.TopSpeed * 3.6f, sp.LateralGrip, FormatLap(cal.BestLap), final ? "" : " (provisional)", sp.CalibratedFuelPerLap, sp.TyreCompound, cal.ErrorLossFull, sp.WingLevel is { } wl ? $", wings {wl * 100:F0} %" : "");
        }
        if (toCalibrate.Count > 0)
            Log.Information("BotDriver: {Count} cars ready in {Seconds:F1} s on {Cores} CPU core(s), {Prov} to be calibrated finally in the background",
                toCalibrate.Count, calSw.Elapsed.TotalSeconds, Environment.ProcessorCount, _field.ProvisionalCount);

        foreach (var slotIndex in botSlots)
        {
            var entryCar = _entryCarManager.EntryCars[slotIndex];
            var entry = _serverConfig.EntryList.Cars[slotIndex];
            var driverCfg = _config.Drivers.FirstOrDefault(d => d.Slot == slotIndex);

            var spec = specCache[(entryCar.Model, entryCar.Ballast, entryCar.Restrictor)]; // loaded and calibrated above

            float strength = driverCfg?.Strength ?? strengths[botIndex++];
            var calibration = _field.Calibration(spec);
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

            var personality = _field.PickPersonality(driverCfg?.Personality);
            aggression = Math.Clamp(aggression + personality.Aggression * 100, 0, 100);
            var bot = new RaceBot
            {
                Id = entryCar.SessionId,
                Name = _config.NamePrefix + name,
                Car = spec,
                Driver = DriverProfile.FromStrength(strength, 0, aggression),
                LegalTyres = entryCar.LegalTyres
            };
            bot.Driver.Personality = personality;
            if (!string.IsNullOrWhiteSpace(driverCfg?.Clone))
            {
                // a clone of a recorded player
                var guid = _clones.FindGuid(driverCfg.Clone);
                var profile = guid != null ? _clones.Get(guid, entryCar.Model, anyCar: true) : null;
                if (profile != null)
                {
                    bot.Clone = profile;
                    if (string.IsNullOrWhiteSpace(driverCfg.Name))
                        bot.Name = _config.NamePrefix + T($"{profile.PlayerName} (clone)", $"{profile.PlayerName} (Klon)");
                    if (profile.Car != entryCar.Model)
                        Log.Warning("BotDriver: clone of {Player} was recorded in {Recorded}, drives a {Car} here", profile.PlayerName, profile.Car, entryCar.Model);
                }
                else Log.Warning("BotDriver: no clean recorded laps of {Player} on this track, slot {Slot} drives as a normal bot", driverCfg.Clone, slotIndex);
            }
            _field.ApplyStrength(bot, strength, calibration);
            world.Bots.Add(bot);
            if (_track.Info.PitBoxes.FindIndex(p => p.Index == slotIndex) is var bi and >= 0)
                world.SetPitBox(bot, _track.Info.PitBoxes[bi].Position);

            var slot = new BotSlot(entryCar, bot, nation);
            _slots.Add(slot);
            _slotsBySessionId[entryCar.SessionId] = slot;
            TakeSlot(slot, broadcast: false);

            Log.Information("BotDriver: slot {Slot} {Model} -> {Name} (strength {Strength:F1} %, aggression {Aggression:F0}, {Personality})",
                slotIndex, entryCar.Model, bot.Name, strength, aggression, personality.Name);
        }

        if (_config.AiStrengthReference == StrengthReference.Field && _field.AnyCalibrations)
            _field.ApplyCalibrations(world, log: true);

        foreach (var bot in world.Bots.OrderByDescending(b => b.Driver.Level))
            Log.Information("BotDriver:   {Name,-22} {Strength,5:F1} %  target lap {Lap}{Errors}", bot.Name, bot.Driver.Level,
                FormatLap(_field.TargetLap(bot) ?? 0),
                bot.Driver.Errors > 0 ? $"  (human errors {bot.Driver.Errors:P0})" : "");

        world.LapCompleted += OnBotLapCompleted;
        world.YellowFlag += _announcer.OnYellowFlag;
        world.PitStopCompleted += _announcer.OnBotPitStop;
        _world = world;
        world.Trace = msg => Log.Information("BotDriver trace: {Line}", msg);
        world.TraceBotId = -1;
        if (_config.Debug) Log.Information("BotDriver: debug logging on (Debug in plugin_bot_driver_cfg.yml)");
        if (_config.MaxBots >= 0) _botLimit = _config.MaxBots;

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
        Log.Information("BotDriver: {Count} bots ready on {Track} ({Length:F1} km)", _slots.Count, trackName, _track.Line.Length / 1000);
        _field.StartBackgroundCalibration(settings);
        if (_botLimit != null)
            lock (_lock)
            {
                var (on, off) = ApplyBotLimit();
                Log.Information("BotDriver: MaxBots {Limit}: {On} bots on track, {Off} switched off", _botLimit, on, off);
            }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _field.Stop();
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
        // build the player's clone profile now (if he was recorded), so a takeover drives like him at once
        if (_clones is { } clones && _config.TakeOverDisconnectedPlayers)
        {
            string guid = client.Guid.ToString(), model = client.EntryCar.Model;
            _ = Task.Run(() => { try { clones.Get(guid, model, anyCar: true); } catch (Exception ex) { Log.Debug(ex, "BotDriver: clone preload failed"); } });
        }
        lock (_lock)
        {
            if (_slotsBySessionId.TryGetValue(client.SessionId, out var slot))
            {
                if (slot.TakeoverGuid != null)
                {
                    // somebody joins a car a bot took over from a player who left: the bot makes room
                    _takeover.End(slot);
                    return;
                }
                if (slot.Active)
                {
                    Log.Information("BotDriver: {Player} took over bot slot {Slot} ({Model}), bot {Bot} left", client.Name, client.SessionId, slot.EntryCar.Model, slot.Bot.Name);
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
            // racing when he left: a bot drives his car on (needs his last position, before it is removed)
            _takeover.TryTakeOver(client);
            _world?.RemoveExternal(client.SessionId);
            if (_slotsBySessionId.TryGetValue(client.SessionId, out var slot) && !slot.Active && !slot.Benched)
            {
                TakeSlot(slot, broadcast: true);
                _grid.PlaceForCurrentSession(slot);
                Log.Information("BotDriver: {Player} left slot {Slot}, bot {Bot} is back", client.Name, client.SessionId, slot.Bot.Name);
            }
        }
    }

    private void OnCollision(ACTcpClient sender, CollisionEventArgs args)
    {
        // not a bot? filtered in Tick, under the lock
        if (args.TargetCar != null)
            _contacts.Enqueue((args.TargetCar.SessionId, sender.SessionId, sender.EntryCar.Status.Position, args.Speed / 3.6f));
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
        _takeover.EndAll();
        _announcer.NewSession();
        _grid.SetupSession(session, previous);
    }

    private double Now => _race.Now;

    // ------------------------------------------------------------------ main loop

    private void OnUpdate(ACServer sender, EventArgs args)
    {
        var world = _world;
        if (world == null) return;

        try
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_lock)
            {
                Tick(world);
            }
            RecordTick(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BotDriver: error in update");
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
            Log.Information("BotDriver: lights out");
        }

        // player contacts
        while (_contacts.TryDequeue(out var c))
        {
            if (_slotsBySessionId.TryGetValue(c.BotSessionId, out var slot) && slot.Active)
            {
                world.OnContact(slot.Bot, c.OtherPosition, c.Speed, world.Externals.FirstOrDefault(e => e.Id == c.OtherSessionId));
                world.AddDamage(slot.Bot, ContactZone(world, slot.Bot, c.OtherPosition), c.Speed * 3.6f);
            }
        }

        // human cars
        foreach (var car in _entryCarManager.EntryCars)
        {
            var client = car.Client;
            if (client == null) continue;
            if (_slotsBySessionId.TryGetValue(car.SessionId, out var botSlot) && botSlot.Active) continue; // a bot drives this car
            var ext = world.GetOrAddExternal(car.SessionId);
            bool active = client.HasSentFirstUpdate && !car.IsSpectator;
            // the last position update is already a little old (network delay): the world extrapolates it from its timestamp
            // to every simulation step (speed and braking), so the bots see where the car is now
            double at = Math.Max(car.Status.Timestamp / 1000.0, now - 0.3);
            world.UpdateExternal(ext, car.Status.Position, car.Status.Velocity, active, at);
            ext.Laps = session.Results != null && session.Results.TryGetValue(car.SessionId, out var res) ? (int)res.NumLaps : 0;
        }

        // chequered flag / end of session
        _grid.CheckFinished(session);

        // track grip (dynamic track, and rain if the server's RainTrackGripReductionPercent is set) slows the bots down like everybody else;
        // the wet grip is worked out from wetness and standing water
        var weather = _weatherManager.CurrentWeather;
        float grip = weather.TrackGrip > 0.3f ? weather.TrackGrip : 1f;
        world.Settings.GripFactor = Math.Clamp(grip, 0.4f, 1.05f);
        world.Settings.Wetness = Math.Clamp(weather.RainWetness, 0, 1);
        world.Settings.AmbientTemp = weather.TemperatureAmbient;
        world.Settings.RoadTemp = weather.TemperatureRoad;
        world.Settings.IsRace = _sessionType == SessionType.Race;
        world.Settings.Water = Math.Clamp(weather.RainWater, 0, 1);
        world.Settings.RainIntensity = Math.Clamp(weather.RainIntensity, 0, 1);

        foreach (var slot in _slots)
            slot.Bot.RemainingLaps = _grid.RemainingLaps(slot, session);

        _grid.ReleaseFromPits(world, now);
        DebugTick(world, now);
        _grid.FinishSessionEarly(session);
        world.Advance(now);

        long serverTime = _sessionManager.ServerTimeMilliseconds;
        var lights = Lights();
        bool signalTest = world.SignalTestPhase(now) != 0;
        if (signalTest) lights = CarStatusFlags.LightsOn | CarStatusFlags.HighBeamsOff;
        var wipers = Wipers();
        foreach (var slot in _slots)
        {
            if (!slot.Active) continue;
            var pose = world.GetPose(slot.Bot);
            slot.WriteStatus(pose, serverTime, lights, wipers, _config.FlashLights || signalTest, _config.FlashLightsDaytime,
                _config.HighBeams || signalTest);
            bool ghost = slot.Bot.GhostUntil > now;
            if (ghost != slot.Ghosted)
            {
                slot.Ghosted = ghost;
                slot.EntryCar.SetCollisions(!ghost);
                if (ghost) Log.Information("BotDriver: {Name} stuck for {Seconds:F0} s, ghost for a moment to get out", slot.Bot.Name, _config.GhostAfterSeconds);
            }
            // the compound on the car, for the clients (tyre apps, leaderboards) and players who join later
            string compound = RaceWorld.CompoundName(slot.Bot);
            if (compound != "" && compound != slot.EntryCar.Status.CurrentTyreCompound)
            {
                slot.EntryCar.Status.CurrentTyreCompound = compound;
                slot.Status.CurrentTyreCompound = compound;
                _entryCarManager.BroadcastPacket(new TyreCompoundUpdate { SessionId = slot.EntryCar.SessionId, CompoundName = compound });
            }
            if (slot.SentDamageVersion != slot.Bot.DamageVersion)
            {
                slot.SentDamageVersion = slot.Bot.DamageVersion;
                SendDamage(slot);
            }
        }
        _announcer.Tick(world, now);
    }

    private void SendDamage(BotSlot slot)
    {
        var zones = new DamageZoneLevel();
        for (int i = 0; i < DamageZoneLevel.Length; i++) zones[i] = slot.Bot.DamageZones[i];
        slot.Status.DamageZoneLevel = zones;
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

    public bool StartSignalTest()
    {
        lock (_lock)
        {
            if (_world == null) return false;
            _world.StartSignalTest(Now);
            return true;
        }
    }

    private string T(string en, string de) => _config.ChatLanguage == "de" ? de : en;

    private void OnDuelLap(ACTcpClient client, LapCompletedEventArgs args) => DuelLap(client, args.Packet.LapTime, args.Packet.Cuts);

    private void OnBotLapCompleted(RaceBot bot, float lapSeconds)
    {
        if (!_slotsBySessionId.TryGetValue((byte)bot.Id, out var slot) || !slot.Active) return;

        if (bot.Phase == BotPhase.CoolDown)
        {
            // in-lap after the flag: back to the pits
            _grid.ParkInPitBox(slot);
            return;
        }

        if (_sessionType is not (SessionType.Race or SessionType.Practice or SessionType.Qualifying)) return;

        uint ms = (uint)Math.Round(lapSeconds * 1000);
        bool accepted = _sessionManager.OnAiLapCompleted(slot.EntryCar, ms);
        if (_config.LogLaps)
            Log.Information("BotDriver: {Name} lap {Lap} {Time}{Rejected}", bot.Name, bot.LapsCompleted,
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

    // ------------------------------------------------------------------ admin

    public bool IsBotCar(byte sessionId) { lock (_lock) return _slotsBySessionId.TryGetValue(sessionId, out var s) && s.Active; }

    private string TrackKey()
    {
        string track = _serverConfig.Server.Track;
        int slash = track.LastIndexOf('/');
        if (slash >= 0) track = track[(slash + 1)..];
        return string.IsNullOrEmpty(_serverConfig.Server.TrackConfig) ? track : $"{track}-{_serverConfig.Server.TrackConfig}";
    }

    public CloneLibrary? Clones => _clones;
}
