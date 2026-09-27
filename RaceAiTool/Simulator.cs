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
            VirtualWetTyres = o.Has("virtual-wets"),
            Spins = !o.Has("no-spins"),
            GrassMoments = !o.Has("no-grass"),
            BotContacts = !o.Has("no-contacts"),
            Damage = !o.Has("no-damage"),
        };
        float errorsBelow = o.Float("errors-below", 87), errorsFull = o.Float("errors-full", 75);
        if (info.StartFinish is { } sf) settings.StartLineS = line.Project(sf).S;
        var world = new RaceWorld(line, settings);
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
            bot.Driver.Errors = settings.HumanErrors ? DriverProfile.ErrorsFor(strengths[i], errorsBelow, errorsFull) : 0;
            bot.Driver.Pace = cal.PaceFor(strengths[i], null, bot.Driver.Errors);
            world.Bots.Add(bot);
            if (info.PitBoxes.FirstOrDefault(p => p.Index == i) is { } box && info.PitBoxes.Count > i)
                world.SetPitBox(bot, box.Position);
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
            playerWorld.Bots.Add(new RaceBot { Id = 1000, Name = "Player", Car = new CarSpec(), Driver = new DriverProfile { Pace = playerPace, Aggression = 0, Consistency = 1 } });
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
            foreach (var bot in world.Bots)
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
        var trace = hotlap ? new SpeedTrace(line, info) : null;
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
        while (now < maxTime)
        {
            now += dt;
            if (playerWorld != null && player != null)
            {
                playerWorld.Advance(now);
                var pb = playerWorld.Bots[0];
                var pose = playerWorld.GetPose(pb);
                world.UpdateExternal(player, pose.Position, pose.Velocity);
            }
            foreach (var b in world.Bots) b.RemainingLaps = hotlap ? int.MaxValue : Math.Max(0, laps - b.LapsCompleted);
            world.Advance(now);
            stats.Sample(now);
            trace?.Sample(world.Bots[0]);

            if (hotlap && world.Bots[0].LapsCompleted >= Math.Max(1, laps)) break;
            if (!hotlap && world.Bots.All(b => b.LapsCompleted >= laps)) break;
        }

        Console.WriteLine();
        Console.WriteLine($"Simulated {now:F0} s in {sw.Elapsed.TotalSeconds:F1} s real time");
        if (trace != null) trace.Print();
        stats.Print(laps);
        return 0;
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

        public SimStats(RaceWorld w) => _w = w;

        public void Sample(double now)
        {
            _frames++;
            var line = _w.Line;
            var bots = _w.Bots.Where(b => b.OnTrack).ToList();
            for (int i = 0; i < bots.Count; i++)
            {
                var a = bots[i];
                float sa = line.WrapS((float)a.Distance);
                int idx = line.IndexAt(sa);
                if (a.InPitLane) continue;
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
                                  $"{(b.LastLapSeconds > 0 ? Fmt(b.LastLapSeconds) : "-"),11}{(b.TotalTime > 0 ? Fmt((float)b.TotalTime) : "-"),11}{b.Overtakes,5}{b.PitStops,4} err={b.Driver.Errors:F2} {(b.OnWets ? "WET" : "slk")} pace={b.Driver.Pace:F3} mis={b.MistakeCount} spin={b.SpinCount} cont={b.ContactCount} dmg={RaceWorld.BodyDamagePercent(b):F0}%/{b.Suspension * 100:F0}% fuel={b.Fuel,5:F1} grip={b.CarGrip:F3} att={b.OvertakeAttempts} noroom={b.OvertakeNoRoom} giveup={b.OvertakeGiveUps} d={b.Distance,8:F0} v={b.Speed * 3.6f,4:F0} off={b.Offset,5:F1} tgtOff={b.TargetOffset,5:F1} tgtV={b.TargetSpeed * 3.6f,4:F0} {b.Phase}");
            }
            long tot = Math.Max(1, _pedals.Sum());
            Console.WriteLine($"pedals: full throttle {_pedals[0] * 100 / tot} %, part throttle {_pedals[1] * 100 / tot} %, coasting {_pedals[2] * 100 / tot} %, " +
                              $"light brake {_pedals[3] * 100 / tot} %, medium {_pedals[4] * 100 / tot} %, hard {_pedals[5] * 100 / tot} %");
            Console.WriteLine($"overlap frames {_overlapFrames} (max penetration {_maxPenetration:F2} m), off-track frames {_offTrackFrames} (max {_maxOffTrack:F2} m), " +
                              $"position changes {_positionChanges / 2}, max lateral speed {_maxLatSpeed:F1} m/s, frames {_frames}");
        }
    }
}
