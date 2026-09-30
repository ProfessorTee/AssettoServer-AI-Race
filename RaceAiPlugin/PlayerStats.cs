using System.Globalization;
using System.Text.Json;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace RaceAiPlugin;

/// <summary>
/// Player statistics: best laps (all time and per week) per track and car, and a safety rating from contacts, off-track moments
/// and cuts per distance driven (recent driving counts more). Stored in stats/players.json, shown with /top, /profile and on
/// the public page /raceai/stats.
/// </summary>
public sealed class PlayerStats : BackgroundService
{
    public sealed class BestLap
    {
        public uint Ms { get; set; }
        public DateTime Date { get; set; }
    }

    public sealed class TrackCarStats
    {
        public BestLap? Best { get; set; }
        /// <summary>Best lap per ISO week ("2026-W40").</summary>
        public Dictionary<string, BestLap> Weeks { get; set; } = new();
        public int Laps { get; set; }
        public int CleanLaps { get; set; }
    }

    public sealed class Player
    {
        public string Name { get; set; } = "";
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
        public double Km { get; set; }
        public int Laps { get; set; }
        public int CleanLaps { get; set; }
        public int CarContacts { get; set; }
        public int WallContacts { get; set; }
        public int Cuts { get; set; }
        public int Races { get; set; }
        public int Finishes { get; set; }
        public int Wins { get; set; }
        public int Podiums { get; set; }
        /// <summary>Decaying sums for the safety rating (half-life about 300 km).</summary>
        public double RecentKm { get; set; }
        public double RecentIncidents { get; set; }
        /// <summary>"track|car" -> laps and best times.</summary>
        public Dictionary<string, TrackCarStats> Tracks { get; set; } = new();
    }

    private const string FilePath = "stats/players.json";
    private const double HalfLifeKm = 300;
    private readonly EntryCarManager _entryCarManager;
    private readonly SessionManager _sessionManager;
    private readonly RaceAiService _service;
    private readonly RaceAiConfiguration _config;
    private readonly object _lock = new();
    private Dictionary<ulong, Player> _players = new();
    private bool _dirty;
    private readonly Dictionary<ACTcpClient, DateTime> _lastIncident = new();

    public PlayerStats(EntryCarManager entryCarManager, SessionManager sessionManager, RaceAiService service, RaceAiConfiguration config)
    {
        _entryCarManager = entryCarManager;
        _sessionManager = sessionManager;
        _service = service;
        _config = config;
        Load();
        _entryCarManager.ClientConnected += (c, _) =>
        {
            c.LapCompleted += OnLap;
            c.Collision += OnCollision;
            lock (_lock) { var p = Get(c.Guid, c.Name); p.LastSeen = DateTime.UtcNow; _dirty = true; }
        };
        _entryCarManager.ClientDisconnected += (c, _) =>
        {
            c.LapCompleted -= OnLap;
            c.Collision -= OnCollision;
            lock (_lock) _lastIncident.Remove(c);
        };
        _sessionManager.SessionChanged += OnSessionChanged;
    }

    private string T(string en, string de) => _config.ChatLanguage == "de" ? de : en;

    public static string Week(DateTime utc) => $"{ISOWeek.GetYear(utc)}-W{ISOWeek.GetWeekOfYear(utc):00}";

    private Player Get(ulong guid, string? name)
    {
        if (!_players.TryGetValue(guid, out var p))
        {
            p = new Player { FirstSeen = DateTime.UtcNow };
            _players[guid] = p;
        }
        if (!string.IsNullOrWhiteSpace(name)) p.Name = name;
        return p;
    }

    // ------------------------------------------------------------------ events

    private void OnLap(ACTcpClient client, LapCompletedEventArgs args)
    {
        uint ms = args.Packet.LapTime;
        int cuts = args.Packet.Cuts;
        float lengthKm = _service.TrackLengthMeters / 1000f;
        string key = $"{_service.TrackKeyName}|{client.EntryCar.Model}";
        lock (_lock)
        {
            var p = Get(client.Guid, client.Name);
            p.Laps++;
            p.Cuts += cuts;
            if (lengthKm > 0) AddDistance(p, lengthKm);
            AddIncidents(p, cuts);
            if (!p.Tracks.TryGetValue(key, out var tc)) p.Tracks[key] = tc = new TrackCarStats();
            tc.Laps++;
            // a lap counts for the times when it's clean and plausible (not the first lap out of the pits or a standing start)
            bool clean = cuts == 0 && ms > 10_000 && ms < 3_600_000;
            if (clean)
            {
                p.CleanLaps++;
                tc.CleanLaps++;
                var now = DateTime.UtcNow;
                bool record = tc.Best == null || ms < tc.Best.Ms;
                if (record) tc.Best = new BestLap { Ms = ms, Date = now };
                var week = Week(now);
                if (!tc.Weeks.TryGetValue(week, out var wb) || ms < wb.Ms) tc.Weeks[week] = new BestLap { Ms = ms, Date = now };
                // only the last 8 weeks
                foreach (var old in tc.Weeks.Keys.OrderByDescending(k => k, StringComparer.Ordinal).Skip(8).ToList()) tc.Weeks.Remove(old);
                if (record && tc.CleanLaps > 1)
                    client.SendChatMessage(T($"New personal best on this track: {Fmt(ms)}", $"Neue persönliche Bestzeit auf dieser Strecke: {Fmt(ms)}"));
            }
            _dirty = true;
        }
        _service.DuelLap(client, ms, cuts);
    }

    private void OnCollision(ACTcpClient client, CollisionEventArgs args)
    {
        float kmh = args.Speed;
        if (kmh < 8) return; // a nudge
        lock (_lock)
        {
            // one incident per second at most (a long scrape along the wall sends many events)
            var now = DateTime.UtcNow;
            if (_lastIncident.TryGetValue(client, out var last) && (now - last).TotalSeconds < 1.5) return;
            _lastIncident[client] = now;
            var p = Get(client.Guid, client.Name);
            if (args.TargetCar != null)
            {
                p.CarContacts++;
                AddIncidents(p, kmh < 25 ? 2 : kmh < 60 ? 4 : 6);
            }
            else
            {
                p.WallContacts++;
                AddIncidents(p, kmh < 30 ? 1 : 2);
            }
            _dirty = true;
        }
    }

    private void OnSessionChanged(SessionManager sender, SessionChangedEventArgs args)
    {
        var prev = args.PreviousSession;
        if (prev?.Configuration.Type != SessionType.Race || prev.Results == null) return;
        lock (_lock)
        {
            foreach (var (_, r) in prev.Results)
            {
                if (r.Guid == 0 || r.NumLaps == 0) continue;
                var p = Get(r.Guid, r.Name);
                p.Races++;
                if (r.HasCompletedLastLap) p.Finishes++;
                if (r.RacePos == 1) p.Wins++;
                if (r.RacePos is >= 1 and <= 3) p.Podiums++;
            }
            _dirty = true;
        }
    }

    private static void AddDistance(Player p, double km)
    {
        p.Km += km;
        double decay = Math.Pow(0.5, km / HalfLifeKm);
        p.RecentKm = p.RecentKm * decay + km;
        p.RecentIncidents *= decay;
    }

    private static void AddIncidents(Player p, double points) => p.RecentIncidents += points;

    // ------------------------------------------------------------------ rating

    /// <summary>Safety rating 0-5 from incident points per 10 km of recent driving; null until 20 km were driven.</summary>
    public static double? SafetyRating(Player p)
    {
        if (p.RecentKm < 20) return null;
        double per10 = p.RecentIncidents / p.RecentKm * 10;
        return Math.Round(Math.Clamp(5 - per10 * 0.8, 0, 5), 2);
    }

    public static string SafetyClass(double? sr) => sr switch
    {
        null => "-",
        >= 4.0 => "A",
        >= 3.0 => "B",
        >= 2.0 => "C",
        >= 1.0 => "D",
        _ => "R"
    };

    public static string Fmt(uint ms) => TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss\.fff");

    // ------------------------------------------------------------------ queries

    public sealed record TopEntry(string Name, string Car, uint Ms, DateTime Date);

    public List<TopEntry> Top(string track, bool week, int count = 10)
    {
        string wk = Week(DateTime.UtcNow);
        lock (_lock)
        {
            var list = new List<TopEntry>();
            foreach (var p in _players.Values)
            {
                TopEntry? best = null;
                foreach (var (key, tc) in p.Tracks)
                {
                    int bar = key.IndexOf('|');
                    if (bar < 0 || key[..bar] != track) continue;
                    var b = week ? (tc.Weeks.TryGetValue(wk, out var w) ? w : null) : tc.Best;
                    if (b != null && (best == null || b.Ms < best.Ms)) best = new TopEntry(p.Name, key[(bar + 1)..], b.Ms, b.Date);
                }
                if (best != null) list.Add(best);
            }
            return list.OrderBy(e => e.Ms).Take(count).ToList();
        }
    }

    public object Overview()
    {
        string track = _service.TrackKeyName;
        lock (_lock)
        {
            var tracks = _players.Values.SelectMany(p => p.Tracks.Keys).Select(k => k.Split('|')[0]).Distinct().OrderBy(t => t).ToList();
            return new
            {
                current = track,
                week = Week(DateTime.UtcNow),
                tracks = tracks.Select(t => new
                {
                    track = t,
                    week = Top(t, true, 20).Select(e => new { e.Name, e.Car, time = Fmt(e.Ms), ms = e.Ms, date = e.Date }),
                    all = Top(t, false, 20).Select(e => new { e.Name, e.Car, time = Fmt(e.Ms), ms = e.Ms, date = e.Date })
                }),
                players = _players.Values.OrderByDescending(p => SafetyRating(p) ?? -1).ThenByDescending(p => p.Km).Select(p => new
                {
                    p.Name,
                    km = Math.Round(p.Km, 1),
                    p.Laps,
                    p.CleanLaps,
                    p.CarContacts,
                    p.WallContacts,
                    p.Cuts,
                    p.Races,
                    p.Finishes,
                    p.Wins,
                    p.Podiums,
                    sr = SafetyRating(p),
                    srClass = SafetyClass(SafetyRating(p)),
                    lastSeen = p.LastSeen,
                    bests = p.Tracks.Where(t => t.Value.Best != null).Select(t => new { track = t.Key.Split('|')[0], car = t.Key.Split('|')[1], time = Fmt(t.Value.Best!.Ms), laps = t.Value.Laps })
                })
            };
        }
    }

    public string ProfileText(ulong guid, string? name)
    {
        lock (_lock)
        {
            var p = _players.TryGetValue(guid, out var own) && (string.IsNullOrWhiteSpace(name) || own.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                ? own
                : _players.Values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(name) && x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (p == null) return T("No statistics yet.", "Noch keine Statistik.");
            var sr = SafetyRating(p);
            string key = _service.TrackKeyName + "|";
            var here = p.Tracks.Where(t => t.Key.StartsWith(key) && t.Value.Best != null).OrderBy(t => t.Value.Best!.Ms).FirstOrDefault();
            string best = here.Value?.Best != null ? $"{Fmt(here.Value.Best.Ms)} ({here.Key[key.Length..]})" : "-";
            return T($"{p.Name}: safety {SafetyClass(sr)} {(sr?.ToString("F2", CultureInfo.InvariantCulture) ?? "(after 20 km)")}, {p.Km:F0} km, {p.Laps} laps ({p.CleanLaps} clean), " +
                     $"contacts {p.CarContacts} cars / {p.WallContacts} walls, cuts {p.Cuts}, races {p.Races} (wins {p.Wins}, podiums {p.Podiums}), best here {best}",
                $"{p.Name}: Sicherheit {SafetyClass(sr)} {(sr?.ToString("F2", CultureInfo.InvariantCulture) ?? "(ab 20 km)")}, {p.Km:F0} km, {p.Laps} Runden ({p.CleanLaps} sauber), " +
                $"Kontakte {p.CarContacts} Autos / {p.WallContacts} Streckenrand, Cuts {p.Cuts}, Rennen {p.Races} (Siege {p.Wins}, Podien {p.Podiums}), Bestzeit hier {best}");
        }
    }

    // ------------------------------------------------------------------ storage

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _players = JsonSerializer.Deserialize<Dictionary<ulong, Player>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Race AI: stats/players.json not readable, starting new statistics");
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { /* ignored */ }
        }
    }

    private void Save()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;
            json = JsonSerializer.Serialize(_players, new JsonSerializerOptions { WriteIndented = true });
        }
        Directory.CreateDirectory("stats");
        File.WriteAllText(FilePath + ".tmp", json);
        File.Move(FilePath + ".tmp", FilePath, true);
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(token))
                try { Save(); } catch (Exception ex) { Log.Warning(ex, "Race AI: statistics not saved"); }
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { Save(); } catch { /* ignored */ }
        }
    }
}
