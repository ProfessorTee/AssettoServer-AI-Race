using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Extensions;
using AssettoServer.Server.Weather;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Outgoing;
using Microsoft.Extensions.Hosting;
using Serilog;
using TrackGeometry;

namespace WebPortalPlugin;

/// <summary>
/// What the pages show, from what the server knows: session, weather, the cars (players and, when a plugin drives some,
/// <see cref="IDrivenCars"/>), where they are on the lap (track geometry from fast_lane.ai), the last race's classification.
/// </summary>
public sealed class RaceView : BackgroundService
{
    public sealed class LiveCar
    {
        public byte Id;
        public string Name = "";
        public string Model = "";
        public string Kind = ""; // "ai", "clone", "player"
        public ulong Guid;
        public float X, Z, Vx, Vz, Speed;
        public int Gear, Rpm;
        public float Throttle;
        public bool Brake;
        public uint Laps, Best, Last;
        public bool Finished;
        public float LapFraction; // 0..1 from the start line
        public int Sector;
        public bool InPit;
        public int Stops;
        public float? Tyres;
        // players only (admin page)
        public bool Admin;
        public int Ping;
        public float Jitter;
    }

    public sealed class LiveFrame
    {
        public string Server = "";
        public string Track = "";
        public string Session = "";
        public string SessionType = "";
        public long? TimeLeft;
        public int Laps;
        public int LeaderLap;
        public bool RaceStarted;
        public long ServerMs;
        public long SessionStart;
        public string Weather = "";
        public float Ambient, Road, Rain, Wetness, Water, Grip;
        public string TimeOfDay = "";
        public int Sectors;
        public List<LiveCar> Cars = [];
    }

    private readonly ACServerConfiguration _serverConfig;
    private readonly WebPortalConfiguration _config;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly WeatherManager _weatherManager;
    private readonly IReadOnlyList<IDrivenCars> _drivers;
    private readonly IReadOnlyList<ISharedSettings> _shared;
    private readonly object _lock = new();
    private readonly Dictionary<byte, int> _hints = new();
    private TrackMap? _map;
    private List<Dictionary<string, object?>>? _lastRace;
    private DateTime _lastRaceAt;

    public RaceView(ACServerConfiguration serverConfig, WebPortalConfiguration config, SessionManager sessionManager, EntryCarManager entryCarManager,
        WeatherManager weatherManager, IEnumerable<IDrivenCars> drivers, IEnumerable<ISharedSettings> shared)
    {
        _serverConfig = serverConfig;
        _config = config;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
        _weatherManager = weatherManager;
        _drivers = drivers.ToList();
        _shared = shared.ToList();
        TrackKey = TrackMap.Key(serverConfig.Server.Track, serverConfig.Server.TrackConfig);
        _sessionManager.SessionChanged += OnSessionChanged;
    }

    /// <summary>"ks_nordschleife-nordschleife".</summary>
    public string TrackKey { get; }
    public TrackMap? Map => _map;
    public string ChatLanguage => SharedSettings.ChatLanguage(_shared, _config.ChatLanguage);
    /// <summary>A plugin drives cars (bots).</summary>
    public bool HasDrivenCars => _drivers.Count > 0;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() =>
    {
        try
        {
            var roots = TrackMap.ContentRoots(SharedSettings.AssettoCorsaPath(_shared, _config.AssettoCorsaPath));
            var grid = TrackMap.FindGridFile(_serverConfig.Layers, TrackKey);
            var map = TrackMap.Load(_serverConfig.CSPTrackOptions.Track, _serverConfig.Server.TrackConfig, roots, grid);
            lock (_lock) _map = map;
            Log.Information("Web portal: track map {Track}, {Length:F0} m, {Sectors} sectors", TrackKey, map.Line.Length, map.SectorSplits.Count + 1);
        }
        catch (Exception ex)
        {
            Log.Warning("Web portal: no track map, the pages show no map and no gaps ({Message})", ex.Message);
        }
    }, stoppingToken);

    private DrivenCarInfo? Driven(EntryCar car)
    {
        foreach (var d in _drivers)
            if (d.Get(car) is { } info) return info;
        return null;
    }

    /// <summary>Everything of this moment, read at once.</summary>
    public LiveFrame Snapshot()
    {
        lock (_lock)
        {
            var session = _sessionManager.CurrentSession;
            var weather = _weatherManager.CurrentWeather;
            var type = session.Configuration.Type;
            var f = new LiveFrame
            {
                Server = _serverConfig.Server.Name,
                Track = TrackKey,
                Session = session.Configuration.Name,
                SessionType = type.ToString(),
                TimeLeft = session.Configuration.Laps > 0 && type == SessionType.Race ? null : session.TimeLeftMilliseconds / 1000,
                Laps = session.Configuration.Laps,
                LeaderLap = (int)session.LeaderLapCount,
                RaceStarted = type == SessionType.Race && _sessionManager.ServerTimeMilliseconds >= session.StartTimeMilliseconds,
                ServerMs = _sessionManager.ServerTimeMilliseconds,
                SessionStart = session.StartTimeMilliseconds,
                Weather = weather.Type.WeatherFxType.ToString(),
                Ambient = weather.TemperatureAmbient,
                Road = weather.TemperatureRoad,
                Rain = weather.RainIntensity,
                Wetness = weather.RainWetness,
                Water = weather.RainWater,
                Grip = weather.TrackGrip,
                TimeOfDay = _weatherManager.CurrentDateTime.TimeOfDay.ToString("HH:mm", null),
                Sectors = (_map?.SectorSplits.Count ?? 0) + 1,
            };

            foreach (var car in _entryCarManager.EntryCars)
            {
                var driven = Driven(car);
                var client = car.Client;
                if (driven == null && client == null) continue;
                var st = driven?.Status ?? car.Status;
                var result = session.Results != null && session.Results.TryGetValue(car.SessionId, out var r) ? r : null;
                var c = new LiveCar
                {
                    Id = car.SessionId,
                    Name = driven?.Name ?? client!.Name ?? "",
                    Model = car.Model,
                    Kind = driven?.Kind ?? "player",
                    Guid = driven?.Guid ?? client!.Guid,
                    X = st.Position.X, Z = st.Position.Z, Vx = st.Velocity.X, Vz = st.Velocity.Z,
                    Speed = st.Velocity.Length() * 3.6f,
                    Gear = st.Gear - 1, // network: 0 = reverse, 1 = neutral
                    Rpm = st.EngineRpm,
                    Throttle = st.Gas / 255f,
                    Brake = (st.StatusFlag & CarStatusFlags.BrakeLightsOn) != 0,
                    Laps = result?.NumLaps ?? 0,
                    Best = result?.BestLap ?? 0,
                    Last = result?.LastLap ?? 0,
                    Finished = result?.HasCompletedLastLap ?? false,
                };
                if (_map != null)
                {
                    int hint = _hints.TryGetValue(car.SessionId, out var h) ? h : -1;
                    var (fraction, sector, index) = _map.Locate(st.Position, hint);
                    _hints[car.SessionId] = index;
                    c.LapFraction = fraction;
                    c.Sector = sector;
                }
                if (driven != null)
                {
                    c.InPit = driven.InPitLane;
                    c.Stops = driven.PitStops;
                    c.Tyres = driven.TyrePercent;
                }
                else
                {
                    c.InPit = _drivers.Select(d => d.PlayerInPitLane(car)).FirstOrDefault(x => x != null) ?? false;
                    c.Admin = client!.IsAdministrator;
                    c.Ping = car.Ping;
                    c.Jitter = car.PingJitter;
                }
                f.Cars.Add(c);
            }
            return f;
        }
    }

    private static float R(float v) => MathF.Round(v, 1);
    private static float? Lap(uint ms) => ms is 0 or >= 999999999 ? null : ms / 1000f;

    /// <summary>Track, pit lane, start line, sector splits and section names in world X/Z (for the maps).</summary>
    public object TrackOutline()
    {
        var map = _map;
        if (map == null) return new { available = false };
        var line = map.Line;
        var pts = new List<float[]>();
        for (float s = 0; s < line.Length; s += 8)
        {
            var p = line.PositionAt(s, 0);
            var i = line.IndexAt(s);
            var l = line.PositionAt(s, -line.RoomMinus[i]);
            var rr = line.PositionAt(s, line.RoomPlus[i]);
            pts.Add([R(p.X), R(p.Z), R(l.X), R(l.Z), R(rr.X), R(rr.Z)]);
        }
        var pit = new List<float[]>();
        if (map.PitLane is { } lane)
            for (float s = 0; s < lane.Length; s += 6)
            {
                var p = lane.PositionAt(s, 0);
                pit.Add([R(p.X), R(p.Z)]);
            }
        var marks = new List<object>();
        var start = line.PositionAt(map.StartLineS, 0);
        marks.Add(new { kind = "start", x = R(start.X), z = R(start.Z), name = "Start/Ziel" });
        int n = 2;
        foreach (var split in map.SectorSplits)
        {
            var p = line.PositionAt(line.WrapS(split + map.StartLineS), 0);
            marks.Add(new { kind = "sector", x = R(p.X), z = R(p.Z), name = $"S{n++}" });
        }
        foreach (var sec in map.Info.Sections)
        {
            float mid = (sec.Start + sec.End) / 2;
            if (sec.End < sec.Start) mid = (sec.Start + sec.End + 1) / 2 % 1;
            var p = line.PositionAt(line.WrapS(mid * line.Length + map.StartLineS), 0);
            marks.Add(new { kind = "section", x = R(p.X), z = R(p.Z), name = sec.Name });
        }
        return new { available = true, track = _serverConfig.Server.Track, length = line.Length, line = pts, pit, marks };
    }

    /// <summary>The admin page's view of the server: session, weather, cars (bot details come from the bot plugin's API).</summary>
    public object AdminState()
    {
        var f = Snapshot();
        bool race = f.SessionType == nameof(SessionType.Race);
        var cars = f.Cars.Select(c =>
        {
            var d = new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["name"] = c.Name,
                ["model"] = c.Model,
                ["bot"] = c.Kind != "player",
                ["kind"] = c.Kind,
                ["x"] = R(c.X), ["z"] = R(c.Z),
                ["speed"] = MathF.Round(c.Speed),
                ["vx"] = R(c.Vx), ["vz"] = R(c.Vz),
                ["laps"] = c.Laps,
                ["best"] = Lap(c.Best),
                ["last"] = Lap(c.Last),
                ["finished"] = c.Finished,
                ["progress"] = c.Laps + c.LapFraction,
                ["sector"] = c.Sector,
                ["inPit"] = c.InPit,
            };
            if (c.Kind == "player")
            {
                d["admin"] = c.Admin;
                d["ping"] = c.Ping;
                d["jitter"] = MathF.Round(c.Jitter);
                d["guid"] = c.Guid.ToString();
            }
            return d;
        }).ToList();
        var ordered = race
            ? cars.OrderByDescending(c => (float)c["progress"]!).ToList()
            : cars.OrderBy(c => (float?)c["best"] ?? float.MaxValue).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i]["pos"] = i + 1;

        List<Dictionary<string, object?>>? lastRace;
        DateTime lastRaceAt;
        lock (_lock)
        {
            lastRace = _lastRace;
            lastRaceAt = _lastRaceAt;
        }
        return new
        {
            server = f.Server,
            track = f.Track,
            session = new { name = f.Session, type = f.SessionType, timeLeft = f.TimeLeft, laps = f.Laps, leaderLap = f.LeaderLap, raceStarted = f.RaceStarted },
            weather = new
            {
                type = f.Weather,
                ambient = MathF.Round(f.Ambient, 1),
                road = MathF.Round(f.Road, 1),
                rain = MathF.Round(f.Rain, 2),
                wetness = MathF.Round(f.Wetness, 2),
                water = MathF.Round(f.Water, 2),
                grip = MathF.Round(f.Grip, 3),
                time = f.TimeOfDay
            },
            cars = ordered,
            lastRace = lastRace == null ? null : new { at = lastRaceAt.ToString("HH:mm"), rows = lastRace },
            health = Health()
        };
    }

    // ---- the last race's classification for the admin page
    private void OnSessionChanged(SessionManager sender, SessionChangedEventArgs args)
    {
        var race = args.PreviousSession;
        if (race?.Configuration.Type != SessionType.Race || race.Results == null) return;
        var rows = race.Results.Where(r => r.Value.NumLaps > 0).OrderByDescending(r => r.Value.NumLaps).ThenBy(r => r.Value.TotalTime).ToList();
        if (rows.Count == 0) return;
        var lead = rows[0].Value;
        bool de = ChatLanguage == "de";
        string Gap(EntryCarResult r)
        {
            if (r == lead) return "";
            uint down = lead.NumLaps - r.NumLaps;
            if (down > 0) return de ? $"+{down} Rd." : $"+{down} lap{(down > 1 ? "s" : "")}";
            return $"+{(r.TotalTime - lead.TotalTime) / 1000.0:F1} s";
        }
        string Total(uint ms) => TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");
        var table = rows.Select((r, i) => new Dictionary<string, object?>
        {
            ["pos"] = i + 1,
            ["name"] = string.IsNullOrEmpty(r.Value.Name) ? "?" : r.Value.Name,
            ["bot"] = _entryCarManager.EntryCars.FirstOrDefault(c => c.SessionId == r.Key) is { } car && Driven(car) != null,
            ["laps"] = r.Value.NumLaps,
            ["gap"] = Gap(r.Value),
            ["best"] = r.Value.BestLap < 999_999_999u ? Total(r.Value.BestLap) : ""
        }).ToList();
        lock (_lock)
        {
            _lastRace = table;
            _lastRaceAt = DateTime.Now;
        }
    }

    // ---- server health: CPU, memory
    private TimeSpan _lastCpu;
    private long _lastCpuAt;
    private double _cpuPercent;
    private readonly object _healthLock = new();

    private object Health()
    {
        lock (_healthLock)
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            var cpu = p.TotalProcessorTime;
            if (_lastCpuAt != 0)
            {
                double wall = System.Diagnostics.Stopwatch.GetElapsedTime(_lastCpuAt, now).TotalSeconds;
                if (wall >= 1) // average over at least a second
                {
                    _cpuPercent = (cpu - _lastCpu).TotalSeconds / wall / Environment.ProcessorCount * 100;
                    _lastCpu = cpu;
                    _lastCpuAt = now;
                }
            }
            else
            {
                _lastCpu = cpu;
                _lastCpuAt = now;
            }
            return new
            {
                cpu = Math.Round(_cpuPercent, 1),
                cores = Environment.ProcessorCount,
                ramMb = p.WorkingSet64 / 1048576,
                heapMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
                uptimeMin = (int)(DateTime.Now - p.StartTime).TotalMinutes
            };
        }
    }
}
