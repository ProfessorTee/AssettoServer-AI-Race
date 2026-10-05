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
                "fuel" => Fuel(opts),
                "car" => Car(opts),
                "acd" => Acd(opts),
                "selftest" => SelfTest.Run(opts),
                "strength" => Strength(opts),
                "width" => Width(opts),
                "height" => Height(opts),
                "accel" => AccelCheck(opts),
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

    /// <summary>Effect of the fuel load: braking distance and lap time with a light and a full tank.</summary>
    private static int Fuel(Options o)
    {
        var (trackRoot, layout, carsRoot) = ResolvePaths(o);
        var line = new RacingLine(FastLaneFile.Read(FastLanePath(trackRoot, layout)));
        var car = CarDataLoader.Load(carsRoot, o.Get("model") ?? "ks_mercedes_amg_gt3");
        var settings = new RaceWorldSettings();
        foreach (float litres in new[] { 20f, car.FuelCapacity / 2, car.FuelCapacity })
        {
            float mass = car.ReferenceMass - 25 + litres * 0.745f;
            float ratio = mass / car.ReferenceMass;
            float grip = 1 - 0.3f * (ratio - 1);
            // braking 250 -> 80 km/h
            float d = 0;
            for (float v = 250 / 3.6f; v > 80 / 3.6f; v -= 0.1f) d += v * 0.1f / car.BrakeAt(v, grip, ratio);
            float t0 = 0; float v0 = 80 / 3.6f; float t = 0;
            for (float v = 0; v < 200 / 3.6f; t += 0.01f) v += car.AccelAt(v, 1) / ratio * 0.01f;
            _ = t0; _ = v0;
            float lap = StrengthCalibration.FlyingLap(line, car, 1f, settings, out _, out _, 0, 1, litres);
            Console.WriteLine($"{litres,5:F0} l  {mass,6:F0} kg  brake 250->80 {d,5:F1} m  0-200 {t,5:F2} s  lap {TimeSpan.FromSeconds(lap):m\\:ss\\.fff}");
        }
        return 0;
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
        Console.WriteLine($"{info.PitBoxes.Count} pit boxes, {info.SectorLines.Count} sector splits");
        foreach (var sl in info.SectorLines) Console.WriteLine($"  sector split at s={line.Project(sl).S:F0}");
        var pitPath = Path.Join(Path.GetDirectoryName(FastLanePath(trackRoot, layout))!, "pit_lane.ai");
        var world = new RaceWorld(line, new RaceWorldSettings { SpotHeightOffset = RaceWorld.MeasureSpotHeight(line, info.StartGrid.Select(g => g.Position)) }) { PitLane = File.Exists(pitPath) ? new PitLane(FastLaneFile.Read(pitPath), line) : null };
        foreach (var g in info.PitBoxes.Take(o.Has("verbose") ? 999 : 5))
        {
            var ground = world.OnGround(g.Position);
            float lane = world.PitLane != null ? world.PitLane.PositionAt(world.PitLane.Project(g.Position).S, 0).Y : float.NaN;
            Console.WriteLine($"AC_PIT_{g.Index,-3} dummy {g.Position.Y,7:F2}, parked at {ground.Y,7:F2} (spot offset {world.Settings.SpotHeightOffset:F2} m), pit lane {lane,7:F2}");
        }

        var outPath = o.Get("out");
        if (outPath != null)
        {
            var json = new GridFile
            {
                Track = Path.GetFileName(trackRoot.TrimEnd('/', '\\')),
                Layout = layout,
                StartFinish = info.StartFinish is { } v ? [v.X, v.Y, v.Z] : null,
                Grid = info.StartGrid.Select(g => new[] { g.Position.X, g.Position.Y, g.Position.Z, g.Forward.X, g.Forward.Y, g.Forward.Z }).ToList(),
                Pits = info.PitBoxes.Select(g => new[] { g.Position.X, g.Position.Y, g.Position.Z, g.Forward.X, g.Forward.Y, g.Forward.Z }).ToList(),
                Sectors = info.SectorLines.Select(p => new[] { p.X, p.Y, p.Z }).ToList()
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

    /// <summary>
    /// Full-throttle acceleration of a player's recorded laps (DriverRecorder csv.gz) against the bot model of the same car, by speed:
    /// shows whether the engine/turbo/drag model is too strong or too weak.
    /// </summary>
    private static int AccelCheck(Options o)
    {
        string cars = o.Get("cars") ?? Path.Join(o.Get("ac") ?? ".", "content", "cars");
        string model = o.Get("model") ?? throw new ArgumentException("--model required");
        string dir = o.Get("recordings") ?? throw new ArgumentException("--recordings <folder with csv.gz> required");
        var spec = CarDataLoader.Load(cars, model, log: _ => { });
        var bins = new SortedDictionary<int, List<float>>();
        var brakeBins = new SortedDictionary<int, List<float>>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.csv.gz", SearchOption.AllDirectories))
        {
            using var gz = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress);
            using var rd = new StreamReader(gz);
            var rows = new List<float[]>();
            string? line;
            while ((line = rd.ReadLine()) != null)
            {
                if (line.StartsWith('#') || line.StartsWith('t')) continue;
                var f = line.Split(',');
                if (f.Length < 11) continue;
                // t, x, y, z, speed m/s, gas, brake, steer, gear
                rows.Add([float.Parse(f[0]), float.Parse(f[2]), float.Parse(f[3]), float.Parse(f[4]), float.Parse(f[5]) / 3.6f, float.Parse(f[6]), float.Parse(f[7]), float.Parse(f[9]), float.Parse(f[10])]);
            }
            // windows of ~0.4 s at full throttle, no brake, straight, same gear
            for (int i = 0; i + 1 < rows.Count; i++)
            {
                int j = i;
                while (j + 1 < rows.Count && rows[j + 1][0] - rows[i][0] < 0.4f) j++;
                if (j == i || rows[j][0] - rows[i][0] < 0.25f) continue;
                bool ok = true, brk = true;
                for (int k = i; k <= j && (ok || brk); k++)
                {
                    ok &= rows[k][5] >= 250 && rows[k][6] == 0 && MathF.Abs(rows[k][7]) < 8 && rows[k][8] == rows[i][8] && rows[k][4] > 8;
                    brk &= rows[k][6] >= 150 && rows[k][5] < 20 && MathF.Abs(rows[k][7]) < 15 && rows[k][4] > 8;
                }
                if (!ok && !brk) continue;
                float dt = rows[j][0] - rows[i][0];
                float ds = 0;
                for (int k = i; k < j; k++) ds += MathF.Sqrt(MathF.Pow(rows[k + 1][1] - rows[k][1], 2) + MathF.Pow(rows[k + 1][3] - rows[k][3], 2));
                if (ds < 1) continue;
                float a = (rows[j][4] - rows[i][4]) / dt + 9.81f * (rows[j][2] - rows[i][2]) / ds; // + climbing
                int bin = (int)(rows[i][4] * 3.6f / 20) * 20;
                var target = brk ? brakeBins : bins;
                if (!target.TryGetValue(bin, out var list)) target[bin] = list = new();
                list.Add(brk ? -a : a);
            }
        }
        Console.WriteLine($"{model}: full-throttle acceleration, player (median of {bins.Values.Sum(l => l.Count)} windows) vs bot model (pace 1)");
        Console.WriteLine("  km/h   player   model   model/player");
        foreach (var (bin, list) in bins)
        {
            if (list.Count < 5) continue;
            list.Sort();
            float real = list[list.Count / 2];
            float mdl = spec.AccelAt((bin + 10) / 3.6f, 1f);
            Console.WriteLine($"  {bin,3}-{bin + 20,-3} {real,6:F2}  {mdl,6:F2}   {(real > 0.2f ? (mdl / real).ToString("F2") : "-"),5}   ({list.Count})");
        }
        Console.WriteLine("  braking (pedal >= 60 %), deceleration m/s²: player vs model");
        foreach (var (bin, list) in brakeBins)
        {
            if (list.Count < 5) continue;
            list.Sort();
            float real = list[list.Count * 3 / 4]; // the harder part of the braking zones
            float mdl = spec.BrakeAt((bin + 10) / 3.6f, 1f) + spec.DragCoefficient * MathF.Pow((bin + 10) / 3.6f, 2);
            Console.WriteLine($"  {bin,3}-{bin + 20,-3} {real,6:F2}  {mdl,6:F2}   {(mdl / real):F2}   ({list.Count})");
        }
        return 0;
    }

    private static int Acd(Options o)
    {
        string cars = o.Get("cars") ?? Path.Join(o.Get("ac") ?? ".", "content", "cars");
        string model = o.Get("model") ?? throw new ArgumentException("--model required");
        var files = AcdReader.Read(Path.Join(cars, model, "data.acd"), model);
        string? only = o.Get("file");
        foreach (var (name, bytes) in files.OrderBy(f => f.Key))
        {
            if (only == null) Console.WriteLine($"{name,-32} {bytes.Length,8}");
            else if (only.Split(',').Contains(name, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"==== {name}\n{System.Text.Encoding.UTF8.GetString(bytes)}");
        }
        return 0;
    }

    /// <summary>How far the real road surface is from the AI line's flat road beside the line (cars sinking into / floating over the road).</summary>
    private static int Height(Options o)
    {
        var (trackRoot, layout, _) = ResolvePaths(o);
        var line = new RacingLine(FastLaneFile.Read(FastLanePath(trackRoot, layout)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var road = RoadSurface.Load(trackRoot, layout);
        if (road == null) { Console.WriteLine("no road meshes"); return 1; }
        Console.WriteLine($"{road.Triangles} road triangles in {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        var lh = LineHeights.Build(line, road);
        var corr = new List<float>();
        for (float s = 0; s < line.Length; s += 5)
            foreach (float off in new[] { -3f, -1.5f, 1.5f, 3f }) corr.Add(lh.At(s, off));
        corr.Sort();
        int big = 0;
        for (float s = 0; s < line.Length; s += 1)
            for (float off = -4; off <= 4; off += 1)
            {
                int i = line.IndexAt(s);
                if (off > line.RoomPlus[i] || -off > line.RoomMinus[i]) continue;
                float c = lh.At(s, off);
                if (MathF.Abs(c) > 0.3f && big++ < 12) Console.WriteLine($"  {s,6:F0} m off {off,3:F0}: {c:F2} (room +{line.RoomPlus[i]:F1} -{line.RoomMinus[i]:F1})");
            }
        Console.WriteLine($"  {big} samples over 0.3 m on the road");
        Console.WriteLine($"profile built in {sw.ElapsedMilliseconds} ms, max {lh.Max:F2} m; correction at ±1.5/3 m: 1 % {corr[corr.Count / 100]:F3}, median {corr[corr.Count / 2]:F3}, 99 % {corr[corr.Count * 99 / 100]:F3}, share over 0.1 m {corr.Count(c => MathF.Abs(c) > 0.1f) * 100f / corr.Count:F1} %");
        float step = o.Float("step", 50);
        var all = new List<float>();
        foreach (float off in new[] { -4f, -2f, 0f, 2f, 4f })
        {
            var d = new List<(float S, float D)>();
            for (float s = 0; s < line.Length; s += step)
            {
                int i = line.IndexAt(s);
                if (off > line.RoomPlus[i] - 1 || -off > line.RoomMinus[i] - 1) continue;
                var p = line.PositionAt(s, off);
                if (road.HeightAt(p.X, p.Z, p.Y) is { } y) d.Add((s, y - p.Y));
            }
            if (d.Count == 0) continue;
            var sorted = d.Select(x => x.D).OrderBy(x => x).ToList();
            all.AddRange(sorted);
            var worst = d.OrderByDescending(x => x.D).First();
            Console.WriteLine($"offset {off,3:F0} m: {d.Count,5} points, road above the line: median {sorted[sorted.Count / 2]:F3} m, 95 % {sorted[(int)(sorted.Count * 0.95)]:F3} m, max {worst.D:F3} m at {worst.S:F0} m, below: min {sorted[0]:F3} m");
        }
        return 0;
    }

    private static int Width(Options o)
    {
        var (trackRoot, layout, _) = ResolvePaths(o);
        var line = new RacingLine(FastLaneFile.Read(FastLanePath(trackRoot, layout)));
        float from = o.Float("from", 800), to = o.Float("to", 1000);
        Console.WriteLine($"spacing {line.Spacing:F2} m, {line.Count} points");
        for (float s = from; s < to; s += 2)
        {
            int i = line.IndexAt(s);
            Console.WriteLine($"{s,6:F0} k {line.Curvature[i],8:F4}  +{line.RoomPlus[i],5:F2} -{line.RoomMinus[i],5:F2}  c {(line.RoomPlus[i] - line.RoomMinus[i]) / 2,6:F2}");
        }
        return 0;
    }

    private static int Strength(Options o)
    {
        var (trackRoot, layout, cars) = ResolvePaths(o);
        var line = new RacingLine(FastLaneFile.Read(FastLanePath(trackRoot, layout)));
        var info = TrackInfo.LoadLayoutData(LayoutDataDir(trackRoot, layout));
        line.ApplyHints(info.SpeedHints, info.MaxSpeedsKmh);
        var settings = new RaceWorldSettings { HumanErrors = !o.Has("no-errors") };
        int threads = o.Int("threads", 0);
        foreach (var model in (o.Get("models") ?? "ks_mercedes_amg_gt3").Split(','))
        {
            var spec = CarDataLoader.Load(cars, model);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
            long alloc = GC.GetTotalAllocatedBytes();
            var cpu0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            var cal = StrengthCalibration.Measure(line, spec, settings, threads: threads);
            var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu0;
            Console.WriteLine($"{model}: best lap {Simulator.Fmt(cal.BestLap)} ({sw.ElapsedMilliseconds} ms wall, {cpu.TotalMilliseconds:F0} ms CPU, " +
                              $"{(GC.GetTotalAllocatedBytes() - alloc) / 1048576} MB allocated, GC {GC.CollectionCount(0) - gc0}/{GC.CollectionCount(2) - gc2}), table {string.Join(" ", cal.LapTimes.Select(Simulator.Fmt))}");
            foreach (var pct in new[] { 100f, 97, 95, 94, 92, 90, 85, 80, 75, 70 })
                Console.WriteLine($"   {pct,4:F0} % -> pace {cal.PaceFor(pct):F3}  target {Simulator.Fmt(cal.LapTimeFor(pct))}");
        }
        return 0;
    }

    public static void PrintSpec(CarSpec s)
    {
        Console.WriteLine($"{s.Model} ({s.Source}): top {s.TopSpeed * 3.6f:F0} km/h, lat grip {s.LateralGrip:F2} g, brake {s.BrakeGrip:F2} g, " +
                          $"downforce {s.Downforce * 10000:F1}e-4, aeroBrake {s.AeroBrake * 10000:F1}e-4, L {s.Length:F2} W {s.Width:F2} WB {s.Wheelbase:F2}, " +
                          $"rpm {s.IdleRpm}-{s.MaxRpm} (up {s.UpshiftRpm}/down {s.DownshiftRpm}, shift {s.ShiftUpTime * 1000:F0}/{s.ShiftDownTime * 1000:F0} ms{(s.HasAbs ? ", ABS" : "")}{(s.HasTc ? ", TC" : "")}), gears [{string.Join(", ", s.GearTopSpeedsKmh.Select(g => g.ToString("F0")))}] km/h");
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
        if (s.ErsGain != null && s.ErsPower != null)
        {
            Console.WriteLine($"    hybrid: {s.ErsKjPerLap:F0} kJ/lap, full deploy {s.ErsPower.Max() / 1000:F0} kW max, +{s.ErsGain.Max():F2} m/s² max");
            foreach (float km in new[] { 4.1f, 5.2f, 13.6f, 20.8f })
            {
                var c = s.Clone();
                c.SetTrack(km * 1000);
                Console.WriteLine($"      {km,5:F1} km lap: deploy share {c.ErsShare:P0}");
            }
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
    /// <summary>Timing sector splits (AC_TIME_1.., middle of L/R).</summary>
    public List<float[]> Sectors { get; set; } = [];
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
