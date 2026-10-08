using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AssettoServer.Server.Extensions;
using Serilog;

namespace RaceAiPlugin;

/// <summary>
/// Public live timing for /raceai/live: builds one frame a few times per second (only while somebody watches), works out positions
/// and time gaps (timing points every ~20 m), and pushes the same bytes to every viewer as Server-Sent Events.
/// No admin data in here (no Steam IDs, no AI internals).
/// </summary>
public sealed class LiveFeed
{
    private readonly RaceAiService _service;
    private readonly IReadOnlyList<IPlayerRating> _ratings;
    private readonly RaceAiConfiguration _config;
    private readonly object _lock = new();
    private readonly List<Channel<byte[]>> _subscribers = [];
    private bool _running;
    private byte[] _latest = [];
    private DateTime _latestAt = DateTime.MinValue;
    private const int MaxViewers = 100;

    public LiveFeed(RaceAiService service, IEnumerable<IPlayerRating> ratings, RaceAiConfiguration config)
    {
        _service = service;
        _ratings = ratings.ToList();
        _config = config;
    }

    public bool Enabled => _config.LiveView;
    private int Hz => Math.Clamp(_config.LiveViewHz, 1, 10);

    // ------------------------------------------------------------------ viewers

    public ChannelReader<byte[]>? Subscribe()
    {
        var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_lock)
        {
            if (_subscribers.Count >= MaxViewers) return null;
            _subscribers.Add(ch);
            if (_latest.Length > 0) ch.Writer.TryWrite(_latest);
            if (!_running)
            {
                _running = true;
                _ = Task.Run(LoopAsync);
            }
        }
        return ch.Reader;
    }

    public void Unsubscribe(ChannelReader<byte[]> reader)
    {
        lock (_lock) _subscribers.RemoveAll(c => c.Reader == reader);
    }

    public int Viewers { get { lock (_lock) return _subscribers.Count; } }

    /// <summary>For browsers that can't keep a stream open: the newest frame (built on demand, at most a few times per second).</summary>
    public byte[] Latest()
    {
        lock (_lock)
        {
            if (_running && _latest.Length > 0) return _latest;
            if ((DateTime.UtcNow - _latestAt).TotalMilliseconds < 1000.0 / Hz && _latest.Length > 0) return _latest;
            _latest = BuildJson();
            _latestAt = DateTime.UtcNow;
            return _latest;
        }
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync())
            {
                Channel<byte[]>[] subs;
                lock (_lock)
                {
                    if (_subscribers.Count == 0)
                    {
                        _running = false;
                        return;
                    }
                    subs = _subscribers.ToArray();
                }
                byte[] json;
                try { json = BuildJson(); }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Race AI: live frame failed");
                    continue;
                }
                var sse = Encoding.UTF8.GetBytes("data: ").Concat(json).Concat("\n\n"u8.ToArray()).ToArray();
                lock (_lock)
                {
                    _latest = json;
                    _latestAt = DateTime.UtcNow;
                }
                foreach (var s in subs) s.Writer.TryWrite(sse);
            }
        }
        finally
        {
            lock (_lock) _running = false;
        }
    }

    // ------------------------------------------------------------------ timing

    private sealed class Timing
    {
        public string Key = "";
        public double P = double.NaN, T;
        public readonly Dictionary<long, double> Pass = new();
        public readonly Queue<long> Order = new();
        public bool InPit;
        public uint Best;
        public int Pos;
    }

    private readonly Dictionary<byte, Timing> _timing = new();
    private string _sessionKey = "";
    private bool _wasStarted;
    private uint _sessionBest;
    private readonly List<(string Time, string Text)> _events = [];
    private int _markers = 500;
    private byte _leader = 255;

    private void Event(RaceAiService.LiveFrame f, string text)
    {
        long ms = Math.Max(0, f.ServerMs - f.SessionStart);
        _events.Add((TimeSpan.FromMilliseconds(ms).ToString(ms >= 3600_000 ? @"h\:mm\:ss" : @"mm\:ss"), text));
        if (_events.Count > 14) _events.RemoveAt(0);
    }

    private static string FmtLap(uint ms) => TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss\.fff");

    private byte[] BuildJson()
    {
        var f = _service.LiveSnapshot();
        double now = f.ServerMs / 1000.0;
        bool race = f.SessionType == "Race";
        float length = _service.TrackLengthMeters;
        _markers = (int)Math.Clamp(length / 20, 50, 2500);

        lock (_timing)
        {
            string key = $"{f.Track}|{f.Session}|{f.SessionStart}";
            if (key != _sessionKey)
            {
                _sessionKey = key;
                _timing.Clear();
                _events.Clear();
                _sessionBest = 0;
                _leader = 255;
            }
            if (race && f.RaceStarted && !_wasStarted)
            {
                foreach (var t in _timing.Values) { t.P = double.NaN; t.Pass.Clear(); t.Order.Clear(); }
                Event(f, "Start!");
            }
            _wasStarted = race && f.RaceStarted;

            // progress along the race (laps + fraction), timing points, events
            var progress = new Dictionary<byte, double>();
            foreach (var c in f.Cars)
            {
                string ckey = $"{c.Kind}|{c.Name}";
                if (!_timing.TryGetValue(c.Id, out var t) || t.Key != ckey)
                {
                    t = new Timing { Key = ckey, InPit = c.InPit, Best = c.Best };
                    _timing[c.Id] = t;
                }
                double p = c.Laps + c.LapFraction;
                if (double.IsNaN(t.P))
                {
                    if (race && c.Laps == 0 && c.LapFraction > 0.5) p -= 1; // on the grid behind the start line
                }
                else
                {
                    while (p < t.P - 0.5) p += 1; // crossed the line, lap not counted yet
                    while (p > t.P + 0.5) p -= 1; // counted before the line
                    long k0 = (long)Math.Floor(t.P * _markers), k1 = (long)Math.Floor(p * _markers);
                    if (k1 > k0 && k1 - k0 < _markers / 2 && p > t.P)
                    {
                        for (long k = k0 + 1; k <= k1; k++)
                        {
                            double at = t.T + ((double)k / _markers - t.P) / (p - t.P) * (now - t.T);
                            t.Pass[k] = at;
                            t.Order.Enqueue(k);
                        }
                        while (t.Order.Count > _markers * 3 / 2 + 10) t.Pass.Remove(t.Order.Dequeue());
                    }
                }
                t.P = p;
                t.T = now;
                progress[c.Id] = p;

                if (c.InPit != t.InPit && race && f.RaceStarted)
                    Event(f, c.InPit ? $"{c.Name} fährt in die Box" : $"{c.Name} verlässt die Box");
                t.InPit = c.InPit;
                if (c.Best is > 0 and < 999999999 && c.Best != t.Best)
                {
                    if (_sessionBest == 0 || c.Best < _sessionBest)
                    {
                        if (_sessionBest != 0 || !race) Event(f, $"Schnellste Runde: {c.Name} {FmtLap(c.Best)}");
                        _sessionBest = c.Best;
                    }
                    t.Best = c.Best;
                }
            }

            // order
            var cars = race
                ? f.Cars.OrderByDescending(c => c.Finished).ThenByDescending(c => progress[c.Id]).ToList()
                : f.Cars.OrderBy(c => c.Best is > 0 and < 999999999 ? c.Best : uint.MaxValue).ToList();
            if (race && f.RaceStarted && cars.Count > 0 && cars[0].Id != _leader)
            {
                if (_leader != 255 && _timing.ContainsKey(_leader)) Event(f, $"{cars[0].Name} übernimmt die Führung");
                _leader = cars[0].Id;
            }

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("server", f.Server);
                w.WriteString("track", f.Track);
                w.WriteNumber("length", MathF.Round(length));
                w.WriteNumber("t", f.ServerMs);
                w.WriteNumber("hz", Hz);
                w.WriteStartObject("session");
                w.WriteString("name", f.Session);
                w.WriteString("type", f.SessionType);
                if (f.TimeLeft is { } tl) w.WriteNumber("timeLeft", tl); else w.WriteNull("timeLeft");
                w.WriteNumber("laps", f.Laps);
                w.WriteNumber("leaderLap", f.LeaderLap);
                w.WriteBoolean("started", f.RaceStarted);
                w.WriteNumber("sectors", f.Sectors);
                w.WriteEndObject();
                w.WriteStartObject("weather");
                w.WriteString("type", f.Weather);
                w.WriteNumber("ambient", MathF.Round(f.Ambient));
                w.WriteNumber("road", MathF.Round(f.Road));
                w.WriteNumber("rain", MathF.Round(f.Rain, 2));
                w.WriteNumber("wet", MathF.Round(f.Wetness, 2));
                w.WriteString("time", f.TimeOfDay);
                w.WriteEndObject();

                w.WriteStartArray("cars");
                RaceAiService.LiveCar? first = cars.Count > 0 ? cars[0] : null;
                for (int i = 0; i < cars.Count; i++)
                {
                    var c = cars[i];
                    w.WriteStartObject();
                    w.WriteNumber("id", c.Id);
                    w.WriteNumber("pos", i + 1);
                    w.WriteString("n", c.Name);
                    w.WriteString("m", c.Model);
                    w.WriteString("k", c.Kind);
                    if (c.Kind != "ai" && _ratings.Select(r => r.Licence(c.Guid)).FirstOrDefault(l => l != null) is { } lic) w.WriteString("lic", lic);
                    w.WriteNumber("x", MathF.Round(c.X, 1));
                    w.WriteNumber("z", MathF.Round(c.Z, 1));
                    w.WriteNumber("vx", MathF.Round(c.Vx, 1));
                    w.WriteNumber("vz", MathF.Round(c.Vz, 1));
                    w.WriteNumber("v", MathF.Round(c.Speed));
                    w.WriteNumber("g", c.Gear);
                    w.WriteNumber("rpm", c.Rpm);
                    w.WriteNumber("thr", MathF.Round(c.Throttle, 2));
                    w.WriteBoolean("brk", c.Brake);
                    w.WriteNumber("lap", c.Laps);
                    w.WriteNumber("lf", MathF.Round(c.LapFraction, 4));
                    w.WriteNumber("sec", c.Sector);
                    w.WriteBoolean("pit", c.InPit);
                    if (c.Kind != "player") w.WriteNumber("stops", c.Stops);
                    if (c.Tyres is { } ty) w.WriteNumber("tyre", ty);
                    if (c.Best is > 0 and < 999999999) w.WriteNumber("best", c.Best); else w.WriteNull("best");
                    if (c.Last is > 0 and < 999999999) w.WriteNumber("last", c.Last); else w.WriteNull("last");
                    w.WriteBoolean("fin", c.Finished);
                    if (i > 0 && first != null)
                    {
                        if (race)
                        {
                            WriteGap(w, "gap", first, c, progress, now);
                            WriteGap(w, "int", cars[i - 1], c, progress, now);
                        }
                        else if (c.Best is > 0 and < 999999999)
                        {
                            if (first.Best is > 0 and < 999999999) w.WriteNumber("gap", (c.Best - (double)first.Best) / 1000);
                            var prev = cars[i - 1];
                            if (prev.Best is > 0 and < 999999999) w.WriteNumber("int", (c.Best - (double)prev.Best) / 1000);
                        }
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("events");
                for (int i = _events.Count - 1; i >= 0; i--)
                {
                    w.WriteStartObject();
                    w.WriteString("t", _events[i].Time);
                    w.WriteString("text", _events[i].Text);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("viewers", Viewers);
                w.WriteEndObject();
            }
            return ms.ToArray();
        }
    }

    /// <summary>Time behind <paramref name="ahead"/>: when it passed the timing point <paramref name="c"/> is at now; whole laps when lapped.</summary>
    private void WriteGap(Utf8JsonWriter w, string name, RaceAiService.LiveCar ahead, RaceAiService.LiveCar c, Dictionary<byte, double> progress, double now)
    {
        double pa = progress[ahead.Id], pc = progress[c.Id];
        if (pa - pc >= 1)
        {
            w.WriteString(name, $"+{(int)Math.Floor(pa - pc)} Rd.");
            return;
        }
        if (!_timing.TryGetValue(ahead.Id, out var ta)) return;
        double km = pc * _markers;
        long k = (long)Math.Floor(km);
        if (!ta.Pass.TryGetValue(k, out var at)) return;
        double gap = now - at;
        if (ta.Pass.TryGetValue(k + 1, out var next)) gap -= (km - k) * (next - at);
        if (gap < 0 || gap > 3600) return;
        w.WriteNumber(name, Math.Round(gap, 1));
    }
}
