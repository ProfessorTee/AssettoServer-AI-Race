using TrackGeometry;
using System.Numerics;
using System.Diagnostics;
using BotDriverPlugin.Core;

namespace BotDriverTool;

/// <summary>Runs bots offline on a real track with the same code the plugin uses.</summary>
public static class Simulator
{
    public static int Run(Options o)
    {
        var (trackRoot, layout, carsRoot) = Program.ResolvePaths(o);
        int botCount = o.Int("bots", 12);
        int laps = o.Int("laps", 2);
        float strength = o.Float("strength", 95);
        float spread = o.Float("spread", 3);
        float aggression = o.Float("aggression", 50);
        int seed = o.Int("seed", 1);
        bool hotlap = o.Has("hotlap");
        float tickHz = o.Float("hz", 18);
        var models = (o.Get("models") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

        var sw = Stopwatch.StartNew();
        var line = new RacingLine(FastLaneFile.Read(Program.FastLanePath(trackRoot, layout)));
        var info = TrackInfo.LoadLayoutData(Program.LayoutDataDir(trackRoot, layout));
        line.ApplyHints(info.SpeedHints, info.MaxSpeedsKmh);
        try { info.LoadSpotsFromKn5(trackRoot, layout); }
        catch (Exception ex) { Console.WriteLine($"No grid from kn5: {ex.Message}"); }
        Console.WriteLine($"Line: {line.Count} pts, {line.Length:F0} m, {info.SpeedHints.Count} hints, {info.Sections.Count} sections, {info.StartGrid.Count} grid spots ({sw.ElapsedMilliseconds} ms)");

        var settings = new RaceWorldSettings
        {
            Seed = seed,
            FuelRate = o.Float("fuel-rate", 1),
            TyreWearRate = o.Float("wear-rate", 1),
            PitWindowStart = o.Int("pit-window-start", 0),
            PitWindowEnd = o.Int("pit-window-end", 0),
            RaceStartTime = 0,
            HumanErrors = !o.Has("no-errors"),
            PushVariation = !o.Has("no-push"),
            Wetness = o.Float("wet", 0),
            Water = o.Float("water", 0),
            BotContacts = !o.Has("no-contacts"),
            Damage = !o.Has("no-damage"),
            GripFactor = o.Float("grip", 1),
            AmbientTemp = o.Float("ambient", 22),
            RoadTemp = o.Float("road", 30),
        };
        if (info.StartFinish is { } sf) settings.StartLineS = line.Project(sf).S;
        var world = new RaceWorld(line, settings);
        if (!o.Has("no-surfaces")) world.OffTrack = RoadSurface.Load(trackRoot, layout, valid: false);
        if (!o.Has("no-road")) world.Road = RoadSurface.Load(trackRoot, layout);
        if (o.Get("step") != null) world.MaxStep = o.Float("step", 0.01f);
        int contactLines = 0;
        if (o.Has("contacts")) world.ContactLog = l => { if (contactLines++ < 40) Console.WriteLine("contact " + l); };
        var pitPath = Path.Join(Path.GetDirectoryName(Program.FastLanePath(trackRoot, layout))!, "pit_lane.ai");
        if (File.Exists(pitPath) && !o.Has("no-pits"))
        {
            world.PitLane = new PitLane(FastLaneFile.Read(pitPath), line);
            Console.WriteLine($"Pit lane {world.PitLane.Length:F0} m, entry at {world.PitLane.EntryTrackS:F0}, exit at {world.PitLane.ExitTrackS:F0}, limiter {world.PitLane.LimiterStart:F0}-{world.PitLane.LimiterEnd:F0}");
        }

        var calibrations = new Dictionary<string, StrengthCalibration>();
        var specs = new Dictionary<string, CarSpec>();
        CarSpec SpecFor(string model)
        {
            if (!specs.TryGetValue(model, out var s))
            {
                s = string.IsNullOrEmpty(carsRoot) ? new CarSpec() : CarDataLoader.Load(carsRoot, model, log: Console.WriteLine);
                Program.PrintSpec(s);
                specs[model] = s;
            }
            return s;
        }

        var rng = new Random(seed);
        int n = hotlap ? 1 : botCount;
        var strengths = hotlap ? [strength] : StrengthCalibration.Distribute(n, strength, spread, o.Has("random-spread"), rng);
        for (int i = 0; i < n; i++)
        {
            string model = models.Length > 0 ? models[i % models.Length] : "generic_gt3";
            var spec = models.Length > 0 ? SpecFor(model) : new CarSpec();
            if (spec.Physics == null) { Console.WriteLine($"{model}: no physics data, can't drive"); return 1; }
            if (!calibrations.TryGetValue(spec.Model, out var cal))
            {
                cal = StrengthCalibration.Measure(line, spec, settings);
                calibrations[spec.Model] = cal;
                Console.WriteLine($"  {spec.Model}: 100 % = {Fmt(cal.BestLap)}");
            }
            float aggr = Math.Clamp(aggression + ((float)rng.NextDouble() * 2 - 1) * o.Float("aggression-spread", 10), 0, 100);
            var bot = new RaceBot
            {
                Id = i,
                Name = BotNames.Default[i % BotNames.Default.Length],
                Car = spec,
                Driver = DriverProfile.FromStrength(strengths[i], StrengthCalibration.PaceFor(strengths[i]), aggr)
            };
            world.Bots.Add(bot);
            if (info.PitBoxes.FindIndex(p => p.Index == i) is var bi and >= 0 && info.PitBoxes[bi] is var box)
                world.SetPitBox(bot, box.Position);
        }
        foreach (var bot in world.Bots)
        {
            world.ResetCarCondition(bot, hotlap ? 30 : world.FuelForLaps(bot, laps + 0.5f));
            world.ChooseTyres(bot, laps);
            world.SetTyreTemperature(bot, RaceWorld.TyreOptimumOf(bot) - 5);
        }

        // optional scripted "human": an AI car in a world of its own (it doesn't see the bots), fed to the bots as a player
        RaceWorld? playerWorld = null;
        ExternalCar? player = null;
        float playerLevel = o.Float("player-pace", 0);
        if (playerLevel > 0)
        {
            playerWorld = new RaceWorld(line, new RaceWorldSettings { StartLineS = settings.StartLineS, Seed = seed + 1, HumanErrors = false })
                { Road = world.Road, OffTrack = world.OffTrack };
            playerWorld.Bots.Add(new RaceBot { Id = 1000, Name = "Player", Car = world.Bots[0].Car, Driver = new DriverProfile { Pace = playerLevel } });
            player = world.GetOrAddExternal(1000);
        }

        double now = 0;
        if (hotlap)
        {
            world.PlaceAt(world.Bots[0], settings.StartLineS - 800, 0, BotPhase.Racing);
            world.Bots[0].Speed = 50;
        }
        else
        {
            // grid: player (if any) takes the middle slot; --slowest-first: the weakest in front (BotGridOrder SlowestFirst)
            int slot = 0;
            int playerSlot = player != null ? n / 2 : -1;
            foreach (var bot in o.Has("slowest-first") ? world.Bots.OrderBy(b => b.Driver.Level).ToList() : world.Bots.ToList())
            {
                if (slot == playerSlot) slot++;
                PlaceOnGrid(world, info, bot, slot++);
            }
            if (playerWorld != null)
            {
                PlaceOnGrid(playerWorld, info, playerWorld.Bots[0], playerSlot);
                playerWorld.StartRace(0);
            }
            world.StartRace(0);
        }

        world.TraceBotId = o.Int("trace", -1);
        world.Trace = Console.WriteLine;
        var stats = new SimStats(world);
        // --dump file.csv: the hot lap's second lap, sample by sample (to compare with a player's recording)
        using var dump = o.Get("dump") is { } dumpPath ? new StreamWriter(dumpPath) : null;
        // --rec <folder with a player's laps>: the hot lap compared with his best laps corner by corner
        var botLap = new List<(float S, float V)>();
        float dt = 1f / tickHz;
        double maxTime = hotlap ? 60 * 20 : laps * 60 * 12 + 120;
        world.PitStopCompleted += (bot, t, fuel, tyres) =>
            Console.WriteLine($"  PIT {bot.Name,-20} lap {bot.LapsCompleted + 1} {bot.PitReason,-9} {t,5:F1} s stationary, +{fuel:F0} l{(tyres ? ", tyres " + RaceWorld.CompoundName(bot) : "")}");
        world.LapCompleted += (bot, lap) =>
        {
            if (hotlap || o.Has("verbose"))
                Console.WriteLine($"  {bot.Name,-22} lap {bot.LapsCompleted} {Fmt(lap)} fuel {bot.Fuel:F1} grip {bot.CarGrip:F3}");
        };

        // --poses: how smooth the poses the clients get are (jumps against the sent velocity, yaw jitter, heading against travel)
        var poses = o.Has("poses") ? new PoseProbe() : null;
        world.Advance(0);
        playerWorld?.Advance(0);
        double firstPass = double.NaN;
        var behindPlayer = new Dictionary<int, float>();
        var prevRel = new Dictionary<int, double>();
        int passedPlayer = 0;
        float closeBehind = 0;
        while (now < maxTime)
        {
            now += dt;
            if (playerWorld != null && player != null)
            {
                playerWorld.Advance(now);
                var pose = playerWorld.GetPose(playerWorld.Bots[0]);
                world.UpdateExternal(player, pose.Position, pose.Velocity);
            }
            foreach (var b in world.Bots) b.RemainingLaps = hotlap ? int.MaxValue : Math.Max(0, laps - b.LapsCompleted);
            world.Advance(now);
            stats.Sample(now);
            if (poses != null && now > 5) poses.Sample(world, dt);
            // a few bots: when does the fastest first lead all the others (tools/passtest.sh)
            if (world.Bots.Count <= 6 && double.IsNaN(firstPass) && now > 1)
            {
                var fast = world.Bots.MaxBy(b => b.Driver.Level)!;
                if (world.Bots.All(b => b == fast || fast.Distance > b.Distance + 2)) firstPass = now;
            }
            // bots against the player: passes on him and time spent right behind him
            if (playerWorld != null && now > 20)
            {
                double pd = playerWorld.Bots[0].Distance;
                foreach (var b in world.Bots)
                {
                    if (b.Phase != BotPhase.Racing) continue;
                    double rel = b.Distance - pd;
                    if (rel > -40 && rel < 0 && b.Speed > 5) behindPlayer[b.Id] = behindPlayer.GetValueOrDefault(b.Id) + dt;
                    if (prevRel.TryGetValue(b.Id, out var pr) && pr < 0 && rel >= 0 && rel < 50) passedPlayer++;
                    prevRel[b.Id] = rel;
                }
                if (world.Bots.Any(x => x.Phase == BotPhase.Racing && x.Distance < pd && x.Distance > pd - 40)) closeBehind += dt;
            }
            if (hotlap && world.Bots[0].LapsCompleted >= 1)
                botLap.Add((line.WrapS((float)world.Bots[0].Distance), world.Bots[0].Speed));
            if (dump != null && world.Bots[0].LapsCompleted == 1)
            {
                var db = world.Bots[0];
                dump.WriteLine(FormattableString.Invariant($"{now:F2},{line.WrapS((float)db.Distance) / line.Length:F5},{db.Speed * 3.6f:F1},{db.Throttle:F2},{db.Brake:F2},{db.Offset:F2},{line.CurvatureAt(line.WrapS((float)db.Distance)):F5},{db.TargetSpeed * 3.6f:F0}"));
            }
            if (hotlap && world.Bots[0].LapsCompleted >= Math.Max(1, laps)) break;
            if (!hotlap && world.Bots.All(b => b.LapsCompleted >= laps)) break;
        }

        Console.WriteLine();
        Console.WriteLine($"Simulated {now:F0} s in {sw.Elapsed.TotalSeconds:F1} s real time");
        stats.Print();
        poses?.Print();
        if (world.Bots.Count <= 6) Console.WriteLine($"first pass: {(double.IsNaN(firstPass) ? "never" : firstPass.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))}");
        if (playerWorld != null)
            Console.WriteLine($"player: passed by bots {passedPlayer}x, a bot right behind him {closeBehind:F0} s, bot-seconds within 40 m behind him {behindPlayer.Values.Sum():F0}, " +
                              $"most {(behindPlayer.Count > 0 ? behindPlayer.Values.Max() : 0):F0} s, player {playerWorld.Bots[0].LapsCompleted} laps");
        if (hotlap && o.Get("rec") is { } recDir) CompareCorners(line, info, settings.StartLineS, recDir, botLap);
        if (!hotlap)
        {
            // held up: best race lap against the lap time the bot's strength gives (traffic costs time, the faster drivers stuck in trains most)
            var loss = world.Bots.Where(b => b.BestLapSeconds < 1e6f && calibrations.ContainsKey(b.Car.Model))
                .Select(b => (b, l: b.BestLapSeconds - calibrations[b.Car.Model].LapTimeFor(b.Driver.Level))).OrderByDescending(x => x.l).ToList();
            if (loss.Count > 0)
                Console.WriteLine($"held up: best lap vs strength target avg +{loss.Average(x => x.l):F2} s, median +{loss[loss.Count / 2].l:F2} s, " +
                                  $"over 2 s: {loss.Count(x => x.l > 2)} of {loss.Count}; worst " +
                                  string.Join(", ", loss.Take(4).Select(x => $"{x.b.Name} {x.b.Driver.Level:F0}% +{x.l:F1}")));
        }
        return 0;
    }

    /// <summary>
    /// Where the bot brakes and how fast it goes through each corner against the player's best recorded laps: the braking point is the
    /// last speed peak before the corner, the corner speed the lowest speed around it.
    /// </summary>
    private static void CompareCorners(RacingLine line, TrackInfo info, float startLineS, string recDir, List<(float S, float V)> botLap)
    {
        // his clean laps within 3 % of his best against all the bot's laps after the first: averages, one lap is too random
        var valid = LoadRecordedLaps(recDir).Where(l => l.Valid && l.Samples.Count > 50).ToList();
        var lap = valid.MinBy(l => l.LapTime);
        if (lap == null || botLap.Count < 50) { Console.WriteLine("corners: no valid recorded lap or no bot lap"); return; }
        var laps = valid.Where(l => l.LapTime <= lap.LapTime * 1.03f).ToList();
        const float bin = 5;
        int n = (int)(line.Length / bin) + 1;
        float[] Profile(IEnumerable<(float S, float V)> samples)
        {
            var sum = new float[n];
            var cnt = new int[n];
            foreach (var (s, sp) in samples)
            {
                int i = (int)(line.WrapS(s) / bin) % n;
                sum[i] += sp;
                cnt[i]++;
            }
            var v = Enumerable.Range(0, n).Select(i => cnt[i] > 0 ? sum[i] / cnt[i] : float.NaN).ToArray();
            for (int k = 0; k < 2 * n; k++) // fill the gaps from the neighbours
                if (float.IsNaN(v[k % n])) v[k % n] = v[(k + n - 1) % n];
            return v;
        }
        var player = Profile(laps.SelectMany(l =>
        {
            int hint = -1;
            return l.Samples.Select(smp =>
            {
                var p = line.Project(smp.Position, hint);
                hint = p.Index;
                return (p.S, smp.Speed);
            }).ToList();
        }));
        var bot = Profile(botLap);
        float At(float[] v, int i) => v[((i % n) + n) % n];
        Console.WriteLine($"Corners against the player's {laps.Count} best laps (best {TimeSpan.FromSeconds(lap.LapTime).ToString(@"m\:ss\.fff")}), averaged: braking point (+ = bot brakes later), slowest speed");
        int last = -1000;
        for (int i = 0; i < n; i++)
        {
            float vi = At(player, i);
            if (float.IsNaN(vi) || i - last < 20) continue;
            bool isMin = true;
            for (int k = -8; k <= 8 && isMin; k++) if (At(player, i + k) < vi) isMin = false;
            float peak = 0;
            int peakAt = i;
            for (int k = 1; k <= 80; k++) if (At(player, i - k) > peak) { peak = At(player, i - k); peakAt = i - k; }
            if (!isMin || peak - vi < 15 / 3.6f) continue;
            last = i;
            int bMin = i;
            for (int k = -12; k <= 12; k++) if (At(bot, i + k) < At(bot, bMin)) bMin = i + k;
            int bPeak = bMin;
            float bp = 0;
            for (int k = 1; k <= 80; k++) if (At(bot, bMin - k) > bp) { bp = At(bot, bMin - k); bPeak = bMin - k; }
            float s = i * bin;
            string where = info.SectionAt(line.WrapS(s - startLineS) / line.Length) ?? "";
            Console.WriteLine($"  {s,6:F0} m {where,-22} brake {(bPeak - peakAt) * bin,+5:F0} m   min {vi * 3.6f,4:F0} / {At(bot, bMin) * 3.6f,4:F0} km/h ({(At(bot, bMin) - vi) * 3.6f,+4:F0})");
        }
    }

    private sealed record RecordedSample(Vector3 Position, float Speed);
    private sealed record RecordedLap(float LapTime, bool Valid, List<RecordedSample> Samples);

    /// <summary>DriverRecorder laps (*.csv.gz): position and speed per sample, lap time and validity from the header.</summary>
    private static List<RecordedLap> LoadRecordedLaps(string dir)
    {
        var laps = new List<RecordedLap>();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var f in Directory.GetFiles(dir, "*.csv.gz"))
        {
            using var gz = new System.IO.Compression.GZipStream(File.OpenRead(f), System.IO.Compression.CompressionMode.Decompress);
            using var r = new StreamReader(gz);
            var samples = new List<RecordedSample>();
            float lapTime = 0;
            bool valid = false;
            int ix = -1, iy = -1, iz = -1, iv = -1;
            string? l;
            while ((l = r.ReadLine()) != null)
            {
                if (l.StartsWith("# laptime_ms=")) lapTime = float.Parse(l[13..], ci) / 1000f;
                else if (l.StartsWith("# valid=")) valid = l.EndsWith("1");
                else if (l.StartsWith("t,"))
                {
                    var h = l.Split(',');
                    ix = Array.IndexOf(h, "x"); iy = Array.IndexOf(h, "y"); iz = Array.IndexOf(h, "z"); iv = Array.IndexOf(h, "speed_kmh");
                }
                else if (!l.StartsWith('#') && ix >= 0)
                {
                    var p = l.Split(',');
                    float F(int i) => float.Parse(p[i], ci);
                    samples.Add(new RecordedSample(new Vector3(F(ix), F(iy), F(iz)), F(iv) / 3.6f));
                }
            }
            laps.Add(new RecordedLap(lapTime, valid, samples));
        }
        return laps;
    }

    private static void PlaceOnGrid(RaceWorld world, TrackInfo info, RaceBot bot, int slot)
    {
        var spot = info.StartGrid.FirstOrDefault(g => g.Index == slot);
        if (spot.Index == slot && info.StartGrid.Count > 0)
            world.PlaceAtWorld(bot, spot.Position, BotPhase.Grid);
        else
            world.PlaceAtGridSlot(bot, slot);
    }

    public static string Fmt(float seconds) => TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.fff");

    /// <summary>Overlaps, cars off the road, position changes, pedals, for the end of a simulated race.</summary>
    private sealed class SimStats
    {
        private readonly RaceWorld _w;
        private int _overlapFrames, _offTrackFrames, _frames, _positionChanges;
        private float _maxPenetration, _maxOffTrack;
        private readonly long[] _pedals = new long[6];
        private readonly Dictionary<int, int> _order = new();
        private double _lastOrderCheck;

        public SimStats(RaceWorld w) => _w = w;

        public void Sample(double now)
        {
            _frames++;
            var line = _w.Line;
            var bots = _w.Bots.Where(b => b.OnTrack).ToList();
            for (int i = 0; i < bots.Count; i++)
            {
                var a = bots[i];
                if (a.InPitLane) continue;
                float sa = line.WrapS((float)a.Distance);
                int idx = line.IndexAt(sa);
                if (a.Phase == BotPhase.Racing)
                    _pedals[a.Brake < 0.04f ? (a.Throttle > 0.95f ? 0 : a.Throttle > 0.05f ? 1 : 2) : a.Brake < 0.35f ? 3 : a.Brake < 0.8f ? 4 : 5]++;
                float excess = MathF.Max(-line.RoomMinus[idx] + a.Car.Width / 2 - a.Offset, a.Offset - (line.RoomPlus[idx] - a.Car.Width / 2));
                if (excess > 0.05f) _offTrackFrames++;
                _maxOffTrack = MathF.Max(_maxOffTrack, excess);
                for (int j = i + 1; j < bots.Count; j++)
                {
                    var b = bots[j];
                    if (a.Phase == BotPhase.Grid && b.Phase == BotPhase.Grid) continue;
                    float longPen = (a.Car.Length + b.Car.Length) / 2 - MathF.Abs(line.Delta(sa, line.WrapS((float)b.Distance)));
                    float latPen = (a.Car.Width + b.Car.Width) / 2 - MathF.Abs(a.Offset - b.Offset);
                    if (longPen > 0.05f && latPen > 0.05f)
                    {
                        _overlapFrames++;
                        _maxPenetration = MathF.Max(_maxPenetration, MathF.Min(longPen, latPen));
                    }
                }
            }
            if (now - _lastOrderCheck > 1)
            {
                _lastOrderCheck = now;
                foreach (var (id, pos) in _w.Bots.OrderByDescending(b => b.Distance).Select((b, i) => (b.Id, i)))
                {
                    if (_order.TryGetValue(id, out var old) && old != pos) _positionChanges++;
                    _order[id] = pos;
                }
            }
        }

        public void Print()
        {
            Console.WriteLine($"{"Pos",-4}{"Driver",-22}{"Lvl",5}{"Laps",5}{"Best",11}{"Last",11}{"Total",11}{"Ovt",5}{"Pit",4}");
            int pos = 1;
            foreach (var b in _w.Bots.OrderByDescending(b => b.LapsCompleted).ThenBy(b => b.TotalTime))
                Console.WriteLine($"{pos++,-4}{("#" + b.Id + " " + b.Name),-22}{b.Driver.Level,5:F0}{b.LapsCompleted,5}{(b.BestLapSeconds < 1e6 ? Fmt(b.BestLapSeconds) : "-"),11}" +
                                  $"{(b.LastLapSeconds > 0 ? Fmt(b.LastLapSeconds) : "-"),11}{(b.TotalTime > 0 ? Fmt((float)b.TotalTime) : "-"),11}{b.Overtakes,5}{b.PitStops,4} " +
                                  $"level={b.Driver.Pace:F3} aggr={b.Driver.Aggression:F2} cont={b.ContactCount} ghost={b.GhostCount} dmg={RaceWorld.BodyDamagePercent(b):F0}%/{b.Suspension * 100:F0}% " +
                                  $"fuel={b.Fuel,5:F1} grip={b.CarGrip:F3} tyres={RaceWorld.CompoundName(b)} {b.TyreTempFront:F0}/{b.TyreTempRear:F0}C {b.Phase}");
            long tot = Math.Max(1, _pedals.Sum());
            Console.WriteLine($"pedals: full throttle {_pedals[0] * 100 / tot} %, part throttle {_pedals[1] * 100 / tot} %, coasting {_pedals[2] * 100 / tot} %, " +
                              $"light brake {_pedals[3] * 100 / tot} %, medium {_pedals[4] * 100 / tot} %, hard {_pedals[5] * 100 / tot} %");
            Console.WriteLine($"overlap frames {_overlapFrames} (max penetration {_maxPenetration:F2} m), off-track frames {_offTrackFrames} (max {_maxOffTrack:F2} m), " +
                              $"position changes {_positionChanges / 2}, frames {_frames}");
            Console.WriteLine($"passes {_w.Bots.Sum(b => b.Overtakes)} contacts {_w.Bots.Sum(b => b.ContactCount) / 2} ghosts {_w.Bots.Sum(b => b.GhostCount)}");
        }
    }

    private sealed class PoseProbe
    {
        private readonly Dictionary<int, (Vector3 P, Vector3 V, float Yaw, float Rate)> _prev = new();
        private readonly List<float> _jump = new(), _jerk = new(), _slip = new();
        private int _big;
        private readonly List<float> _lineErr = new();
        private readonly List<(float Slip, string Where)> _worst = new();

        public void Sample(RaceWorld world, float dt)
        {
            foreach (var b in world.Bots)
            {
                if (b.Phase != BotPhase.Racing || b.InPitLane || b.Speed < 5) { _prev.Remove(b.Id); continue; }
                var p = world.GetPose(b);
                float yaw = p.Rotation.X;
                if (_prev.TryGetValue(b.Id, out var q))
                {
                    // where the client extrapolates the car from the last packet against where the next one puts it
                    float jump = (p.Position - (q.P + q.V * dt)).Length();
                    float rate = MathF.IEEERemainder(yaw - q.Yaw, 2 * MathF.PI) / dt;
                    _jump.Add(jump);
                    if (jump > 0.5f) _big++;
                    if (!float.IsNaN(q.Rate)) _jerk.Add(MathF.Abs(rate - q.Rate) * 57.3f);
                    var dir = p.Position - q.P;
                    float heading = MathF.Atan2(dir.Z, dir.X) - MathF.PI / 2;
                    float slip = MathF.Abs(MathF.IEEERemainder(yaw - heading, 2 * MathF.PI)) * 57.3f;
                    _slip.Add(slip);
                    if (b.Ai is { } ai) _lineErr.Add(MathF.Abs(b.Offset - ai.DesiredOffset));
                    if (slip > 8) _worst.Add((slip, FormattableString.Invariant($"{b.Name} s={world.Line.WrapS((float)b.Distance):F0} v={b.Speed * 3.6f:F0} yaw={b.Yaw * 57.3f:F1} psi={b.Phys?.Psi * 57.3f:F1} off={b.Offset:F1} thr={b.Throttle:F2} brk={b.Brake:F2} g={b.Gear}")));
                    _prev[b.Id] = (p.Position, p.Velocity, yaw, rate);
                }
                else _prev[b.Id] = (p.Position, p.Velocity, yaw, float.NaN);
            }
        }

        private static string Q(List<float> x)
        {
            if (x.Count == 0) return "-";
            var s = x.OrderBy(v => v).ToList();
            return FormattableString.Invariant($"p50 {s[s.Count / 2]:F2} p99 {s[(int)(s.Count * 0.99)]:F2} max {s[^1]:F2}");
        }

        public void Print()
        {
            Console.WriteLine($"poses: extrapolation error m {Q(_jump)}, over 0.5 m {_big}x");
            Console.WriteLine($"poses: yaw rate change deg/s per packet {Q(_jerk)}");
            Console.WriteLine($"poses: body vs travel direction deg {Q(_slip)}, over 8° {_worst.Count}x ({100f * _worst.Count / Math.Max(1, _slip.Count):F1} %)");
            Console.WriteLine($"poses: off its wanted line m {Q(_lineErr)}, over 2 m {100f * _lineErr.Count(x => x > 2) / Math.Max(1, _lineErr.Count):F1} %");
            foreach (var w in _worst.OrderByDescending(w => w.Slip).Take(12)) Console.WriteLine(FormattableString.Invariant($"   {w.Slip:F1}° {w.Where}"));
            foreach (var g in _worst.GroupBy(w => (int)float.Parse(w.Where.Split(" s=")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) / 100).OrderByDescending(g => g.Count()).Take(8))
                Console.WriteLine($"   s {g.Key * 100}-{g.Key * 100 + 100}: {g.Count()}x, max {g.Max(w => w.Slip):F0}°");
        }
    }
}
