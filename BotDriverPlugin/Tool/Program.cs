using TrackGeometry;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using BotDriverPlugin.Core;

namespace BotDriverTool;

/// <summary>
/// Offline helper for BotDriverPlugin.
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
                "acd" => Acd(opts),
                "physstep" => PhysStep(opts),
                "selftest" => SelfTest.Run(opts),
                "strength" => Strength(opts),
                "width" => Width(opts),
                "height" => Height(opts),
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
            BotDriverTool <command> [options]

            sim      --ac <assettocorsa dir> | --track <track dir> --layout <layout>  [--cars <cars dir>] [--models m1,m2,..]
                     [--bots 12] [--laps 2] [--strength 95] [--spread 3] [--aggression 50] [--seed 1] [--slowest-first]
                     [--hotlap [--rec <recordings folder>] [--dump lap.csv]] [--trace <bot id>] [--contacts] [--no-errors] [--no-push]
                     [--player-pace 0.9]  (adds an AI 'player' car, blind to the bots, that the bots have to race against)
            grid     --ac <assettocorsa dir> | --track <track dir> --layout <layout>  [--out grid.json]
            car      --ac <assettocorsa dir> | --cars <cars dir>  --model <car> [--speed 25]   (car data, vehicle model, skid pad)
            physstep --ac <dir> --model <car> [--speed 10] [--gear n] [--throttle x] ...      (vehicle model, step by step)
            strength --ac <dir> --track-name <track> --layout <layout> --models m1,m2           (strength calibration: % -> AI level)
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

    /// <summary>Vehicle model check: constant steering at constant speed on a flat plane, the steady state per wheel angle.</summary>
    private static void SkidPad(BotDriverPlugin.Core.Physics.VehicleData d, float speed)
    {
        Console.WriteLine($"  physics: mass {d.Mass:F0} kg (sprung {d.SprungMass:F0}), Iz {d.YawInertia:F0}, L {d.Wheelbase:F2} a {d.A:F2} b {d.B:F2}, cg h {d.CgHeight:F3} pitch {d.StaticPitch * 57.3f:F2}°, drive {d.Drive}, diff {d.RearDiff}, " +
                          $"tyres F {d.Front.DyRef:F2}/{d.Front.Fz0:F0}/{d.Front.LsExpY:F2}/{d.Front.PeakAngle * 57.3f:F1}° R {d.Rear.DyRef:F2}/{d.Rear.Fz0:F0}/{d.Rear.LsExpY:F2}/{d.Rear.PeakAngle * 57.3f:F1}°, " +
                          $"brake {d.BrakeTorque:F0} Nm {d.BrakeFront:F2} front, {d.Wings.Count} wings, turbos {d.Turbos.Count}, max steer {d.MaxSteer * 57.3f:F0}°, rack {d.RackPerDeg * 1000:F2} mm/° sign {d.RackSign}, ultra grip {d.AiUltraGrip:F2}");
        foreach (var (name, sa) in new[] { ("front", d.FrontSusp), ("rear", d.RearSusp) })
        {
            float z0 = name == "front" ? d.StaticHub[0] : d.StaticHub[2];
            Console.WriteLine($"  {name} {sa.Type} geometry={sa.HasGeometry}{(sa.GeometryError != null ? $" ({sa.GeometryError})" : "")} k={sa.SpringRate:F0} arb={sa.Arb:F0} hub={sa.HubMass:F0} kg");
            for (float z = -0.06f; z <= 0.061f; z += 0.03f)
            {
                var (ti, toe) = sa.Angles(z, 0);
                var (dy, dx, dxh, dp) = sa.Jacking(z);
                Console.WriteLine($"    travel {z * 1000,4:F0} mm: camber {-ti * 57.3f,5:F2}° toe-in {toe * 57.3f,5:F2}° cp in/z {dy,5:F2} cp fwd/z {dx,5:F2} hub fwd/z {dxh,5:F2} pitch/z {dp,5:F2}");
            }
            if (sa.RackCount > 1)
                foreach (float deg in new[] { 5f, 15f, 30f })
                {
                    float rack = d.RackSign * d.RackPerDeg * deg;
                    float inner = -sa.Angles(0, -rack).ToeIn, outer = sa.Angles(0, rack).ToeIn;
                    Console.WriteLine($"    steer {deg}°: wheels {inner * 57.3f:F1}° / {outer * 57.3f:F1}°");
                }
        }
        var pl = BotDriverPlugin.Core.Physics.Envelope.Measure(d, 1, ai: true, plan: true);
        Console.WriteLine($"  AC AI plans with: {pl.LatSlow / 9.81f:F2} g at 80 km/h, {pl.LatFast / 9.81f:F2} g at 200 km/h");
        BotDriverPlugin.Core.Physics.Envelope.Calibrate(d);
        Console.WriteLine($"  tyre grip x{d.GripScale:F2} (calibrated to the plan)");
        foreach (float lg in new[] { 1f, d.AiUltraGrip })
        {
            var e = BotDriverPlugin.Core.Physics.Envelope.Measure(d, lg, ai: true);
            Console.WriteLine($"  envelope (lateral grip x{lg:F2}, AI help): {e.LatSlow / 9.81f:F2} g at 80 km/h, {e.LatFast / 9.81f:F2} g at 200 km/h -> grip {e.Grip:F2} + downforce {e.Downforce * 1e4f:F1}e-4/(m/s)², braking {e.BrakeGrip:F2} g + {e.AeroBrake * 1e4f:F1}e-4");
        }
        Console.WriteLine($"  skid pad at {speed * 3.6f:F0} km/h: steer°  ay g  radius m  slip angle F/R (1 = peak)  body slip°");
        for (float deg = 0.5f; deg <= 8; deg += 0.5f)
        {
            var inp = new BotDriverPlugin.Core.Physics.VehicleInputs { MassRatio = 1, Grip = 1, Ratio = BotDriverPlugin.Core.Physics.Envelope.RatioFor(d, speed) };
            var st = BotDriverPlugin.Core.Physics.Envelope.Rolling(d, speed, ref inp);
            inp.Steer = deg / 57.3f;
            bool spun = false;
            for (int i = 0; i < 300 * 12; i++)
            {
                inp.Throttle = Math.Clamp(0.3f + (speed - st.Vx) * 0.5f, 0, 1);
                BotDriverPlugin.Core.Physics.Vehicle.Step(d, st, inp, 1f / 300);
                if (MathF.Abs(MathF.Atan2(st.Vy, st.Vx)) > 0.5f) { spun = true; break; }
            }
            float ay = st.Vx * st.R;
            Console.WriteLine(spun ? $"    {deg,4:F1}  spun" : $"    {deg,4:F1}  {ay / 9.81f,5:F2}  {(MathF.Abs(st.R) > 1e-3f ? st.Vx / st.R : 0),7:F0}  {st.FrontSlip,5:F2}/{st.RearSlip,5:F2}  {MathF.Atan2(st.Vy, st.Vx) * 57.3f,6:F1}");
        }
    }

    private static int Car(Options o)
    {
        string? ac = o.Get("ac");
        string cars = o.Get("cars") ?? Path.Join(ac ?? ".", "content", "cars");
        foreach (var model in (o.Get("model") ?? throw new ArgumentException("--model required")).Split(','))
        {
            var spec = CarDataLoader.Load(cars, model, log: Console.WriteLine);
            PrintSpec(spec);
            if (spec.Physics is { } pd) SkidPad(pd, o.Float("speed", 25));
            else Console.WriteLine("  no physics data: bots can't drive this car");
            foreach (var c in spec.Compounds)
                Console.WriteLine($"  {(c == spec.DefaultCompound ? "*" : " ")} {c}: grip x{c.Grip:F3}, best at {c.Optimum:F0} °C, " +
                                  $"wear 0/20/50/100 vkm {c.GripAt(0):F3}/{c.GripAt(20):F3}/{c.GripAt(50):F3}/{c.GripAt(100):F3}{(c.IsSlick ? ", slick" : c.IsWet ? ", wet" : "")}");
            if (o.Get("track") is { } tr && spec.HasWingSetup)
            {
                var (trackRoot, layout, _) = ResolvePaths(o);
                var line = new RacingLine(FastLaneFile.Read(FastLanePath(trackRoot, layout)));
                foreach (float lv in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
                {
                    var s2 = CarDataLoader.Load(cars, model, wingLevel: lv);
                    Console.WriteLine($"  wings {lv * 100,3:F0} %: top {s2.TopSpeed * 3.6f:F0} km/h, estimated lap {LapEstimator.Estimate(s2, line):F2} s");
                }
                Console.WriteLine($"  chosen: {CarDataLoader.LoadForTrack(cars, model, line).WingLevel?.ToString("P0") ?? "default"}");
            }
        }
        return 0;
    }

    /// <summary>Vehicle model debug: steps from a steady speed with fixed inputs, printing the wheels and the engine.</summary>
    private static int PhysStep(Options o)
    {
        string cars = o.Get("cars") ?? Path.Join(o.Get("ac") ?? ".", "content", "cars");
        var d = CarDataLoader.Load(cars, o.Get("model") ?? throw new ArgumentException("--model required")).Physics!;
        float v = o.Float("speed", 10);
        var inp = new BotDriverPlugin.Core.Physics.VehicleInputs { MassRatio = 1, Grip = 1, LateralGrip = o.Float("lat", 1), NoFalloff = o.Has("nofalloff"), AiAbs = o.Has("aiabs") };
        int gear = o.Int("gear", 0);
        inp.Ratio = gear > 0 ? d.Ratios[gear - 1] * d.Final : BotDriverPlugin.Core.Physics.Envelope.RatioFor(d, v);
        var st = BotDriverPlugin.Core.Physics.Envelope.Rolling(d, v, ref inp);
        inp.Throttle = o.Float("throttle", 0);
        inp.Brake = o.Float("brake", 0);
        inp.Steer = o.Float("steer", 0) / 57.3f;
        int n = o.Int("steps", 60), every = o.Int("every", 3);
        for (int i = 0; i < n; i++)
        {
            BotDriverPlugin.Core.Physics.Vehicle.Step(d, st, inp, 1f / 300);
            if (i % every == 0)
                Console.WriteLine(FormattableString.Invariant($"{i,4} v={st.Vx:F2} ax={st.Ax:F2} ay={st.Ay:F2} rpm={st.EngineRpm:F0} clutch={st.ClutchLocked} w={st.Omega[0]:F1}/{st.Omega[1]:F1}/{st.Omega[2]:F1}/{st.Omega[3]:F1} k={st.Kappa[0]:F3}/{st.Kappa[1]:F3}/{st.Kappa[2]:F3}/{st.Kappa[3]:F3} a={st.Alpha[2] * 57.3f:F2} nd={st.NdSlip[0]:F2}/{st.NdSlip[1]:F2}/{st.NdSlip[2]:F2}/{st.NdSlip[3]:F2} fz={st.Load[0]:F0}/{st.Load[1]:F0}/{st.Load[2]:F0}/{st.Load[3]:F0} h={st.H:F3} p={st.Pitch * 57.3f:F2} r={st.R * 57.3f:F1} vy={st.Vy:F2}"));
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
        if (RoadSurface.Load(trackRoot, layout, valid: false) is { } offSurf)
        {
            var seen = new Dictionary<string, int>();
            for (float s = 0; s < line.Length; s += 10)
            {
                int i = line.IndexAt(s);
                foreach (float off2 in new[] { line.RoomPlus[i] + 1.5f, -line.RoomMinus[i] - 1.5f })
                {
                    var p = line.PositionAt(s, off2);
                    string k = offSurf.SurfaceAt(p.X, p.Z, p.Y) is { } sf ? $"{sf.Key} (grip {sf.Friction:F2}, damping {sf.Damping:F2})" : "-";
                    seen[k] = seen.GetValueOrDefault(k) + 1;
                }
            }
            Console.WriteLine($"beside the track ({offSurf.Triangles} triangles): " + string.Join(", ", seen.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} {x.Value}")));
        }
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
        foreach (float r in new[] { 300f, 500f, 1000f })
            Console.WriteLine($"share of the lap straighter than radius {r} m: {line.Curvature.Count(k => MathF.Abs(k) < 1 / r) * 100f / line.Count:F0} %");
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
        var settings = new RaceWorldSettings();
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
                Console.WriteLine($"   {pct,4:F0} % -> AI level {StrengthCalibration.PaceFor(pct):F3}  target {Simulator.Fmt(cal.LapTimeFor(pct))}");
        }
        return 0;
    }

    public static void PrintSpec(CarSpec s)
    {
        Console.WriteLine($"{s.Model} ({s.Source}): top {s.TopSpeed * 3.6f:F0} km/h, lat grip {s.LateralGrip:F2} g, brake {s.BrakeGrip:F2} g, " +
                          $"downforce {s.Downforce * 10000:F1}e-4, aeroBrake {s.AeroBrake * 10000:F1}e-4, L {s.Length:F2} W {s.Width:F2} WB {s.Wheelbase:F2}, " +
                          $"rpm {s.IdleRpm}-{s.MaxRpm} (up {s.UpshiftRpm}/down {s.DownshiftRpm}, shift {s.ShiftUpTime * 1000:F0}/{s.ShiftDownTime * 1000:F0} ms{(s.HasAbs ? ", ABS" : "")}{(s.HasTc ? ", TC" : "")}{$", brakes {s.BrakeFront * 100:F0} % front, exit slide x{s.ExitSlide:F2}"}{$", damage from {string.Join("/", s.DamageMinKmh.Select(x => x.ToString("F0")))} km/h, susp {s.SuspDamageMinKmh:F0}, front cd {s.ZoneCd[0]:F4} cl {s.ZoneCl[0]:F4}, rear cl {s.ZoneCl[1]:F4}"}{$", tyres {s.TyreCompound} best at {s.TyreOptimum:F0} °C" + (s.TyreTempCurve != null ? $" (60 °C {s.TyreTempCurve.At(60):F2}, 100 °C {s.TyreTempCurve.At(100):F2})" : "")}{(s.SpoolUp > 0 ? $", turbo lag {s.SpoolUp:F2}/{s.SpoolDown:F2} s, boost up to {s.TurboAccel!.Max():F1} m/s²" : "")}), gears [{string.Join(", ", s.GearTopSpeedsKmh.Select(g => g.ToString("F0")))}] km/h");
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
            // full-throttle acceleration per 20 km/h band (to compare with a player's recorded laps)
            Console.WriteLine("    accel m/s² " + string.Join("  ", Enumerable.Range(0, 11).Select(i => $"{i * 20 + 10}:{s.AccelAt((i * 20 + 10) / 3.6f, 1):F2}")));
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
