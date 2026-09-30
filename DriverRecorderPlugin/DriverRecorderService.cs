using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Network.Packets.Shared;
using DriverRecorderPlugin.Packets;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DriverRecorderPlugin;

/// <summary>
/// Records the driving of players who agreed (/rec on): the CSP script sends inputs, position and speed, the server cuts them into laps
/// and stores every lap as a gzip CSV in RecordingsFolder/&lt;steam id&gt;/&lt;track&gt;/&lt;car&gt;/. The Race AI builds driver profiles from these.
/// </summary>
public sealed class DriverRecorderService : IHostedService
{
    public const int FormatVersion = 1;

    private readonly DriverRecorderConfiguration _config;
    private readonly ACServerConfiguration _serverConfig;
    private readonly EntryCarManager _entryCarManager;
    private readonly string _root;
    private readonly string _trackKey;
    private readonly object _lock = new();
    private readonly Dictionary<ulong, OptIn> _optIns = new();
    private readonly ConcurrentDictionary<ACTcpClient, Recorder> _recorders = new();

    private sealed record OptIn(string Name, DateTime Since);

    private sealed class Sample
    {
        public float Time, Spline, Speed, Steer;
        public Vector3 Position;
        public byte Gas, Brake, Clutch, Gear, Flags;
        public float TyreFront, TyreRear, Fuel;
    }

    private sealed class LapCandidate
    {
        public required List<Sample> Samples;
        public required DateTime At;
        public bool Complete;
    }

    private sealed class Recorder
    {
        public readonly List<Sample> Buffer = new();
        public readonly List<LapCandidate> Waiting = new();
        public readonly List<(uint LapTime, int Cuts, DateTime At)> LapInfos = new();
        public float TyreFront, TyreRear, Fuel;
        public bool Recording;
        public int Saved;
        /// <summary>Diagnostics for /rec info: samples received, time of the last one, lap cuts seen.</summary>
        public long Received;
        public DateTime LastSample;
        public int Cuts;
        public float LastSpline = -1;
    }

    public DriverRecorderService(DriverRecorderConfiguration config, ACServerConfiguration serverConfig, EntryCarManager entryCarManager,
        CSPServerScriptProvider scriptProvider, CSPClientMessageTypeManager messageTypes)
    {
        _config = config;
        _serverConfig = serverConfig;
        _entryCarManager = entryCarManager;
        _root = Path.GetFullPath(config.RecordingsFolder);
        _trackKey = TrackKey(serverConfig);
        Directory.CreateDirectory(_root);
        LoadOptIns();

        if (!serverConfig.Extra.EnableClientMessages)
            Log.Warning("DriverRecorder: EnableClientMessages is off in extra_cfg.yml, the server can't receive recordings");

        scriptProvider.AddScript(Assembly.GetExecutingAssembly().GetManifestResourceStream("DriverRecorderPlugin.lua.driverrecorder.lua")!,
            "driverrecorder.lua", new Dictionary<string, object> { ["sampleHz"] = config.SampleHz });
        messageTypes.RegisterOnlineEvent<DrSamplesPacket>(OnSamples);
        messageTypes.RegisterOnlineEvent<DrStatusPacket>(OnStatus);

        _entryCarManager.ClientConnected += OnClientConnected;
        _entryCarManager.ClientDisconnected += OnClientDisconnected;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Log.Information("DriverRecorder: recordings in {Folder}, {Count} players agreed to be recorded (online events {Samples:X8}/{Status:X8}/{Control:X8})",
            _root, _optIns.Count, DrSamplesPacket.PacketType, DrStatusPacket.PacketType, DrControlPacket.PacketType);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Track folder name, e.g. ks_nordschleife-nordschleife.</summary>
    public static string TrackKey(ACServerConfiguration cfg)
    {
        string track = cfg.Server.Track;
        int slash = track.LastIndexOf('/');
        if (slash >= 0) track = track[(slash + 1)..];
        return string.IsNullOrEmpty(cfg.Server.TrackConfig) ? track : $"{track}-{cfg.Server.TrackConfig}";
    }

    private string T(string en, string de) => _config.Language == "de" ? de : en;

    // ------------------------------------------------------------------ consent

    public bool IsOptedIn(ulong guid)
    {
        lock (_lock) return _optIns.ContainsKey(guid);
    }

    public void SetOptIn(ACTcpClient client, bool on)
    {
        lock (_lock)
        {
            if (on) _optIns[client.Guid] = new OptIn(client.Name ?? client.Guid.ToString(), DateTime.UtcNow);
            else _optIns.Remove(client.Guid);
            SaveOptIns();
        }
        Log.Information("DriverRecorder: {Player} {State} recording", client.Name, on ? "agreed to" : "stopped");
        var rec = _recorders.GetOrAdd(client, _ => new Recorder());
        lock (rec)
        {
            rec.Recording = on;
            rec.Buffer.Clear();
            rec.Waiting.Clear();
        }
        SendControl(client, on);
    }

    private void SendControl(ACTcpClient client, bool on)
    {
        try
        {
            var (laps, best) = CleanLaps(client);
            client.SendPacket(new DrControlPacket { Recording = on, SampleHz = (byte)_config.SampleHz, Laps = (ushort)Math.Min(laps, ushort.MaxValue), BestMs = (int)best });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "DriverRecorder: could not send control packet");
        }
    }

    private void LoadOptIns()
    {
        try
        {
            var path = Path.Join(_root, "optin.json");
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, OptIn>>(File.ReadAllText(path));
            if (data == null) return;
            foreach (var (k, v) in data)
                if (ulong.TryParse(k, out var g)) _optIns[g] = v;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DriverRecorder: optin.json not readable");
        }
    }

    private void SaveOptIns()
    {
        var path = Path.Join(_root, "optin.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_optIns.ToDictionary(k => k.Key.ToString(), v => v.Value),
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }

    // ------------------------------------------------------------------ clients

    private void OnClientConnected(ACTcpClient client, EventArgs args)
    {
        _recorders[client] = new Recorder { Recording = IsOptedIn(client.Guid) };
        client.LuaReady += OnLuaReady;
        client.LapCompleted += OnLapCompleted;
        client.FirstUpdateSent += OnFirstUpdate;
    }

    private void OnClientDisconnected(ACTcpClient client, EventArgs args)
    {
        client.LuaReady -= OnLuaReady;
        client.LapCompleted -= OnLapCompleted;
        client.FirstUpdateSent -= OnFirstUpdate;
        if (_recorders.TryRemove(client, out var rec))
            lock (rec) FlushWaiting(client, rec, force: true);
    }

    private void OnLuaReady(ACTcpClient client, EventArgs args)
    {
        if (IsOptedIn(client.Guid)) SendControl(client, true);
    }

    private void OnFirstUpdate(ACTcpClient client, EventArgs args)
    {
        if (!_config.JoinHint) return;
        client.SendChatMessage(IsOptedIn(client.Guid)
            ? T("Driver Recorder: your driving is being recorded for your AI clone. /rec off stops it.",
                "Driver Recorder: deine Fahrweise wird für deinen KI-Klon aufgezeichnet. /rec off beendet das.")
            : T("Driver Recorder: this server can record your driving to build an AI clone of you (it can drive your car if you disconnect in a race). /rec on to agree, /rec info for details.",
                "Driver Recorder: Dieser Server kann deine Fahrweise aufzeichnen und daraus einen KI-Klon von dir bauen (der z. B. dein Auto weiterfährt, wenn du im Rennen rausfliegst). /rec on zum Zustimmen, /rec info für Details."));
    }

    // ------------------------------------------------------------------ data

    private void OnStatus(ACTcpClient client, DrStatusPacket packet)
    {
        if (!_recorders.TryGetValue(client, out var rec) || !rec.Recording) return;
        if (packet.TyreTemp is not { Length: >= 4 } || packet.Fuel is not { Length: >= 1 }) return;
        lock (rec)
        {
            rec.TyreFront = (packet.TyreTemp[0] + packet.TyreTemp[1]) / 2;
            rec.TyreRear = (packet.TyreTemp[2] + packet.TyreTemp[3]) / 2;
            rec.Fuel = packet.Fuel[0];
        }
    }

    private void OnSamples(ACTcpClient client, DrSamplesPacket packet)
    {
        // never let a broken packet disconnect the player: an exception here ends his connection
        try { HandleSamples(client, packet); }
        catch (Exception ex) { LogBadPacket(client, ex); }
    }

    private readonly HashSet<ulong> _badPacketLogged = new();

    private void LogBadPacket(ACTcpClient client, Exception ex)
    {
        lock (_badPacketLogged)
            if (!_badPacketLogged.Add(client.Guid)) return;
        Log.Warning(ex, "DriverRecorder: unreadable data from {Player}, ignored", client.Name);
    }

    private static int ArrayCount(DrSamplesPacket p)
    {
        int n = DrSamplesPacket.Size;
        foreach (var len in new[] { p.Time?.Length ?? 0, p.Spline?.Length ?? 0, p.Position?.Length ?? 0, p.Speed?.Length ?? 0, p.Gas?.Length ?? 0,
                     p.Brake?.Length ?? 0, p.Clutch?.Length ?? 0, p.Steer?.Length ?? 0, p.Gear?.Length ?? 0, p.Flags?.Length ?? 0 })
            n = Math.Min(n, len);
        return n;
    }

    private void HandleSamples(ACTcpClient client, DrSamplesPacket packet)
    {
        if (!_recorders.TryGetValue(client, out var rec) || !rec.Recording) return; // no consent: ignore
        lock (rec)
        {
            if (rec.Received == 0)
                Log.Information("DriverRecorder: receiving data from {Player}", client.Name);
            int n = ArrayCount(packet);
            if (n == 0) return;
            rec.Received += n;
            rec.LastSample = DateTime.UtcNow;
            if (n > 0) rec.LastSpline = packet.Spline[n - 1];
            for (int i = 0; i < n; i++)
            {
                var s = new Sample
                {
                    Time = packet.Time[i], Spline = packet.Spline[i], Position = packet.Position[i], Speed = packet.Speed[i],
                    Gas = packet.Gas[i], Brake = packet.Brake[i], Clutch = packet.Clutch[i], Steer = packet.Steer[i] / 10f,
                    Gear = packet.Gear[i], Flags = packet.Flags[i], TyreFront = rec.TyreFront, TyreRear = rec.TyreRear, Fuel = rec.Fuel
                };
                if (!float.IsFinite(s.Spline) || !float.IsFinite(s.Speed)) continue;
                var prev = rec.Buffer.Count > 0 ? rec.Buffer[^1] : null;
                rec.Buffer.Add(s);
                // crossing the start line: the spline position jumps from ~1 back to ~0
                if (prev != null && prev.Spline > 0.9f && s.Spline < 0.1f)
                {
                    rec.Cuts++;
                    var lap = rec.Buffer.GetRange(0, rec.Buffer.Count - 1);
                    if (DebugOn)
                        Log.Information("DriverRecorder debug: {Player} crossed the line, {Count} samples, {Duration:F1} s, first spline {First:F3}",
                            client.Name, lap.Count, lap.Count > 1 ? lap[^1].Time - lap[0].Time : 0, lap.Count > 0 ? lap[0].Spline : -1);
                    rec.Buffer.RemoveRange(0, rec.Buffer.Count - 1);
                    rec.Waiting.Add(new LapCandidate { Samples = lap, At = DateTime.UtcNow, Complete = lap.Count > 0 && lap[0].Spline < 0.05f });
                }
            }
            if (rec.Buffer.Count > 50 * 60 * 20) rec.Buffer.RemoveRange(0, rec.Buffer.Count - 50 * 60 * 20); // endless lap: keep 20 min
            FlushWaiting(client, rec, force: false);
        }
    }

    private void OnLapCompleted(ACTcpClient client, LapCompletedEventArgs args)
    {
        if (!_recorders.TryGetValue(client, out var rec) || !rec.Recording) return;
        lock (rec)
        {
            rec.LapInfos.Add((args.Packet.LapTime, args.Packet.Cuts, DateTime.UtcNow));
            FlushWaiting(client, rec, force: false);
        }
    }

    /// <summary>Matches the laps cut from the samples with the official lap times (they arrive in either order) and saves them.</summary>
    private void FlushWaiting(ACTcpClient client, Recorder rec, bool force)
    {
        var now = DateTime.UtcNow;
        rec.LapInfos.RemoveAll(l => now - l.At > TimeSpan.FromSeconds(20));
        for (int i = 0; i < rec.Waiting.Count; i++)
        {
            var cand = rec.Waiting[i];
            float duration = cand.Samples.Count > 1 ? cand.Samples[^1].Time - cand.Samples[0].Time : 0;
            int match = rec.LapInfos.FindIndex(l => Math.Abs(l.LapTime / 1000f - duration) < 1.5f);
            if (DebugOn)
                Log.Information("DriverRecorder debug: {Player} lap candidate {Duration:F1} s, official times [{Times}] -> {Result}", client.Name, duration,
                    string.Join(", ", rec.LapInfos.Select(l => (l.LapTime / 1000f).ToString("F1"))), match >= 0 ? "matched" : force || now - cand.At > TimeSpan.FromSeconds(15) ? "saved without time" : "waiting");
            if (match >= 0)
            {
                var info = rec.LapInfos[match];
                rec.LapInfos.RemoveAt(match);
                Save(client, cand, info.LapTime, info.Cuts);
            }
            else if (force || now - cand.At > TimeSpan.FromSeconds(15))
            {
                Save(client, cand, (uint)(duration * 1000), -1); // no official time: kept, but not used for profiles
            }
            else continue;
            rec.Waiting.RemoveAt(i--);
            rec.Saved++;
        }
    }

    private void Save(ACTcpClient client, LapCandidate cand, uint lapTime, int cuts)
    {
        if (cand.Samples.Count < 20) return;
        bool pit = cand.Samples.Any(s => (s.Flags & 1) != 0);
        bool offTrack = cand.Samples.Count(s => (s.Flags & 8) != 0) > 10;
        // a standing start (race lap 1) is no lap to learn the driving from
        bool standingStart = cand.Samples.Take(Math.Min(60, cand.Samples.Count)).Any(s => s.Speed < 15);
        bool valid = cand.Complete && cuts == 0 && !pit && !offTrack && !standingStart;
        string car = client.EntryCar.Model;
        try
        {
            var dir = Path.Join(_root, client.Guid.ToString(), _trackKey, car);
            Directory.CreateDirectory(dir);
            var file = Path.Join(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{lapTime}_{(valid ? "valid" : "invalid")}.csv.gz");
            using (var fs = File.Create(file))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                var ci = CultureInfo.InvariantCulture;
                w.WriteLine($"# DriverRecorder {FormatVersion}");
                w.WriteLine($"# guid={client.Guid}");
                w.WriteLine($"# name={client.Name}");
                w.WriteLine($"# track={_trackKey}");
                w.WriteLine($"# car={car}");
                w.WriteLine($"# laptime_ms={lapTime}");
                w.WriteLine($"# cuts={cuts}");
                w.WriteLine($"# valid={(valid ? 1 : 0)}");
                w.WriteLine($"# complete={(cand.Complete ? 1 : 0)}");
                w.WriteLine($"# date={DateTime.UtcNow:O}");
                w.WriteLine("t,spline,x,y,z,speed_kmh,gas,brake,clutch,steer_deg,gear,flags,tyre_front_c,tyre_rear_c,fuel_l");
                float t0 = cand.Samples[0].Time;
                foreach (var s in cand.Samples)
                    w.WriteLine(string.Format(ci, "{0:F3},{1:F5},{2:F2},{3:F2},{4:F2},{5:F1},{6},{7},{8},{9:F1},{10},{11},{12:F0},{13:F0},{14:F1}",
                        s.Time - t0, s.Spline, s.Position.X, s.Position.Y, s.Position.Z, s.Speed, s.Gas, s.Brake, s.Clutch, s.Steer, s.Gear, s.Flags,
                        s.TyreFront, s.TyreRear, s.Fuel));
            }
            File.WriteAllText(Path.Join(_root, client.Guid.ToString(), "player.json"),
                JsonSerializer.Serialize(new { guid = client.Guid.ToString(), name = client.Name, updated = DateTime.UtcNow }));
            Prune(dir);
            Log.Information("DriverRecorder: {Player} lap {Time} on {Car} saved ({Valid}, {Count} samples)", client.Name,
                TimeSpan.FromMilliseconds(lapTime).ToString(@"m\:ss\.fff"), car, valid ? "valid" : "not valid", cand.Samples.Count);
            if (valid && IsOptedIn(client.Guid)) SendControl(client, true);
            if (valid)
                client.SendChatMessage(T($"Driver Recorder: lap {TimeSpan.FromMilliseconds(lapTime):m\\:ss\\.fff} saved for your clone.",
                    $"Driver Recorder: Runde {TimeSpan.FromMilliseconds(lapTime):m\\:ss\\.fff} für deinen Klon gespeichert."));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DriverRecorder: could not save a lap of {Player}", client.Name);
        }
    }

    private void Prune(string dir)
    {
        var files = new DirectoryInfo(dir).GetFiles("*.csv.gz");
        int over = files.Length - _config.MaxLapsPerCar;
        if (over <= 0) return;
        // throw away invalid laps first, then the oldest
        foreach (var f in files.OrderBy(f => f.Name.Contains("_valid") ? 1 : 0).ThenBy(f => f.Name).Take(over))
            f.Delete();
    }

    // ------------------------------------------------------------------ info

    /// <summary>Clean laps and the best clean lap time (ms) of this player with his current car on this track.</summary>
    public (int Laps, uint BestMs) CleanLaps(ACTcpClient client)
    {
        int laps = 0;
        uint best = 0;
        try
        {
            var dir = Path.Join(_root, client.Guid.ToString(), _trackKey, client.EntryCar.Model);
            if (!Directory.Exists(dir)) return (0, 0);
            foreach (var f in Directory.EnumerateFiles(dir, "*_valid.csv.gz"))
            {
                laps++;
                // <date>_<lap time ms>_valid.csv.gz
                var parts = Path.GetFileName(f).Split('_');
                if (parts.Length >= 3 && uint.TryParse(parts[^2], out var ms) && (best == 0 || ms < best)) best = ms;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "DriverRecorder: could not count laps");
        }
        return (laps, best);
    }

    public string Info(ACTcpClient client)
    {
        var dir = Path.Join(_root, client.Guid.ToString(), _trackKey);
        int valid = 0, all = 0;
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.csv.gz", SearchOption.AllDirectories))
            {
                all++;
                if (f.Contains("_valid")) valid++;
            }
        bool on = IsOptedIn(client.Guid);
        string diag = "";
        if (on && _recorders.TryGetValue(client, out var r))
        {
            lock (r)
            {
                diag = r.Received == 0
                    ? T(" No data received from your game yet (CSP needed; drive a few seconds).", " Noch keine Daten von deinem Spiel empfangen (CSP nötig; ein paar Sekunden fahren).")
                    : T($" Data: {r.Received} samples, last {(DateTime.UtcNow - r.LastSample).TotalSeconds:F0} s ago, track position {r.LastSpline:F3}, finish line crossed {r.Cuts}x.",
                        $" Daten: {r.Received} Messungen, zuletzt vor {(DateTime.UtcNow - r.LastSample).TotalSeconds:F0} s, Streckenposition {r.LastSpline:F3}, Ziellinie {r.Cuts}x überquert.");
            }
        }
        return InfoText(on, all, valid) + diag;
    }

    private string InfoText(bool on, int all, int valid)
    {
        return T($"Driver Recorder: recording {(on ? "ON" : "off")}. Laps on this track: {all} ({valid} clean). " +
                 "Recorded: throttle, brake, clutch, steering, gear, position, speed, tyre temperatures, fuel – only your own car, only on this server. " +
                 "The server uses it for an AI clone of you. /rec on, /rec off, /rec delete (deletes all your recordings).",
            $"Driver Recorder: Aufzeichnung {(on ? "AN" : "aus")}. Runden auf dieser Strecke: {all} ({valid} sauber). " +
            "Aufgezeichnet werden Gas, Bremse, Kupplung, Lenkung, Gang, Position, Tempo, Reifentemperaturen und Sprit – nur dein eigenes Auto, nur auf diesem Server. " +
            "Der Server baut daraus einen KI-Klon von dir. /rec on, /rec off, /rec delete (löscht alle deine Aufzeichnungen).");
    }

    public int DeleteAll(ACTcpClient client)
    {
        SetOptIn(client, false);
        var dir = Path.Join(_root, client.Guid.ToString());
        if (!Directory.Exists(dir)) return 0;
        int n = Directory.EnumerateFiles(dir, "*.csv.gz", SearchOption.AllDirectories).Count();
        Directory.Delete(dir, true);
        Log.Information("DriverRecorder: {Player} deleted all recordings ({Count} laps)", client.Name, n);
        return n;
    }

    public string Language => _config.Language;

    private bool _debug;
    public bool DebugOn => _debug || _config.Debug;
    public void SetDebug(bool on)
    {
        _debug = on;
        Log.Information("DriverRecorder: debug {State}", on ? "on" : "off");
    }
}
