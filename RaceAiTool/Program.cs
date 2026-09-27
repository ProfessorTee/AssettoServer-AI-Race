using System.Globalization;
using System.Numerics;
using System.Text.Json;
using RaceAiPlugin.Core;

namespace RaceAiTool;

/// <summary>
/// Offline helper for RaceAiPlugin.
///   sim   : runs a race with bots on a real track (no server needed) and prints lap times, overtakes, contacts and overlap checks
///   grid  : extracts AC_START / AC_PIT / AC_TIME_0 positions from the track's kn5 files (writes a json the plugin can use)
///   car   : shows the performance model built from a car's data.acd
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var opts = Options.Parse(args.Skip(1));
        try
        {
            return args[0] switch
            {
                "sim" => Simulator.Run(opts),
                "grid" => Grid(opts),
                "car" => Car(opts),
                "selftest" => SelfTest.Run(opts),
                "strength" => Strength(opts),
                _ => Usage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static int Usage()
    {
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            RaceAiTool <command> [options]

            sim   --ac <assettocorsa dir> | --track <track dir> --layout <layout>  [--cars <cars dir>] [--models m1,m2,..]
                  [--bots 12] [--laps 2] [--level 95] [--variation 5] [--aggression 50] [--seed 1] [--hotlap]
                  [--player-pace 0.9]  (adds a scripted 'human' car that the bots have to race against)
            grid  --ac <assettocorsa dir> | --track <track dir> --layout <layout>  [--out grid.json]
            car   --ac <assettocorsa dir> | --cars <cars dir>  --model <car>
            selftest                 (offline checks on a synthetic track, no game files needed)
            """);
    }

    public static (string TrackRoot, string Layout, string CarsRoot) ResolvePaths(Options o, string defaultTrack = "ks_nordschleife", string defaultLayout = "nordschleife")
    {
        string? ac = o.Get("ac");
        string track = o.Get("track") ?? (ac != null ? Path.Join(ac, "content", "tracks", o.Get("track-name") ?? defaultTrack) : throw new ArgumentException("--ac or --track required"));
        string layout = o.Get("layout") ?? defaultLayout;
        string cars = o.Get("cars") ?? (ac != null ? Path.Join(ac, "content", "cars") : "");
        return (track, layout, cars);
    }

    public static string FastLanePath(string trackRoot, string layout)
    {
        string p = Path.Join(trackRoot, layout, "ai", "fast_lane.ai");
        return File.Exists(p) ? p : Path.Join(trackRoot, "ai", "fast_lane.ai");
    }

    public static string LayoutDataDir(string trackRoot, string layout)
    {
        string p = Path.Join(trackRoot, layout, "data");
        return Directory.Exists(p) ? p : Path.Join(trackRoot, "data");
    }

    private static int Grid(Options o)
    {
        var (trackRoot, layout, _) = ResolvePaths(o);
        var info = new TrackInfo();
        info.LoadSpotsFromKn5(trackRoot, layout);
        var line = new RacingLine(FastLaneFile.Read(FastLanePath(trackRoot, layout)));

        Console.WriteLine($"Line length {line.Length:F0} m");
        if (info.StartFinish is { } sf)
        {
            var p = line.Project(sf);
            Console.WriteLine($"Start/finish line at s={p.S:F1} ({sf})");
        }
        foreach (var g in info.StartGrid)
        {
            var p = line.Project(g.Position);
            Console.WriteLine($"AC_START_{g.Index,-3} s={p.S,8:F1} offset={p.Offset,6:F2} height={p.Height,5:F2}  {g.Position}");
        }
        Console.WriteLine($"{info.PitBoxes.Count} pit boxes");

        var outPath = o.Get("out");
        if (outPath != null)
        {
            var json = new GridFile
            {
                Track = Path.GetFileName(trackRoot.TrimEnd('/', '\\')),
                Layout = layout,
                StartFinish = info.StartFinish is { } v ? [v.X, v.Y, v.Z] : null,
                Grid = info.StartGrid.Select(g => new[] { g.Position.X, g.Position.Y, g.Position.Z, g.Forward.X, g.Forward.Y, g.Forward.Z }).ToList(),
                Pits = info.PitBoxes.Select(g => new[] { g.Position.X, g.Position.Y, g.Position.Z, g.Forward.X, g.Forward.Y, g.Forward.Z }).ToList()
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Written {outPath}");
        }
        return 0;
    }

    private static int Car(Options o)
    {
        string? ac = o.Get("ac");
        string cars = o.Get("cars") ?? Path.Join(ac ?? ".", "content", "cars");
        foreach (var model in (o.Get("model") ?? throw new ArgumentException("--model required")).Split(','))
        {
            var spec = CarDataLoader.Load(cars, model, log: Console.WriteLine);
            PrintSpec(spec);
        }
        return 0;
    }

    private static int Strength(Options o)
    {
        var (trackRoot, layout, cars) = ResolvePaths(o);
        var line = new RacingLine(FastLaneFile.Read(FastLanePath(trackRoot, layout)));
        var info = TrackInfo.LoadLayoutData(LayoutDataDir(trackRoot, layout));
        line.ApplyHints(info.SpeedHints, info.MaxSpeedsKmh);
        var settings = new RaceWorldSettings();
        foreach (var model in (o.Get("models") ?? "ks_mercedes_amg_gt3").Split(','))
        {
            var spec = CarDataLoader.Load(cars, model);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cal = StrengthCalibration.Measure(line, spec, settings);
            Console.WriteLine($"{model}: best lap {Simulator.Fmt(cal.BestLap)} ({sw.ElapsedMilliseconds} ms), table {string.Join(" ", cal.LapTimes.Select(Simulator.Fmt))}");
            foreach (var pct in new[] { 100f, 97, 95, 94, 92, 90, 85, 80, 75, 70 })
                Console.WriteLine($"   {pct,4:F0} % -> pace {cal.PaceFor(pct):F3}  target {Simulator.Fmt(cal.LapTimeFor(pct))}");
        }
        return 0;
    }

    public static void PrintSpec(CarSpec s)
    {
        Console.WriteLine($"{s.Model} ({s.Source}): top {s.TopSpeed * 3.6f:F0} km/h, lat grip {s.LateralGrip:F2} g, brake {s.BrakeGrip:F2} g, " +
                          $"downforce {s.Downforce * 10000:F1}e-4, aeroBrake {s.AeroBrake * 10000:F1}e-4, L {s.Length:F2} W {s.Width:F2} WB {s.Wheelbase:F2}, " +
                          $"rpm {s.IdleRpm}-{s.MaxRpm} (up {s.UpshiftRpm}), gears [{string.Join(", ", s.GearTopSpeedsKmh.Select(g => g.ToString("F0")))}] km/h");
        if (s.AccelTable != null)
        {
            // 0-100 and 0-200 km/h
            float t = 0, v = 0, t100 = 0, t200 = 0;
            while (v < 200 / 3.6f && t < 60)
            {
                v += s.AccelAt(v, 1) * 0.01f;
                t += 0.01f;
                if (t100 == 0 && v >= 100 / 3.6f) t100 = t;
            }
            t200 = t;
            Console.WriteLine($"    0-100 {t100:F1}s  0-200 {t200:F1}s");
        }
    }
}

public sealed class GridFile
{
    public string Track { get; set; } = "";
    public string Layout { get; set; } = "";
    public float[]? StartFinish { get; set; }
    public List<float[]> Grid { get; set; } = [];
    public List<float[]> Pits { get; set; } = [];
}

public sealed class Options
{
    private readonly Dictionary<string, string> _values = new();

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--"))
            {
                if (key != null) o._values[key] = "true";
                key = a[2..];
            }
            else if (key != null)
            {
                o._values[key] = a;
                key = null;
            }
        }
        if (key != null) o._values[key] = "true";
        return o;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public bool Has(string key) => _values.ContainsKey(key);
    public int Int(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;
    public float Float(string key, float fallback) => float.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
