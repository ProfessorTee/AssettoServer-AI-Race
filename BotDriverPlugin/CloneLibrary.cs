using TrackGeometry;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using BotDriverPlugin.Core;
using Serilog;

namespace BotDriverPlugin;

/// <summary>
/// Driver profiles ("clones") built from the laps the DriverRecorder plugin saved in
/// RecordingsFolder/&lt;steam id&gt;/&lt;track&gt;/&lt;car&gt;/*.csv.gz. Built on demand and cached until new laps come in.
/// </summary>
public sealed class CloneLibrary
{
    private readonly string _root;
    private readonly string _trackKey;
    private readonly RacingLine _line;
    private readonly Dictionary<(string Guid, string Car), (string Signature, CloneProfile? Profile)> _cache = new();
    private readonly object _lock = new();

    public CloneLibrary(string root, string trackKey, RacingLine line)
    {
        _root = Path.GetFullPath(root);
        _trackKey = trackKey;
        _line = line;
    }

    public sealed record Entry(string Guid, string Name, string Car, int CleanLaps, int AllLaps);

    /// <summary>Everything recorded on this track (cheap: only file names).</summary>
    public List<Entry> List()
    {
        var list = new List<Entry>();
        if (!Directory.Exists(_root)) return list;
        foreach (var playerDir in Directory.EnumerateDirectories(_root))
        {
            string guid = Path.GetFileName(playerDir);
            var trackDir = Path.Join(playerDir, _trackKey);
            if (!Directory.Exists(trackDir)) continue;
            string name = PlayerName(playerDir) ?? guid;
            foreach (var carDir in Directory.EnumerateDirectories(trackDir))
            {
                var files = Directory.GetFiles(carDir, "*.csv.gz");
                list.Add(new Entry(guid, name, Path.GetFileName(carDir), files.Count(f => f.EndsWith("_valid.csv.gz")), files.Length));
            }
        }
        return list;
    }

    private static string? PlayerName(string playerDir)
    {
        try
        {
            var path = Path.Join(playerDir, "player.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Finds a player by Steam ID or (part of the) name.</summary>
    public string? FindGuid(string idOrName)
    {
        var all = List();
        return all.FirstOrDefault(e => e.Guid == idOrName)?.Guid
               ?? all.FirstOrDefault(e => string.Equals(e.Name, idOrName, StringComparison.OrdinalIgnoreCase))?.Guid
               ?? all.FirstOrDefault(e => e.Name.Contains(idOrName, StringComparison.OrdinalIgnoreCase))?.Guid;
    }

    /// <summary>
    /// The profile of a player for a car; with <paramref name="anyCar"/> falls back to the car with the most clean laps.
    /// Null when there is no clean lap.
    /// </summary>
    public CloneProfile? Get(string guid, string car, bool anyCar = false)
    {
        var p = GetExact(guid, car);
        if (p != null || !anyCar) return p;
        var other = List().Where(e => e.Guid == guid && e.CleanLaps > 0).OrderByDescending(e => e.CleanLaps).FirstOrDefault();
        return other != null ? GetExact(guid, other.Car) : null;
    }

    private CloneProfile? GetExact(string guid, string car)
    {
        var dir = Path.Join(_root, guid, _trackKey, car);
        if (!Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir, "*_valid.csv.gz").OrderBy(f => f).ToList();
        string signature = string.Join("|", files.Select(Path.GetFileName));
        lock (_lock)
        {
            if (_cache.TryGetValue((guid, car), out var cached) && cached.Signature == signature) return cached.Profile;
        }

        var laps = new List<RecordedLap>();
        string name = PlayerName(Path.Join(_root, guid)) ?? guid;
        foreach (var f in files)
        {
            try
            {
                laps.Add(ReadLap(f));
            }
            catch (Exception ex)
            {
                Log.Warning("BotDriver: recording {File} not readable: {Error}", f, ex.Message);
            }
        }
        var profile = CloneProfile.Build(_line, laps, guid, name, car);
        if (profile != null)
            Log.Information("BotDriver: clone profile of {Name} ({Car}): {Used} of {Clean} clean laps, best {Best}, average {Avg}",
                name, car, profile.LapsUsed, profile.CleanLaps, Fmt(profile.BestLap), Fmt(profile.AverageLap));
        lock (_lock) _cache[(guid, car)] = (signature, profile);
        return profile;
    }

    private static string Fmt(float s) => TimeSpan.FromSeconds(s).ToString(@"m\:ss\.fff");

    /// <summary>Reads one lap file of the DriverRecorder plugin (gzip CSV with # header lines).</summary>
    public static RecordedLap ReadLap(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new StreamReader(gz);
        float lapTime = 0;
        bool valid = false;
        var samples = new List<RecordedSample>();
        int ix = -1, iy = -1, iz = -1, iv = -1, ig = -1, ib = -1;
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.StartsWith('#'))
            {
                var kv = line[1..].Trim().Split('=', 2);
                if (kv.Length == 2 && kv[0] == "laptime_ms") lapTime = float.Parse(kv[1], CultureInfo.InvariantCulture) / 1000f;
                if (kv.Length == 2 && kv[0] == "valid") valid = kv[1] == "1";
                continue;
            }
            var parts = line.Split(',');
            if (ix < 0)
            {
                ix = Array.IndexOf(parts, "x"); iy = Array.IndexOf(parts, "y"); iz = Array.IndexOf(parts, "z");
                iv = Array.IndexOf(parts, "speed_kmh"); ig = Array.IndexOf(parts, "gas"); ib = Array.IndexOf(parts, "brake");
                continue;
            }
            float F(int i) => float.Parse(parts[i], CultureInfo.InvariantCulture);
            samples.Add(new RecordedSample(new Vector3(F(ix), F(iy), F(iz)), F(iv) / 3.6f, F(ig) / 255f, F(ib) / 255f));
        }
        return new RecordedLap { LapTime = lapTime, Valid = valid, Samples = samples };
    }
}
