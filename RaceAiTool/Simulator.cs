using System.Numerics;
using System.Diagnostics;
using RaceAiPlugin.Core;

namespace RaceAiTool;

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
        var raw = FastLaneFile.Read(Program.FastLanePath(trackRoot, layout));
        var line = new RacingLine(raw);
        var info = TrackInfo.LoadLayoutData(Program.LayoutDataDir(trackRoot, layout));
        line.ApplyHints(info.SpeedHints, info.MaxSpeedsKmh);
        try
        {
            info.LoadSpotsFromKn5(trackRoot, layout);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"No grid from kn5: {ex.Message}");
        }
        Console.WriteLine($"Line: {line.Count} pts, {line.Length:F0} m, SideLeftIsPlus={line.SideLeftIsPlus}, width median {Median(line.RoomPlus.Zip(line.RoomMinus, (a, b) => a + b)):F1} m, " +
                          $"{info.SpeedHints.Count} hints, {info.Sections.Count} sections, {info.StartGrid.Count} grid spots ({sw.ElapsedMilliseconds} ms)");

        var settings = new RaceWorldSettings
        {
            Seed = seed,
            FuelRate = o.Float("fuel-rate", 1),
            TyreWearRate = o.Float("wear-rate", 1),
            PitWindowStart = o.Int("pit-window-start", 0),
            PitWindowEnd = o.Int("pit-window-end", 0),
            RaceStartTime = 0,
            HumanErrors = !o.Has("no-errors"),
            Wetness = o.Float("wet", 0),
            Water = o.Float("water", 0),
            RainIntensity = o.Float("rain", 0),
            Spins = !o.Has("no-spins"),
            GrassMoments = !o.Has("no-grass"),
            BotContacts = !o.Has("no-contacts"),
            Damage = !o.Has("no-damage"),
            GripFactor = o.Float("grip", 1),
            AmbientTemp = o.Float("ambient", 22),
            RoadTemp = o.Float("road", 30),
        };
        float errorsBelow = o.Float("errors-below", 87), errorsFull = o.Float("errors-full", 75);
        if (info.StartFinish is { } sf) settings.StartLineS = line.Project(sf).S;
        var world = new RaceWorld(line, settings);
        if (!o.Has("no-ideal"))
            world.Ideal = IdealLine.Load(Path.Join(Path.GetDirectoryName(Path.GetDirectoryName(Program.FastLanePath(trackRoot, layout)))!, "data", "ideal_line.ai"), line);
        if (!o.Has("no-surfaces")) world.OffTrack = RoadSurface.Load(trackRoot, layout, valid: false);
        if (o.Get("step") != null) world.MaxStep = o.Float("step", 0.025f);
        world.MeasureJumps = o.Has("jumps");
        var pitPath = Path.Join(Path.GetDirectoryName(Program.FastLanePath(trackRoot, layout))!, "pit_lane.ai");
        if (File.Exists(pitPath) && !o.Has("no-pits"))
        {
            world.PitLane = new PitLane(FastLaneFile.Read(pitPath), line);
            Console.WriteLine($"Pit lane {world.PitLane.Length:F0} m, entry at {world.PitLane.EntryTrackS:F0}, exit at {world.PitLane.ExitTrackS:F0}, limiter {world.PitLane.LimiterStart:F0}-{world.PitLane.LimiterEnd:F0}");
        }
        var calibrations = new Dictionary<CarSpec, StrengthCalibration>();

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
            if (!calibrations.TryGetValue(spec, out var cal))
            {
                cal = StrengthCalibration.Measure(line, spec, settings);
                calibrations[spec] = cal;
                Console.WriteLine($"  {spec.Model}: 100 % = {Fmt(cal.BestLap)}, mistakes cost {cal.ErrorLossHalf:F1} s/lap at level 0.5, {cal.ErrorLossFull:F1} s at 1");
            }
            var bot = new RaceBot
            {
                Id = i,
                Name = $"{BotNames.Default[i % BotNames.Default.Length]}",
                Car = spec,
                Driver = DriverProfile.FromStrength(strengths[i], 0, aggression)
            };
            if (!o.Has("no-personalities"))
            {
                var defs = Personality.Defaults();
                float r = (float)rng.NextDouble() * defs.Sum(d => d.Share);
                foreach (var d in defs) { r -= d.Share; if (r <= 0) { bot.Driver.Personality = d.Personality; break; } }
                if (o.Get("personality") is { } forced) bot.Driver.Personality = Personality.BuiltIn(forced) ?? bot.Driver.Personality;
                if (o.Get("personalities") is { } cycle) { var names = cycle.Split(','); bot.Driver.Personality = Personality.BuiltIn(names[i % names.Length]) ?? bot.Driver.Personality; }
                bot.Driver.Aggression = Math.Clamp(bot.Driver.Aggression + bot.Driver.Personality.Aggression, 0, 1);
            }
            bot.Driver.Errors = settings.HumanErrors ? DriverProfile.ErrorsFor(strengths[i], errorsBelow, errorsFull) : 0;
            bot.Driver.Pace = cal.PaceFor(strengths[i], null, bot.Driver.Errors);
            world.Bots.Add(bot);
            if (info.PitBoxes.FirstOrDefault(p => p.Index == i) is { } box && info.PitBoxes.Count > i)
                world.SetPitBox(bot, box.Position);
        }
        // --clone <folder with recorded laps (*.csv.gz)>: the first bot becomes a clone of that player
        CloneProfile? cloneProfile = null;
        if (o.Get("clone") is { } cloneDir)
        {
            var recLaps = LoadRecordedLaps(cloneDir);
            cloneProfile = CloneProfile.Build(line, recLaps, "", "player", models.FirstOrDefault() ?? "");
            if (cloneProfile == null) { Console.WriteLine("No usable clean laps in " + cloneDir); return 1; }
            var cb = world.Bots[0];
            cb.Clone = cloneProfile;
            cb.Driver.Pace = 1; cb.Driver.Consistency = 0.97f;
            float spreadRel = cloneProfile.AverageLap > 0 ? cloneProfile.LapSpread / cloneProfile.AverageLap : 0;
            cb.Driver.Errors = settings.HumanErrors ? Math.Clamp(spreadRel * 25, 0.05f, 0.6f) : 0;
            Console.WriteLine($"Clone: {recLaps.Count} laps ({recLaps.Count(l => l.Valid)} clean), {cloneProfile.LapsUsed} used, best {Fmt(cloneProfile.BestLap)}, " +
                              $"average {Fmt(cloneProfile.AverageLap)}, errors {cb.Driver.Errors:F2}, offset |avg| {cloneProfile.Offset.Average(MathF.Abs):F2} m, max {cloneProfile.Offset.Max(MathF.Abs):F2} m");
        }
        foreach (var bot in world.Bots)
            world.ResetCarCondition(bot, hotlap ? 30 : world.FuelForLaps(bot, laps + 0.5f));

        // optional scripted "human": drives its own line at a fixed pace and ignores the bots
        RaceWorld? playerWorld = null;
        ExternalCar? player = null;
        float playerPace = o.Float("player-pace", 0);
        if (playerPace > 0)
        {
            playerWorld = new RaceWorld(line, new RaceWorldSettings { StartLineS = settings.StartLineS, Seed = seed + 1 });
            playerWorld.Bots.Add(new RaceBot { Id = 1000, Name = "Player", Car = new CarSpec(), Driver = new DriverProfile { Pace = playerPace, Aggression = o.Float("player-aggression", 0), Consistency = 1 } });
            player = world.GetOrAddExternal(1000);
        }

        double now = 0;
        if (hotlap)
        {
            world.SpreadOnTrack(world.Bots, now);
            world.PlaceAt(world.Bots[0], settings.StartLineS - 800, 0, BotPhase.Racing);
            world.Bots[0].Speed = 50;
        }
        else
        {
            // grid: player (if any) takes the middle slot
            int slot = 0;
            int playerSlot = player != null ? n / 2 : -1;
            // --slowest-first: the weakest in front (BotGridOrder SlowestFirst on the server)
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
        var stats = new SimStats(world) { Behaviour = o.Has("style") };
        var trace = hotlap ? new SpeedTrace(line, info) : null;
        // --dump file.csv: the hot lap's second lap, sample by sample (to compare with a player's recording)
        using var dump = o.Get("dump") is { } dumpPath ? new StreamWriter(dumpPath) : null;
        var cloneCmp = new SortedDictionary<int, (float V, float O, int N)>();
        float dt = 1f / tickHz;
        double maxTime = hotlap ? 60 * 20 : laps * 60 * 12 + 120;
        world.PitStopCompleted += (bot, t, fuel, tyres) =>
            Console.WriteLine($"  PIT {bot.Name,-20} lap {bot.LapsCompleted + 1} {bot.PitReason,-9} {t,5:F1} s stationary, +{fuel:F0} l{(tyres ? ", tyres" : "")}");
        world.LapCompleted += (bot, lap) =>
        {
            if (hotlap || o.Has("verbose"))
                Console.WriteLine($"  {bot.Name,-22} lap {bot.LapsCompleted} {Fmt(lap)} fuel {bot.Fuel:F1} lastLapFuel {bot.LastLapFuel:F1} grip {bot.CarGrip:F3} rem {bot.RemainingLaps}");
        };

        world.Advance(0);
        playerWorld?.Advance(0);
        // network model (--latency seconds one way): the server sees the player's packets late, the player sees the bots' packets late
        // and extrapolates them with their velocity like AC does. Measured from the player's view: overlaps and jumps of cars near him.
        float lastLat = float.NaN, maxLatChange = 0, devMax = 0; int twitches = 0, devN = 0; double devSum = 0;
        float latency = o.Float("latency", 0);
        var net = latency > 0 && playerWorld != null ? new NetModel(line, latency) : null;
        while (now < maxTime)
        {
            now += dt;
            if (playerWorld != null && player != null)
            {
                var pb = playerWorld.Bots[0];
                if (net != null)
                {
                    net.FeedPlayerView(playerWorld, world, now);
                    playerWorld.Advance(now);
                    var pose = playerWorld.GetPose(pb);
                    net.SendPlayer(now, pose.Position, pose.Velocity);
                    if (net.ReceivePlayer(now) is { } pkt) world.UpdateExternal(player, pkt.Pos, pkt.Vel, true, pkt.T);
                }
                else
                {
                    playerWorld.Advance(now);
                    var pose = playerWorld.GetPose(pb);
                    world.UpdateExternal(player, pose.Position, pose.Velocity);
                }
            }
            foreach (var b in world.Bots) b.RemainingLaps = hotlap ? int.MaxValue : Math.Max(0, laps - b.LapsCompleted);
            world.Advance(now);
            net?.SendBots(world, now);
            stats.Sample(now);
            trace?.Sample(world.Bots[0]);
            if (dump != null && world.Bots[0].LapsCompleted == 1)
            {
                var db = world.Bots[0];
                dump.WriteLine(FormattableString.Invariant($"{now:F2},{line.WrapS((float)db.Distance) / line.Length:F5},{db.Speed * 3.6f:F1},{db.Throttle:F2},{db.Brake:F2},{db.Offset:F2},{line.CurvatureAt(line.WrapS((float)db.Distance)):F5},{db.TargetSpeed * 3.6f:F0}"));
            }
            if (cloneProfile != null && world.Bots[0].LapsCompleted >= 1)
            {
                var cb = world.Bots[0];
                // sideways twitches: change of the sideways speed from one frame to the next (what a viewer sees as a snap)
                float dLat = float.IsNaN(lastLat) ? 0 : MathF.Abs(cb.LateralSpeed - lastLat);
                lastLat = cb.LateralSpeed;
                maxLatChange = MathF.Max(maxLatChange, dLat);
                float sDbg = line.WrapS((float)cb.Distance);
                if (o.Has("verbose-clone") && (sDbg < 8 || sDbg > line.Length - 12) && cb.LapsCompleted == 2)
                    Console.WriteLine($"    L {line.Length:F1} s {sDbg:F1} off {cb.Offset:F2} lat {cb.LateralSpeed:F2} prof {cloneProfile.OffsetAt(sDbg, line.Length):F2} tgt {cb.TargetOffset:F2} room +{line.RoomPlusAt(sDbg):F2}/-{line.RoomMinusAt(sDbg):F2}");
                if (dLat > 0.5f)
                {
                    twitches++;
                    if (o.Has("verbose-clone")) Console.WriteLine($"  twitch {dLat:F2} m/s at {line.WrapS((float)cb.Distance):F0} m, off {cb.Offset:F2}, lat {cb.LateralSpeed:F2}, v {cb.Speed * 3.6f:F0}, mistake {cb.Mistake}, overtake {cb.OvertakeTargetId}");
                }
                float rawOff = cb.Offset - cloneProfile.OffsetAt(line.WrapS((float)cb.Distance), line.Length);
                devSum += MathF.Abs(rawOff); devN++; devMax = MathF.Max(devMax, MathF.Abs(rawOff));
                int bin = (int)(line.WrapS((float)cb.Distance) / 100f);
                cloneCmp.TryGetValue(bin, out var acc);
                cloneCmp[bin] = (acc.V + cb.Speed, acc.O + cb.Offset, acc.N + 1);
            }

            if (hotlap && world.Bots[0].LapsCompleted >= Math.Max(1, laps)) break;
            if (!hotlap && world.Bots.All(b => b.LapsCompleted >= laps)) break;
        }

        Console.WriteLine();
        Console.WriteLine($"Simulated {now:F0} s in {sw.Elapsed.TotalSeconds:F1} s real time");
        if (trace != null) trace.Print();
        if (cloneProfile != null)
        {
            Console.WriteLine($"Clone on the player's line: average {devSum / Math.Max(1, devN):F2} m off, at most {devMax:F2} m; " +
                              $"sideways speed changes > 0.5 m/s per frame: {twitches}, largest {maxLatChange:F2} m/s");
            Console.WriteLine("Clone vs player every 100 m (laps 2+): speed km/h player/clone, line offset m player/clone");
            foreach (var (bin, acc) in cloneCmp)
            {
                float s0 = bin * 100 + 50;
                float pv = cloneProfile.SpeedAt(s0, line.Length) * 3.6f, po = cloneProfile.OffsetAt(s0, line.Length);
                float cv = acc.V / acc.N * 3.6f, co = acc.O / acc.N;
                Console.WriteLine($"  {bin * 100,5} m  {pv,5:F0} / {cv,5:F0}  ({cv - pv,+5:F0})   {po,5:F1} / {co,5:F1}");
            }
        }
        stats.Print(laps);
        if (stats.Behaviour) stats.PrintBehaviour(now);
        net?.Print();
        if (world.MeasureJumps)
        {
            Console.WriteLine("Jumps per step > 5 cm (position change not explained by the velocity):");
            foreach (var (k, v) in world.JumpStats.OrderByDescending(x => x.Value.Count))
                Console.WriteLine($"  {k,-20} {v.Count,6}x  max {v.Max:F2} m  avg {v.Sum / v.Count:F2} m");
        }
        return 0;
    }

    /// <summary>Packets between server and player with a fixed one-way delay; the player's view of the bots is what AC shows.</summary>
    private sealed class NetModel
    {
        private readonly RacingLine _line;
        private readonly double _lat;
        private readonly Queue<(double Arrive, double T, Vector3 Pos, Vector3 Vel)> _toServer = new();
        private readonly Queue<(double Arrive, double T, (int Id, Vector3 Pos, Vector3 Vel, float Len, float Wid)[] Cars)> _toPlayer = new();
        private readonly Dictionary<int, (double T, Vector3 Pos, Vector3 Vel)> _seen = new();
        private readonly HashSet<int> _inContact = new();
        private int _episodes, _frames, _snaps, _nearSnaps, _nSnap;
        private float _maxDepth, _sumDepth, _maxNearSnap;
        private double _sumSnap;
        private readonly List<string> _events = new();

        public NetModel(RacingLine line, float latency) { _line = line; _lat = latency; }

        public void SendPlayer(double now, Vector3 pos, Vector3 vel) => _toServer.Enqueue((now + _lat, now, pos, vel));

        public (double T, Vector3 Pos, Vector3 Vel)? ReceivePlayer(double now)
        {
            (double, Vector3, Vector3)? last = null;
            while (_toServer.Count > 0 && _toServer.Peek().Arrive <= now + 1e-9) { var p = _toServer.Dequeue(); last = (p.T, p.Pos, p.Vel); }
            return last;
        }

        public void SendBots(RaceWorld w, double now)
        {
            var cars = w.Bots.Where(b => b.Phase is BotPhase.Racing or BotPhase.CoolDown or BotPhase.Grid)
                .Select(b => { var p = w.GetPose(b); return (b.Id, p.Position, p.Velocity, b.Car.Length, b.Car.Width); }).ToArray();
            _toPlayer.Enqueue((now + _lat, now, cars));
        }

        /// <summary>Delivers bot packets to the player and feeds the extrapolated positions to his world (he avoids what he sees).</summary>
        public void FeedPlayerView(RaceWorld pw, RaceWorld server, double now)
        {
            var me = pw.Bots[0];
            var mePose = pw.GetPose(me);
            bool racing = now > 15 && me.Speed > 5;
            while (_toPlayer.Count > 0 && _toPlayer.Peek().Arrive <= now + 1e-9)
            {
                var pk = _toPlayer.Dequeue();
                foreach (var c in pk.Cars)
                {
                    if (_seen.TryGetValue(c.Id, out var prev) && racing && prev.Vel.Length() > 5)
                    {
                        var predicted = prev.Pos + prev.Vel * (float)(pk.T - prev.T);
                        float snap = Vector3.Distance(predicted, c.Pos);
                        if (snap < 25)
                        {
                            bool near = Vector3.Distance(c.Pos, mePose.Position) < 12;
                            _sumSnap += snap; _nSnap++;
                            if (snap > 0.25f) { _snaps++; if (near) _nearSnaps++; }
                            if (near) _maxNearSnap = MathF.Max(_maxNearSnap, snap);
                            if (near && snap > 1 && _events.Count < 25) _events.Add($"  {now,7:F1}s snap {snap:F2} m of bot {c.Id} near the player");
                        }
                    }
                    _seen[c.Id] = (pk.T, c.Pos, c.Vel);
                }
            }
            var mp = _line.Project(mePose.Position);
            foreach (var (id, st) in _seen)
            {
                float age = Math.Clamp((float)(now - st.T), 0, 0.5f);
                var shown = st.Pos + st.Vel * age;
                var e = pw.GetOrAddExternal(id);
                pw.UpdateExternal(e, shown, st.Vel);
                if (!racing) continue;
                var pr = _line.Project(shown);
                var bot = server.Bots.First(b => b.Id == id);
                float lp = (me.Car.Length + bot.Car.Length) / 2 - MathF.Abs(_line.Delta(mp.S, pr.S));
                float wp = (me.Car.Width + bot.Car.Width) / 2 - MathF.Abs(pr.Offset - mp.Offset);
                if (lp > 0 && wp > 0)
                {
                    float depth = MathF.Min(lp, wp);
                    _frames++; _sumDepth += depth; _maxDepth = MathF.Max(_maxDepth, depth);
                    if (_inContact.Add(id))
                    {
                        _episodes++;
                        if (_events.Count < 25) _events.Add($"  {now,7:F1}s overlap with {bot.Name} depth {depth:F2} m (long {lp:F2} lat {wp:F2}) player {me.Speed * 3.6f:F0} km/h bot {bot.Speed * 3.6f:F0} km/h");
                    }
                }
                else _inContact.Remove(id);
            }
        }

        public void Print()
        {
            Console.WriteLine($"Network {_lat * 1000:F0} ms one way, seen by the player: {_episodes} overlaps ({_frames} frames, max {_maxDepth:F2} m, " +
                              $"avg {(_frames > 0 ? _sumDepth / _frames : 0):F2} m), jumps > 0.25 m: {_snaps} ({_nearSnaps} near him, max {_maxNearSnap:F2} m), " +
                              $"average prediction error {(_nSnap > 0 ? _sumSnap / _nSnap * 100 : 0):F1} cm");
            foreach (var e in _events) Console.WriteLine(e);
        }
    }

    private static List<RecordedLap> LoadRecordedLaps(string dir)
    {
        var laps = new List<RecordedLap>();
        foreach (var f in Directory.GetFiles(dir, "*.csv.gz"))
        {
            using var gz = new System.IO.Compression.GZipStream(File.OpenRead(f), System.IO.Compression.CompressionMode.Decompress);
            using var r = new StreamReader(gz);
            var samples = new List<RecordedSample>();
            float lapTime = 0; bool valid = false;
            int ix = -1, iy = -1, iz = -1, iv = -1, ig = -1, ib = -1;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string? l;
            while ((l = r.ReadLine()) != null)
            {
                if (l.StartsWith("# laptime_ms=")) lapTime = float.Parse(l[13..], ci) / 1000f;
                else if (l.StartsWith("# valid=")) valid = l.EndsWith("1");
                else if (l.StartsWith("t,"))
                {
                    var h = l.Split(',');
                    ix = Array.IndexOf(h, "x"); iy = Array.IndexOf(h, "y"); iz = Array.IndexOf(h, "z");
                    iv = Array.IndexOf(h, "speed_kmh"); ig = Array.IndexOf(h, "gas"); ib = Array.IndexOf(h, "brake");
                }
                else if (!l.StartsWith("#") && ix >= 0)
                {
                    var p = l.Split(',');
                    float F(int i) => float.Parse(p[i], ci);
                    samples.Add(new RecordedSample(new System.Numerics.Vector3(F(ix), F(iy), F(iz)), F(iv) / 3.6f, F(ig) / 255f, F(ib) / 255f));
                }
            }
            laps.Add(new RecordedLap { LapTime = lapTime, Valid = valid, Samples = samples });
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

    private static float Median(IEnumerable<float> values)
    {
        var a = values.OrderBy(x => x).ToArray();
        return a.Length == 0 ? 0 : a[a.Length / 2];
    }

    public static string Fmt(float seconds) => TimeSpan.FromSeconds(seconds).ToString(@"m\:ss\.fff");

    private sealed class SpeedTrace
    {
        private readonly RacingLine _line;
        private readonly TrackInfo _info;
        private readonly Dictionary<string, (float Min, float Max)> _sections = new();
        private float _top;

        public SpeedTrace(RacingLine line, TrackInfo info)
        {
            _line = line;
            _info = info;
        }

        public void Sample(RaceBot bot)
        {
            if (bot.LapsCompleted < 1 && !bot.TimingValid) return;
            float n = _line.WrapS((float)bot.Distance) / _line.Length;
            float kmh = bot.Speed * 3.6f;
            _top = MathF.Max(_top, kmh);
            var name = _info.SectionAt(n);
            if (name == null) return;
            _sections[name] = _sections.TryGetValue(name, out var mm) ? (MathF.Min(mm.Min, kmh), MathF.Max(mm.Max, kmh)) : (kmh, kmh);
        }

        public void Print()
        {
            Console.WriteLine($"Top speed {_top:F0} km/h");
            foreach (var s in _info.Sections)
                if (_sections.TryGetValue(s.Name, out var mm))
                    Console.WriteLine($"  {s.Name,-28} min {mm.Min,4:F0}  max {mm.Max,4:F0} km/h");
        }
    }

    private sealed class SimStats
    {
        private readonly RaceWorld _w;
        private int _overlapFrames, _offTrackFrames, _frames;
        private float _maxPenetration, _maxLatSpeed, _maxOffTrack;
        private readonly long[] _pedals = new long[6];
        private readonly Dictionary<int, int> _order = new();
        private int _positionChanges;
        private double _lastOrderCheck;
        private readonly Dictionary<int, float> _planApex = new();
        private readonly int[] _lineKinds = new int[4];
        private float _maxShift;
        // ---- behaviour report (--style): lines per personality, fights, start
        private readonly Dictionary<string, (double[] Sum, int[] N)> _lineByPers = new();
        private readonly Dictionary<int, double> _t100 = new();
        private readonly Dictionary<int, MistakeKind> _lastMistake = new();
        private readonly Dictionary<string, int> _grass = new(), _slides = new();
        private readonly Dictionary<string, (double Sum, int N)> _sideGap = new();
        private double _raceStart = double.NaN;
        private int _changesFirst30;

        public SimStats(RaceWorld w) => _w = w;

        private void SampleBehaviour(double now)
        {
            var line = _w.Line;
            int bins = (int)(line.Length / 10) + 1;
            if (double.IsNaN(_raceStart) && _w.Bots.Any(b => b.Phase == BotPhase.Racing)) _raceStart = now;
            foreach (var b in _w.Bots)
            {
                if (b.Phase != BotPhase.Racing || b.InPitLane) continue;
                string p = b.Driver.Personality.Name;
                if (!_t100.ContainsKey(b.Id) && b.Speed >= 100 / 3.6f && !double.IsNaN(_raceStart)) _t100[b.Id] = now - _raceStart;
                var m = b.Mistake;
                if (_lastMistake.TryGetValue(b.Id, out var lm) && lm != m)
                {
                    if (m == MistakeKind.Grass) _grass[p] = _grass.GetValueOrDefault(p) + 1;
                    if (m == MistakeKind.Slide) _slides[p] = _slides.GetValueOrDefault(p) + 1;
                }
                _lastMistake[b.Id] = m;
                if (b.LapsCompleted < 1) continue; // lap 1 is traffic
                // the line: only driving alone (nobody within 60 m), no mistake
                float sb = line.WrapS((float)b.Distance);
                bool alone = !_w.Bots.Any(o => o != b && o.OnTrack && MathF.Abs(line.Delta(sb, line.WrapS((float)o.Distance))) < 60);
                if (alone && b.Mistake == MistakeKind.None && b.OvertakeTargetId < 0)
                {
                    if (!_lineByPers.TryGetValue(p, out var acc)) _lineByPers[p] = acc = (new double[bins], new int[bins]);
                    int i = Math.Min(bins - 1, (int)(sb / 10));
                    acc.Sum[i] += b.Offset; acc.N[i]++;
                }
                // room left to a car alongside
                foreach (var o in _w.Bots)
                {
                    if (o == b || !o.OnTrack || o.InPitLane) continue;
                    float ds = MathF.Abs(line.Delta(sb, line.WrapS((float)o.Distance)));
                    if (ds > (b.Car.Length + o.Car.Length) / 2) continue;
                    float gap = MathF.Abs(b.Offset - o.Offset) - (b.Car.Width + o.Car.Width) / 2;
                    if (gap > 3) continue;
                    var g = _sideGap.GetValueOrDefault(p);
                    _sideGap[p] = (g.Sum + gap, g.N + 1);
                }
            }
        }

        public void PrintBehaviour(double simTime)
        {
            var line = _w.Line;
            Console.WriteLine("Behaviour:");
            double laps = _w.Bots.Average(b => b.LapsCompleted);
            foreach (var g in _w.Bots.GroupBy(b => b.Driver.Personality.Name).OrderBy(g => g.Key))
            {
                string p = g.Key;
                var gs = _sideGap.GetValueOrDefault(p);
                var t = g.Where(b => _t100.ContainsKey(b.Id)).Select(b => _t100[b.Id]).ToList();
                Console.WriteLine($"  {p,-11} bots {g.Count(),2}  attempts {g.Sum(b => b.OvertakeAttempts),4} overtakes {g.Sum(b => b.Overtakes),3} noroom-steps {g.Sum(b => b.OvertakeNoRoom),6} giveups {g.Sum(b => b.OvertakeGiveUps),3}" +
                                  $"  defends {g.Sum(b => b.Defends),3} letby {g.Sum(b => b.LetBy),2}  contacts {g.Sum(b => b.ContactCount),3}  grass {_grass.GetValueOrDefault(p),2} (greedy {g.Sum(b => b.GreedyExits)}) slides {_slides.GetValueOrDefault(p),2}" +
                                  $"  side gap {(gs.N > 0 ? gs.Sum / gs.N : double.NaN):F2} m  0-100 {(t.Count > 0 ? t.Average() : double.NaN):F2} s");
            }
            Console.WriteLine("  " + string.Join(", ", _w.DiagCounts.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}")));
            Console.WriteLine($"  no room for an attack: track too narrow {_w.DiagNoRoomEdge}, lane taken {_w.DiagNoRoomLane}, total {_w.DiagNoRoomAll}");
            foreach (var (id, t) in _t100.Where(x => x.Value > 12).OrderByDescending(x => x.Value))
                if (_w.Bots.FirstOrDefault(b => b.Id == id) is { } sb)
                    Console.WriteLine($"  slow start: #{id} {sb.Car.Model} 0-100 {t:F1} s launch {sb.Launch}");
            var t100 = _t100.Values.ToList();
            if (t100.Count > 0) Console.WriteLine($"  start: 0-100 km/h {t100.Min():F2} .. {t100.Max():F2} s (avg {t100.Average():F2}), launches: " +
                string.Join(", ", _w.Bots.GroupBy(b => b.Launch).Select(g => $"{g.Key} {g.Count()}")));
            // line differences between personalities in corners (bins where both have data)
            var names = _lineByPers.Keys.OrderBy(x => x).ToList();
            for (int a = 0; a < names.Count; a++)
                for (int b = a + 1; b < names.Count; b++)
                {
                    var A = _lineByPers[names[a]]; var B = _lineByPers[names[b]];
                    double sum = 0, max = 0; int n = 0;
                    for (int i = 0; i < A.N.Length; i++)
                    {
                        if (A.N[i] < 3 || B.N[i] < 3) continue;
                        if (MathF.Abs(line.CurvatureAt(i * 10 + 5)) < 1 / 300f) continue;
                        double d = Math.Abs(A.Sum[i] / A.N[i] - B.Sum[i] / B.N[i]);
                        sum += d; max = Math.Max(max, d); n++;
                    }
                    if (n > 0) Console.WriteLine($"  line {names[a]} vs {names[b]}: corners avg {sum / n:F2} m apart, max {max:F2} m ({n} samples)");
                }
        }

        public bool Behaviour;

        public void Sample(double now)
        {
            _frames++;
            if (Behaviour && _frames % 3 == 0) SampleBehaviour(now);
            var line = _w.Line;
            var bots = _w.Bots.Where(b => b.OnTrack).ToList();
            for (int i = 0; i < bots.Count; i++)
            {
                var a = bots[i];
                float sa = line.WrapS((float)a.Distance);
                int idx = line.IndexAt(sa);
                if (a.InPitLane) continue;
                if (a.Phase == BotPhase.Racing && a.PlanActive && (!_planApex.TryGetValue(a.Id, out var apex) || apex != a.PlanApexS))
                {
                    _planApex[a.Id] = a.PlanApexS;
                    _lineKinds[(int)a.PlanKind]++;
                }
                _maxShift = MathF.Max(_maxShift, a.PlanActive ? a.PlanAmp : 0);
                if (a.Phase == BotPhase.Racing)
                {
                    int bucket = a.Brake < 0.04f ? (a.Throttle > 0.95f ? 0 : a.Throttle > 0.05f ? 1 : 2) : a.Brake < 0.35f ? 3 : a.Brake < 0.8f ? 4 : 5;
                    _pedals[bucket]++;
                }
                float excess = MathF.Max(-line.RoomMinus[idx] + a.Car.Width / 2 - a.Offset, a.Offset - (line.RoomPlus[idx] - a.Car.Width / 2));
                if (excess > 0.05f) _offTrackFrames++;
                _maxOffTrack = MathF.Max(_maxOffTrack, excess);
                _maxLatSpeed = MathF.Max(_maxLatSpeed, MathF.Abs(a.LateralSpeed));

                for (int j = i + 1; j < bots.Count; j++)
                {
                    var b = bots[j];
                    if (a.Phase == BotPhase.Grid && b.Phase == BotPhase.Grid) continue;
                    float ds = MathF.Abs(line.Delta(sa, line.WrapS((float)b.Distance)));
                    float dOff = MathF.Abs(a.Offset - b.Offset);
                    float longPen = (a.Car.Length + b.Car.Length) / 2 - ds;
                    float latPen = (a.Car.Width + b.Car.Width) / 2 - dOff;
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
                var order = _w.Bots.OrderByDescending(b => b.Distance).Select((b, i) => (b.Id, i)).ToList();
                foreach (var (id, pos) in order)
                {
                    if (_order.TryGetValue(id, out var old) && old != pos) _positionChanges++;
                    _order[id] = pos;
                }
            }
        }

        public void Print(int laps)
        {
            Console.WriteLine($"{"Pos",-4}{"Driver",-22}{"Lvl",5}{"Laps",5}{"Best",11}{"Last",11}{"Total",11}{"Ovt",5}{"Pit",4}");
            int pos = 1;
            foreach (var b in _w.Bots.OrderByDescending(b => b.LapsCompleted).ThenBy(b => b.TotalTime))
            {
                Console.WriteLine($"{pos++,-4}{b.Name,-22}{b.Driver.Level,5:F0}{b.LapsCompleted,5}{(b.BestLapSeconds < 1e6 ? Fmt(b.BestLapSeconds) : "-"),11}" +
                                  $"{(b.LastLapSeconds > 0 ? Fmt(b.LastLapSeconds) : "-"),11}{(b.TotalTime > 0 ? Fmt((float)b.TotalTime) : "-"),11}{b.Overtakes,5}{b.PitStops,4} {b.Driver.Personality.Name,-10} err={b.Driver.Errors:F2} pace={b.Driver.Pace:F3} mis={b.MistakeCount} spin={b.SpinCount} cont={b.ContactCount} dmg={RaceWorld.BodyDamagePercent(b):F0}%/{b.Suspension * 100:F0}% fuel={b.Fuel,5:F1} grip={b.CarGrip:F3} att={b.OvertakeAttempts} noroom={b.OvertakeNoRoom} giveup={b.OvertakeGiveUps} d={b.Distance,8:F0} v={b.Speed * 3.6f,4:F0} off={b.Offset,5:F1} tgtOff={b.TargetOffset,5:F1} tgtV={b.TargetSpeed * 3.6f,4:F0} {b.Phase}");
            }
            long tot = Math.Max(1, _pedals.Sum());
            Console.WriteLine($"pedals: full throttle {_pedals[0] * 100 / tot} %, part throttle {_pedals[1] * 100 / tot} %, coasting {_pedals[2] * 100 / tot} %, " +
                              $"light brake {_pedals[3] * 100 / tot} %, medium {_pedals[4] * 100 / tot} %, hard {_pedals[5] * 100 / tot} %");
            Console.WriteLine($"overlap frames {_overlapFrames} (max penetration {_maxPenetration:F2} m), off-track frames {_offTrackFrames} (max {_maxOffTrack:F2} m), " +
                              $"position changes {_positionChanges / 2}, max lateral speed {_maxLatSpeed:F1} m/s, frames {_frames}");
            Console.WriteLine($"flashes: {_w.Bots.Sum(b => b.FlashCount)} ({string.Join(", ", _w.Bots.Where(b => b.FlashCount > 0).Select(b => $"{b.Driver.Personality.Name} {b.FlashCount}"))})");
            Console.WriteLine($"tyre temps: {string.Join(" ", _w.Bots.Select(b => $"{b.TyreTempFront:F0}/{b.TyreTempRear:F0}"))}");
            Console.WriteLine($"corner lines: clean {_lineKinds[0]}, wide {_lineKinds[1]}, early apex {_lineKinds[2]}, late apex {_lineKinds[3]}, largest line error {_maxShift:F2} m");
        }
    }
}
