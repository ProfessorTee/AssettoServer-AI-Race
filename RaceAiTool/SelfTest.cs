using TrackGeometry;
using System.Numerics;
using RaceAiPlugin.Core;

namespace RaceAiTool;

/// <summary>Offline checks of the racing AI core that need no game files (synthetic track).</summary>
public static class SelfTest
{
    private static int _failed;

    public static int Run(Options o)
    {
        Check("ACD key ks_mercedes_amg_gt3", AcdReader.CreateKey("ks_mercedes_amg_gt3") == "134-108-37-104-150-205-64-52");
        Check("ACD key bmw_z4_gt3", AcdReader.CreateKey("bmw_z4_gt3") == "192-129-197-37-144-179-73-52");
        Check("ACD key abarth500", AcdReader.CreateKey("abarth500") == "7-248-6-221-246-250-21-49");

        var points = SyntheticTrack();
        var ms = new MemoryStream();
        FastLaneFile.Write(ms, points);
        ms.Position = 0;
        var read = FastLaneFile.Read(ms);
        Check("fast_lane.ai roundtrip", read.Length == points.Length && Vector3.Distance(read[123].Position, points[123].Position) < 1e-4f
                                        && MathF.Abs(read[50].SideLeft - points[50].SideLeft) < 1e-5f);

        var line = new RacingLine(read);
        Check($"line length {line.Length:F0} m", MathF.Abs(line.Length - ExpectedLength) < 5);
        // the synthetic line hugs the left edge (SideLeft small) in the left-hand corners
        int apex = line.IndexAt(600 + MathF.PI * 80 / 2);
        Check($"corner curvature {1 / MathF.Abs(line.Curvature[apex]):F0} m (80 expected)", MathF.Abs(1 / MathF.Abs(line.Curvature[apex]) - 80) < 8);
        Check("inside of the corner has less room", MathF.Min(line.RoomPlus[apex], line.RoomMinus[apex]) < 2.5f);

        // projection
        var p = line.PositionAt(300, 1.5f);
        var proj = line.Project(p);
        Check("projection", MathF.Abs(proj.S - 300) < 0.5f && MathF.Abs(proj.Offset - 1.5f) < 0.1f);

        // race
        var world = new RaceWorld(line, new RaceWorldSettings { Seed = 7 });
        var rng = new Random(3);
        for (int i = 0; i < 10; i++)
        {
            world.Bots.Add(new RaceBot
            {
                Id = i,
                Name = $"Bot {i}",
                Car = new CarSpec(),
                // slowest bots in front so the faster ones have to overtake
                Driver = DriverProfile.FromLevel(82 + i * 2f, 60)
            });
        }
        world.PlaceOnGrid(world.Bots);
        world.TraceBotId = o.Int("trace", -1);
        world.Trace = Console.WriteLine;
        world.StartRace(0);
        int laps = 0;
        world.LapCompleted += (_, _) => laps++;

        float maxPen = 0;
        int overlapFrames = 0;
        double t = 0;
        world.Advance(0);
        while (t < 900 && world.Bots.Any(b => b.LapsCompleted < 5))
        {
            t += 0.05;
            world.Advance(t);
            foreach (var a in world.Bots)
            foreach (var b in world.Bots)
            {
                if (a.Id >= b.Id) continue;
                float ds = MathF.Abs(line.Delta(line.WrapS((float)a.Distance), line.WrapS((float)b.Distance)));
                float lp = (a.Car.Length + b.Car.Length) / 2 - ds;
                float wp = (a.Car.Width + b.Car.Width) / 2 - MathF.Abs(a.Offset - b.Offset);
                if (lp > 0.05f && wp > 0.05f)
                {
                    overlapFrames++;
                    maxPen = MathF.Max(maxPen, MathF.Min(lp, wp));
                }
            }
        }

        if (o.Has("verbose"))
            foreach (var b in world.Bots.OrderByDescending(b => b.Distance))
                Console.WriteLine($"  {b.Name} lvl {b.Driver.Level:F0} best {b.BestLapSeconds:F2} ovt {b.Overtakes} att {b.OvertakeAttempts} noroom {b.OvertakeNoRoom} giveup {b.OvertakeGiveUps}");
        Check($"all bots finished 5 laps ({laps} laps in {t:F0} s)", world.Bots.All(b => b.LapsCompleted >= 5));
        int overtakes = world.Bots.Sum(b => b.Overtakes);
        Check($"overtakes happen ({overtakes})", overtakes > 0);
        Check($"no deep overlaps (max {maxPen:F2} m, {overlapFrames} frames)", maxPen < 0.5f);
        var fastest = world.Bots.OrderBy(b => b.BestLapSeconds).First();
        Check($"best lap by a strong driver ({fastest.Name}, level {fastest.Driver.Level:F0}, {fastest.BestLapSeconds:F1} s)", fastest.Driver.Level >= 89);

        // pose sanity
        var pose = world.GetPose(world.Bots[0]);
        Check("pose finite", !float.IsNaN(pose.Position.X) && !float.IsNaN(pose.Rotation.X) && pose.Gear >= 1);

        // strength distribution: 80 +/- 10 over 5 bots -> 70, 75, 80, 85, 90 (shuffled)
        var dist = StrengthCalibration.Distribute(5, 80, 10, false, new Random(1)).OrderBy(x => x).ToArray();
        Check($"strength spread [{string.Join(", ", dist)}]", dist.SequenceEqual(new[] { 70f, 75f, 80f, 85f, 90f }));
        var calib = StrengthCalibration.Measure(line, new CarSpec(), new RaceWorldSettings());
        float p95 = calib.PaceFor(95), p90 = calib.PaceFor(90);
        Check($"calibration 100 % = {calib.BestLap:F1} s, 95 % -> pace {p95:F3}, 90 % -> pace {p90:F3}", p95 < 1 && p90 < p95 && p90 > 0.5f);
        float lap95 = StrengthCalibration.FlyingLap(line, new CarSpec(), p95, new RaceWorldSettings());
        Check($"95 % drives {lap95:F1} s (target {calib.LapTimeFor(95):F1} s)", MathF.Abs(lap95 - calib.LapTimeFor(95)) < 1.0f);

        // pit stops: pit lane parallel to the first straight, heavy fuel use
        FlagTest(line);
        HumanTest(line);
        StuckTest(line);
        PitTest(line);
        CloneTest(line);

        Console.WriteLine(_failed == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({_failed})");
        return _failed == 0 ? 0 : 3;
    }

    private static void FlagTest(RacingLine line)
    {
        // blue flag: a much faster car a lap ahead comes up behind a slow bot
        var world = new RaceWorld(line, new RaceWorldSettings { Seed = 3, BlueFlags = true, YellowFlags = true, RaceStartTime = 0 });
        var slow = new RaceBot { Id = 0, Name = "Slow", Car = new CarSpec(), Driver = DriverProfile.FromLevel(60, 50) };
        var fast = new RaceBot { Id = 1, Name = "Fast", Car = new CarSpec(), Driver = DriverProfile.FromLevel(100, 50) };
        world.Bots.Add(slow);
        world.Bots.Add(fast);
        world.PlaceOnGrid(world.Bots);
        world.StartRace(0);
        fast.LapsCompleted = 1;
        slow.Distance += 120;
        slow.Speed = fast.Speed = 30;
        double t = 0;
        world.Advance(0);
        bool indicated = false, passed = false;
        while (t < 200 && !passed)
        {
            t += 0.05;
            world.Advance(t);
            indicated |= slow.Indicator != 0;
            passed = fast.Distance - slow.Distance > 30 && fast.Distance - slow.Distance < line.Length / 2;
        }
        Check($"blue flag: lapped bot indicates and lets the leader through ({t:F0} s)", indicated && passed);

        // yellow flag: a stopped car on the track
        var w2 = new RaceWorld(line, new RaceWorldSettings { Seed = 4, YellowFlags = true, RaceStartTime = -100 });
        var bot = new RaceBot { Id = 0, Name = "Y", Car = new CarSpec(), Driver = DriverProfile.FromLevel(95, 50) };
        w2.Bots.Add(bot);
        w2.PlaceOnGrid(w2.Bots);
        w2.StartRace(0);
        var ext = w2.GetOrAddExternal(99);
        var wreck = line.PositionAt(line.WrapS((float)bot.Distance + 600), line.RoomPlus[line.IndexAt(line.WrapS((float)bot.Distance + 600))] - 1.5f);
        bool hazards = false, crash = false;
        t = 0;
        while (t < 40)
        {
            t += 0.05;
            w2.UpdateExternal(ext, wreck, Vector3.Zero);
            w2.Advance(t);
            var pose = w2.GetPose(bot);
            hazards |= pose.Hazards;
            crash |= Vector3.Distance(pose.Position, wreck) < 1.5f;
        }
        Check($"yellow flag: hazards on near the stopped car, no crash", hazards && !crash);
    }

    /// <summary>Records a "player" (a bot with its own pace and a line 1.5 m to the side), builds a clone and races it.</summary>
    private static void CloneTest(RacingLine line)
    {
        var world = new RaceWorld(line, new RaceWorldSettings { Seed = 21, HumanErrors = false, RaceStartTime = -100 });
        var player = new RaceBot { Id = 0, Name = "Player", Car = new CarSpec(), Driver = new DriverProfile { Pace = 0.8f, Aggression = 0, Consistency = 1 } };
        world.Bots.Add(player);
        world.PlaceAt(player, 10, 0, BotPhase.Racing);
        player.Speed = 30;
        var laps = new List<RecordedLap>();
        var cur = new List<RecordedSample>();
        double lapStart = 0;
        int lastLaps = 0;
        double t = 0;
        world.Advance(0);
        while (laps.Count < 3 && t < 600)
        {
            t += 0.05;
            world.Advance(t);
            var pose = world.GetPose(player);
            // the "player" drives 1.5 m right of the racing line (shifted when recording)
            var lat = line.LateralAt(line.WrapS((float)player.Distance));
            cur.Add(new RecordedSample(pose.Position + lat * 1.5f, player.Speed, player.Throttle, player.Brake));
            if (player.LapsCompleted != lastLaps)
            {
                if (lastLaps > 0) laps.Add(new RecordedLap { LapTime = (float)(t - lapStart), Valid = true, Samples = cur });
                cur = new List<RecordedSample>();
                lastLaps = player.LapsCompleted;
                lapStart = t;
            }
        }
        var profile = CloneProfile.Build(line, laps, "1", "Player", "generic");
        Check($"clone profile from {laps.Count} laps", profile != null && profile.LapsUsed == laps.Count);
        if (profile == null) return;
        float avgOffset = profile.Offset.Average();
        Check($"clone profile: line 1.5 m to the side (average offset {avgOffset:F2} m)", MathF.Abs(MathF.Abs(avgOffset) - 1.5f) < 0.4f);

        var w2 = new RaceWorld(line, new RaceWorldSettings { Seed = 22, HumanErrors = false, RaceStartTime = -100 });
        var clone = new RaceBot { Id = 1, Name = "Clone", Car = new CarSpec(), Driver = new DriverProfile { Pace = 1, Aggression = 0.3f, Consistency = 1 }, Clone = profile };
        w2.Bots.Add(clone);
        w2.PlaceAt(clone, 10, 0, BotPhase.Racing);
        clone.Speed = 30;
        t = 0;
        float offSum = 0; int offN = 0;
        w2.Advance(0);
        while (clone.LapsCompleted < 3 && t < 600)
        {
            t += 0.05;
            w2.Advance(t);
            if (clone.LapsCompleted >= 1) { offSum += clone.Offset; offN++; }
        }
        float playerLap = laps.Average(l => l.LapTime);
        Check($"clone laps like the player ({clone.LastLapSeconds:F1} s vs {playerLap:F1} s)", MathF.Abs(clone.LastLapSeconds - playerLap) < playerLap * 0.04f);
        Check($"clone drives the player's line (average offset {offSum / Math.Max(1, offN):F2} m)", MathF.Abs(offSum / Math.Max(1, offN) - avgOffset) < 0.6f);
    }

    private static void HumanTest(RacingLine line)
    {
        var world = new RaceWorld(line, new RaceWorldSettings { Seed = 9, RaceStartTime = -100, Damage = true, DamageRate = 1 });
        for (int i = 0; i < 6; i++)
        {
            var d = DriverProfile.FromStrength(76, 0.6f, 80);
            d.Errors = DriverProfile.ErrorsFor(76);
            world.Bots.Add(new RaceBot { Id = i, Name = $"H{i}", Car = new CarSpec(), Driver = d });
        }
        world.PlaceOnGrid(world.Bots);
        world.StartRace(0);
        double t = 0;
        world.Advance(0);
        bool finite = true, sawYaw = false, sawLock = false;
        float maxBeyond = 0;
        while (t < 400)
        {
            t += 0.05;
            world.Advance(t);
            foreach (var b in world.Bots)
            {
                var p = world.GetPose(b);
                finite &= float.IsFinite(p.Position.X) && float.IsFinite(p.Rotation.X) && float.IsFinite(p.Velocity.X);
                sawYaw |= MathF.Abs(b.Yaw) > 0.1f;
                sawLock |= p.FrontTyreFactor == 0;
                int i = line.IndexAt(line.WrapS((float)b.Distance));
                float half = b.Car.Width / 2;
                maxBeyond = MathF.Max(maxBeyond, MathF.Max(b.Offset - (line.RoomPlus[i] - half), -line.RoomMinus[i] + half - b.Offset));
            }
        }
        int mistakes = world.Bots.Sum(b => b.MistakeCount);
        Check($"human errors: {mistakes} mistakes, {world.Bots.Sum(b => b.SpinCount)} spins, {world.Bots.Sum(b => b.ContactCount)} contacts, slides {sawYaw}, lock-ups {sawLock}",
            mistakes > 5 && sawYaw && finite);
        Check($"grass: at most {maxBeyond:F2} m beyond the edge", maxBeyond <= world.Settings.GrassAllowance + 0.55f);
        Check("everybody keeps racing", world.Bots.All(b => b.LapsCompleted >= 5));

        // damage and repair
        var bot = world.Bots[0];
        float grip = bot.CarGrip;
        world.AddDamage(bot, 0, 60);
        Check($"damage slows the car (grip {grip:F3} -> {bot.CarGrip:F3}, repair {RaceWorld.RepairTime(bot):F0} s)", bot.CarGrip < grip && RaceWorld.RepairTime(bot) > 5);
        world.RepairDamage(bot);
        Check("repair", !RaceWorld.HasDamage(bot));

        // signal test phases
        world.StartSignalTest(1000);
        var phases = new[] { 1001.0, 1008, 1014, 1021, 1027, 1035, 1040 }.Select(x => world.SignalTestPhase(x)).ToArray();
        Check($"signal test phases [{string.Join(",", phases)}]", phases.SequenceEqual(new[] { 1, 2, 3, 4, 5, 6, 0 }));
        world.Advance(1001);
        var pose = world.GetPose(world.Bots[1]);
        Check("signal test: left indicator", pose.Indicator == -1);

        // high beams: only with nobody ahead
        var hb = new RaceWorld(line, new RaceWorldSettings { Seed = 2, HumanErrors = false });
        var lead = new RaceBot { Id = 0, Name = "Lead", Car = new CarSpec(), Driver = DriverProfile.FromLevel(95, 0) };
        var follow = new RaceBot { Id = 1, Name = "Follow", Car = new CarSpec(), Driver = DriverProfile.FromLevel(95, 0) };
        hb.Bots.Add(lead);
        hb.Bots.Add(follow);
        hb.PlaceOnGrid(hb.Bots);
        hb.StartRace(0);
        hb.Advance(0);
        for (double x = 0.05; x < 8; x += 0.05) hb.Advance(x);
        // the start is random now (reaction, wheelspin): whoever is in front
        var first = lead.Distance >= follow.Distance ? lead : follow;
        var second = first == lead ? follow : lead;
        Check($"high beams: leader on ({hb.GetPose(first).HighBeam}), car right behind dipped ({hb.GetPose(second).HighBeam})",
            hb.GetPose(first).HighBeam && !hb.GetPose(second).HighBeam);
    }

    private static void StuckTest(RacingLine line)
    {
        // a jam: three bots side by side and one behind, all standing, a stopped player car right in front of them
        var world = new RaceWorld(line, new RaceWorldSettings { Seed = 8, HumanErrors = false, RaceStartTime = -100 });
        var start = new List<RaceBot>();
        for (int i = 0; i < 4; i++)
        {
            var b = new RaceBot { Id = i, Name = $"J{i}", Car = new CarSpec(), Driver = DriverProfile.FromLevel(95, 60) };
            world.Bots.Add(b);
        }
        world.PlaceAt(world.Bots[0], 300, -2.3f, BotPhase.Racing);
        world.PlaceAt(world.Bots[1], 300, 0f, BotPhase.Racing);
        world.PlaceAt(world.Bots[2], 300.5, 2.3f, BotPhase.Racing);
        world.PlaceAt(world.Bots[3], 294, 0f, BotPhase.Racing);
        foreach (var b in world.Bots) { b.Speed = 0; b.TimingValid = true; }
        var wreck = world.GetOrAddExternal(99);
        var wreckPos = line.PositionAt(306, 0.3f);
        var yellows = new List<YellowFlagEvent>();
        world.YellowFlag += e => yellows.Add(e);
        double t = 0;
        world.Advance(0);
        while (t < 90)
        {
            t += 0.05;
            world.UpdateExternal(wreck, wreckPos, System.Numerics.Vector3.Zero);
            world.Advance(t);
        }
        float minMoved = world.Bots.Min(b => (float)b.Distance - 300);
        Check($"stuck cluster sorted out: every bot got going (min {minMoved:F0} m, ghosts {world.Bots.Sum(b => b.GhostCount)})", minMoved > 150);
        Check($"yellow flag for the stopped car ({yellows.Count} messages)", yellows.Any(y => y.CarId == 99));

        // road completely blocked by standing player cars: after GhostAfter the bot ghosts through
        var w2 = new RaceWorld(line, new RaceWorldSettings { Seed = 9, HumanErrors = false, RaceStartTime = -100 });
        var g = new RaceBot { Id = 0, Name = "G", Car = new CarSpec(), Driver = DriverProfile.FromLevel(95, 60) };
        w2.Bots.Add(g);
        w2.PlaceAt(g, 300, 0, BotPhase.Racing);
        g.TimingValid = true;
        int idx = line.IndexAt(310);
        var blockers = new List<(ExternalCar Car, System.Numerics.Vector3 Pos)>();
        for (float off = -line.RoomMinus[idx]; off <= line.RoomPlus[idx]; off += 1.9f)
            blockers.Add((w2.GetOrAddExternal(100 + blockers.Count), line.PositionAt(310, off)));
        t = 0;
        w2.Advance(0);
        double ghostAt = -1;
        while (t < 80)
        {
            t += 0.05;
            foreach (var (car, pos) in blockers) w2.UpdateExternal(car, pos, System.Numerics.Vector3.Zero);
            w2.Advance(t);
            if (ghostAt < 0 && g.GhostUntil > t) ghostAt = t;
        }
        Check($"road blocked: ghosted after {ghostAt:F0} s and got through ({g.Distance - 300:F0} m)", ghostAt > 20 && g.Distance > 400);
    }

    private static void PitTest(RacingLine line)
    {
        var lanePts = new List<FastLanePoint>();
        for (float x = -150; x <= 750; x += 1.5f)
        {
            // joins the line before and after, 9 m to the side along the straight
            float side = x < 50 ? MathF.Max(0, (x + 150) / 200) : x > 550 ? MathF.Max(0, (750 - x) / 200) : 1;
            float z = 9 * side;
            lanePts.Add(new FastLanePoint { Position = new Vector3(x < 0 ? 0 : x, 0, z), Normal = Vector3.UnitY });
        }
        // before x=0 the lane runs on the last metres of the oval: use the line itself there
        for (int i = 0; i < lanePts.Count; i++)
        {
            var p = lanePts[i];
            if (p.Position.X <= 0)
            {
                float back = -(-150 + i * 1.5f);
                var q = line.PositionAt(line.Length - back, 0);
                lanePts[i] = new FastLanePoint { Position = q + new Vector3(0, 0, 9 * MathF.Max(0, (150 - back) / 200)), Normal = Vector3.UnitY };
            }
        }
        var lane = new PitLane(lanePts.ToArray(), line);
        var world = new RaceWorld(line, new RaceWorldSettings { Seed = 5, FuelRate = 6, TyreWearRate = 1 }) { PitLane = lane };
        int stops = 0;
        world.PitStopCompleted += (_, _, _, _) => stops++;
        for (int i = 0; i < 4; i++)
        {
            var bot = new RaceBot { Id = i, Name = $"P{i}", Car = new CarSpec { FuelCapacity = 60, KmPerLiter = 1.6f }, Driver = DriverProfile.FromLevel(95, 50) };
            world.Bots.Add(bot);
            world.SetPitBox(bot, new Vector3(250 + i * 10, 0, 13));
        }
        world.PlaceOnGrid(world.Bots);
        foreach (var b in world.Bots) world.ResetCarCondition(b, world.FuelForLaps(b, 3));
        world.StartRace(0);
        double t = 0;
        world.Advance(0);
        bool dry = false;
        while (t < 1500 && world.Bots.Any(b => b.LapsCompleted < 12))
        {
            t += 0.05;
            foreach (var b in world.Bots) b.RemainingLaps = Math.Max(0, 12 - b.LapsCompleted);
            world.Advance(t);
            dry |= world.Bots.Any(b => b.Fuel <= 0 && b.LapsCompleted < 12);
        }
        Check($"pit lane {lane.Length:F0} m, limiter {lane.LimiterStart:F0}-{lane.LimiterEnd:F0}", lane.LimiterEnd > lane.LimiterStart);
        Check($"pit stops made ({stops}), all finished", stops >= 4 && world.Bots.All(b => b.LapsCompleted >= 12));
        Check("nobody ran out of fuel", !dry);

        // rain: less grip, more careful
        var rain = new RaceWorld(line, new RaceWorldSettings { Seed = 6, Wetness = 0.8f, Water = 0.3f, RainIntensity = 0.5f });
        var rb = new RaceBot { Id = 0, Name = "R", Car = new CarSpec(), Driver = DriverProfile.FromLevel(95, 50) };
        rain.Bots.Add(rb);
        Check($"rain: wet grip {rain.RainGrip(rb):F2}", rain.RainGrip(rb) < 0.9f);
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}");
        if (!ok) _failed++;
    }

    private const float Straight = 600, Radius = 80;
    private static float ExpectedLength => 2 * Straight + 2 * MathF.PI * Radius;

    /// <summary>Oval: two 600 m straights, two left-hand 180° corners with R=80 m, 12 m wide. The line hugs the inside in the corners.</summary>
    private static FastLanePoint[] SyntheticTrack()
    {
        var pts = new List<FastLanePoint>();
        void Add(Vector3 pos, Vector3 fwd, float insideRoom)
        {
            // driving direction with Y up; "left" of the car = cross(up, fwd)
            pts.Add(new FastLanePoint
            {
                Position = pos, Forward = fwd, Normal = Vector3.UnitY,
                SideLeft = insideRoom, SideRight = 12 - insideRoom, Speed = 30
            });
        }

        for (float s = 0; s < Straight; s += 1.5f)
            Add(new Vector3(s, 0, 0), Vector3.UnitX, 6);
        for (float a = 0; a < MathF.PI; a += 1.5f / Radius)
        {
            var c = new Vector3(Straight, 0, -Radius);
            var pos = c + new Vector3(MathF.Sin(a), 0, MathF.Cos(a)) * Radius;
            Add(pos, new Vector3(MathF.Cos(a), 0, -MathF.Sin(a)), 1.2f + 4.8f * MathF.Abs(a / MathF.PI - 0.5f) * 2);
        }
        for (float s = 0; s < Straight; s += 1.5f)
            Add(new Vector3(Straight - s, 0, -2 * Radius), -Vector3.UnitX, 6);
        for (float a = 0; a < MathF.PI; a += 1.5f / Radius)
        {
            var c = new Vector3(0, 0, -Radius);
            var pos = c + new Vector3(-MathF.Sin(a), 0, -MathF.Cos(a)) * Radius;
            Add(pos, new Vector3(-MathF.Cos(a), 0, MathF.Sin(a)), 1.2f + 4.8f * MathF.Abs(a / MathF.PI - 0.5f) * 2);
        }
        return pts.ToArray();
    }
}
