using TrackGeometry;
using System.Numerics;
using BotDriverPlugin.Core;
using BotDriverPlugin.Core.Physics;

namespace BotDriverTool;

/// <summary>Offline checks of the bots that need no game files (synthetic track, synthetic car).</summary>
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
        int apex = line.IndexAt(600 + MathF.PI * 80 / 2);
        Check($"corner curvature {1 / MathF.Abs(line.Curvature[apex]):F0} m (80 expected)", MathF.Abs(1 / MathF.Abs(line.Curvature[apex]) - 80) < 8);
        var p = line.PositionAt(300, 1.5f);
        var proj = line.Project(p);
        Check("projection", MathF.Abs(proj.S - 300) < 0.5f && MathF.Abs(proj.Offset - 1.5f) < 0.1f);

        VehicleModelTest();

        // strength distribution: 80 +/- 10 over 5 bots -> 70, 75, 80, 85, 90 (shuffled)
        var dist = StrengthCalibration.Distribute(5, 80, 10, false, new Random(1)).OrderBy(x => x).ToArray();
        Check($"strength spread [{string.Join(", ", dist)}]", dist.SequenceEqual(new[] { 70f, 75f, 80f, 85f, 90f }));
        var car = TestCar();
        var calib = StrengthCalibration.Measure(line, car, new RaceWorldSettings());
        float l95 = StrengthCalibration.PaceFor(95), l90 = StrengthCalibration.PaceFor(90);
        Check($"calibration level 1 = {calib.BestLap:F1} s, 95 % -> level {l95:F3}, 90 % -> level {l90:F3}", l95 < 1 && l90 < l95 && l90 > 0.3f);
        float lap95 = StrengthCalibration.FlyingLap(line, car, l95, new RaceWorldSettings());
        Check($"95 % drives {lap95:F1} s (target {calib.LapTimeFor(95):F1} s)", MathF.Abs(lap95 - calib.LapTimeFor(95)) < 1.0f);

        RaceTest(line, calib, o);
        PassTest(line, calib);
        PitTest(line, calib);
        TyreTest();
        DamageTest(line);

        Console.WriteLine(_failed == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({_failed})");
        return _failed == 0 ? 0 : 3;
    }

    /// <summary>A plain RWD car (1400 kg, 400 Nm) for the vehicle model, without any car files.</summary>
    private static VehicleData TestVehicle()
    {
        var tyre = new TyreData { DyRef = 1.25f, DxRef = 1.25f, Fz0 = 3000, LsExpY = 0.8f, LsExpX = 0.85f, FalloffLevel = 0.85f, FalloffSpeed = 3, Radius = 0.32f };
        return new VehicleData
        {
            Mass = 1400, YawInertia = 2300, RollInertia = 500, PitchInertia = 2100, Wheelbase = 2.6f, A = 1.25f, B = 1.35f,
            FrontSusp = new() { Arb = 20000, BaseY = -0.1f }, RearSusp = new() { Arb = 10000, BaseY = -0.1f },
            Front = tyre, Rear = tyre, Torque = Enumerable.Repeat(400f, 80).ToArray(), Limiter = 7500, IdleRpm = 1000, Ratios = [3, 2, 1.5f, 1.2f, 1, 0.85f],
            Final = 3.5f, BrakeTorque = 2500, BrakeFront = 0.65f, MaxSteer = 0.55f,
            Wings = [new() { Area = 1, AoaCd = new Lut([0f], [0.5f]) }],
        };
    }

    private static CarSpec TestCar() => new() { Model = "test", Physics = TestVehicle(), FuelCapacity = 60, KmPerLiter = 3 };

    /// <summary>The vehicle model alone: a skid pad at 90 km/h settles at a plausible lateral acceleration, front at its limit first.</summary>
    private static void VehicleModelTest()
    {
        var vd = TestVehicle();
        var vs = new VehicleState { Vx = 25 };
        var vin = new VehicleInputs { Steer = 0.06f, MassRatio = 1, Grip = 1, Ratio = 1.2f * 3.5f };
        for (int i = 0; i < 300 * 10; i++)
        {
            vin.Throttle = Math.Clamp(0.3f + (25 - vs.Vx) * 0.5f, 0, 1);
            Vehicle.Step(vd, vs, vin, 1f / 300);
        }
        float skidG = vs.Vx * MathF.Abs(vs.R) / CarSpec.G;
        Check($"vehicle model skid pad: {skidG:F2} g at {vs.Vx * 3.6f:F0} km/h, slip F/R {vs.FrontSlip:F2}/{vs.RearSlip:F2}",
            skidG is > 0.8f and < 1.35f && vs.FrontSlip > vs.RearSlip && MathF.Abs(vs.Vx - 25) < 1.5f);
    }

    private static RaceBot NewBot(int id, CarSpec car, StrengthCalibration cal, float strength, float aggression = 50)
        => new() { Id = id, Name = $"Bot {id}", Car = car, Driver = DriverProfile.FromStrength(strength, StrengthCalibration.PaceFor(strength), aggression) };

    /// <summary>A race of 8 bots, slowest first: everybody finishes, no deep overlaps, nothing NaN, the mistakes happen.</summary>
    private static void RaceTest(RacingLine line, StrengthCalibration cal, Options o)
    {
        var world = new RaceWorld(line, new RaceWorldSettings { Seed = 7 });
        var car = TestCar();
        for (int i = 0; i < 8; i++) world.Bots.Add(NewBot(i, car, cal, 86 + i * 2));
        for (int i = 0; i < world.Bots.Count; i++) world.PlaceAtGridSlot(world.Bots[i], i);
        foreach (var b in world.Bots) world.ResetCarCondition(b, world.FuelForLaps(b, 6));
        world.TraceBotId = o.Int("trace", -1);
        world.Trace = Console.WriteLine;
        world.StartRace(0);
        float maxPen = 0;
        bool finite = true, mistake = false;
        double t = 0;
        world.Advance(0);
        while (t < 900 && world.Bots.Any(b => b.LapsCompleted < 4))
        {
            t += 0.05;
            foreach (var b in world.Bots) b.RemainingLaps = Math.Max(0, 4 - b.LapsCompleted);
            world.Advance(t);
            foreach (var a in world.Bots)
            {
                var pose = world.GetPose(a);
                finite &= float.IsFinite(pose.Position.X) && float.IsFinite(pose.Rotation.X) && float.IsFinite(a.Speed);
                mistake |= a.Ai is { } ai && ai.InMistake(t);
                foreach (var b in world.Bots)
                {
                    if (a.Id >= b.Id) continue;
                    float lp = (a.Car.Length + b.Car.Length) / 2 - MathF.Abs(line.Delta(line.WrapS((float)a.Distance), line.WrapS((float)b.Distance)));
                    float wp = (a.Car.Width + b.Car.Width) / 2 - MathF.Abs(a.Offset - b.Offset);
                    if (lp > 0.05f && wp > 0.05f) maxPen = MathF.Max(maxPen, MathF.Min(lp, wp));
                }
            }
        }
        if (o.Has("verbose"))
            foreach (var b in world.Bots.OrderByDescending(b => b.Distance))
                Console.WriteLine($"  {b.Name} {b.Driver.Level:F0} % best {b.BestLapSeconds:F2} contacts {b.ContactCount} ghosts {b.GhostCount}");
        Check($"all bots finished 4 laps ({t:F0} s)", world.Bots.All(b => b.LapsCompleted >= 4));
        Check("nothing NaN", finite);
        Check($"no deep overlaps (max {maxPen:F2} m)", maxPen < 0.6f);
        Check("the humanizer's mistakes happen", mistake);
        var fastest = world.Bots.OrderBy(b => b.BestLapSeconds).First();
        Check($"best lap by a strong driver ({fastest.Name}, {fastest.Driver.Level:F0} %, {fastest.BestLapSeconds:F1} s)", fastest.Driver.Level >= 92);
        var gb = world.Bots[1];
        gb.LateralSpeed = 3;
        world.PlaceAtGridSlot(gb, 0);
        Check($"straight on the grid ({world.HeadingError(gb, world.GetPose(gb)):F1}°)", world.HeadingError(gb, world.GetPose(gb)) < 3);
    }

    /// <summary>A clearly faster bot starting right behind a slower one gets by within two laps (Kunos: the side with room, committed).</summary>
    private static void PassTest(RacingLine line, StrengthCalibration cal)
    {
        var world = new RaceWorld(line, new RaceWorldSettings { Seed = 3, HumanErrors = false });
        var car = TestCar();
        var slow = NewBot(0, car, cal, 85);
        var fast = NewBot(1, car, cal, 100, 80);
        world.Bots.Add(slow);
        world.Bots.Add(fast);
        world.PlaceAt(slow, 100, 0, BotPhase.Racing);
        world.PlaceAt(fast, 80, 0, BotPhase.Racing);
        slow.Speed = fast.Speed = 30;
        double t = 0, passedAt = double.NaN;
        world.Advance(0);
        while (t < 300 && double.IsNaN(passedAt))
        {
            t += 0.05;
            world.Advance(t);
            if (fast.Distance > slow.Distance + 6) passedAt = t;
        }
        Check($"a faster bot passes a slower one ({(double.IsNaN(passedAt) ? "never" : $"after {passedAt:F0} s")}, contacts {fast.ContactCount})", !double.IsNaN(passedAt));
    }

    private static PitLane TestLane(RacingLine line)
    {
        var lanePts = new List<FastLanePoint>();
        for (float x = -150; x <= 750; x += 1.5f)
        {
            // joins the line before and after, 9 m to the side along the straight
            float side = x < 50 ? MathF.Max(0, (x + 150) / 200) : x > 550 ? MathF.Max(0, (750 - x) / 200) : 1;
            lanePts.Add(new FastLanePoint { Position = new Vector3(x < 0 ? 0 : x, 0, 9 * side), Normal = Vector3.UnitY });
        }
        // before x=0 the lane runs on the last metres of the oval: use the line itself there
        for (int i = 0; i < lanePts.Count; i++)
        {
            if (lanePts[i].Position.X > 0) continue;
            float back = -(-150 + i * 1.5f);
            var q = line.PositionAt(line.Length - back, 0);
            lanePts[i] = new FastLanePoint { Position = q + new Vector3(0, 0, 9 * MathF.Max(0, (150 - back) / 200)), Normal = Vector3.UnitY };
        }
        return new PitLane(lanePts.ToArray(), line);
    }

    /// <summary>Heavy fuel use: every bot stops, nobody runs dry, everybody finishes.</summary>
    private static void PitTest(RacingLine line, StrengthCalibration cal)
    {
        var lane = TestLane(line);
        var world = new RaceWorld(line, new RaceWorldSettings { Seed = 5, FuelRate = 6, TyreWearRate = 1 }) { PitLane = lane };
        int stops = 0;
        world.PitStopCompleted += (_, _, _, _) => stops++;
        var car = TestCar();
        car.KmPerLiter = 1.6f;
        for (int i = 0; i < 4; i++)
        {
            var bot = NewBot(i, car, cal, 95);
            world.Bots.Add(bot);
            world.SetPitBox(bot, new Vector3(250 + i * 10, 0, 13));
        }
        for (int i = 0; i < world.Bots.Count; i++) world.PlaceAtGridSlot(world.Bots[i], i);
        foreach (var b in world.Bots) world.ResetCarCondition(b, world.FuelForLaps(b, 3));
        world.StartRace(0);
        double t = 0;
        world.Advance(0);
        bool dry = false;
        while (t < 1500 && world.Bots.Any(b => b.LapsCompleted < 10))
        {
            t += 0.05;
            foreach (var b in world.Bots) b.RemainingLaps = Math.Max(0, 10 - b.LapsCompleted);
            world.Advance(t);
            dry |= world.Bots.Any(b => b.Fuel <= 0 && b.LapsCompleted < 10);
        }
        Check($"pit lane {lane.Length:F0} m, limiter {lane.LimiterStart:F0}-{lane.LimiterEnd:F0}", lane.LimiterEnd > lane.LimiterStart);
        Check($"pit stops made ({stops}), all finished", stops >= 4 && world.Bots.All(b => b.LapsCompleted >= 10));
        Check("nobody ran out of fuel", !dry);
    }

    /// <summary>Like the AC AI: the preferred compound in a race, the grippiest in qualifying, LEGAL_TYRES, never rain tyres.</summary>
    private static void TyreTest()
    {
        Lut Wear(float drop20) => new([0, 20, 60], [100, 100 * (1 - drop20), 100 * (1 - drop20 * 2)]);
        var gt3 = new CarSpec { Model = "gt3", CalibratedTyreVkmPerLap = 2 };
        gt3.Compounds = [new Compound { Index = 0, Name = "Slick Soft", ShortName = "S", Grip = 1.013f, Wear = Wear(0.10f) },
            new Compound { Index = 1, Name = "Slick Medium", ShortName = "M", Grip = 1, Wear = Wear(0.04f) },
            new Compound { Index = 2, Name = "Slick Hard", ShortName = "H", Grip = 0.987f, Wear = Wear(0.025f) },
            new Compound { Index = 3, Name = "Wet", ShortName = "W", Grip = 0.95f }];
        gt3.DefaultCompound = gt3.Compounds[1];
        var world = new RaceWorld(new RacingLine(SyntheticTrack()), new RaceWorldSettings { Seed = 2 });
        RaceBot Bot(string legal = "") => new() { Id = 0, Name = "T", Car = gt3, LegalTyres = legal };
        var race = Bot();
        world.ChooseTyres(race, 20);
        Check($"race: the preferred compound ({RaceWorld.CompoundName(race)})", RaceWorld.CompoundName(race) == "M");
        var legal = Bot("S;H");
        world.ChooseTyres(legal, 20);
        Check($"LEGAL_TYRES S;H: not the medium ({RaceWorld.CompoundName(legal)})", RaceWorld.CompoundName(legal) is "S" or "H");
        var wetOnly = Bot("W");
        world.ChooseTyres(wetOnly, 10);
        Check("never rain tyres", wetOnly.Tyres == null);
        world.Settings.Qualifying = true;
        var quali = Bot();
        world.ChooseTyres(quali, 3);
        Check($"qualifying: the grippiest ({RaceWorld.CompoundName(quali)})", RaceWorld.CompoundName(quali) == "S");
    }

    private static void DamageTest(RacingLine line)
    {
        var world = new RaceWorld(line, new RaceWorldSettings { Seed = 4 });
        var b = new RaceBot { Id = 0, Name = "D", Car = TestCar() };
        world.Bots.Add(b);
        world.AddDamage(b, 0, 80, 0.3f);
        Check($"damage: body {RaceWorld.BodyDamagePercent(b):F0} %, suspension {b.Suspension:F2}, grip {RaceWorld.DamageGrip(b):F2}",
            RaceWorld.HasDamage(b) && RaceWorld.DamageGrip(b) < 1);
        world.RepairDamage(b);
        Check("repaired", !RaceWorld.HasDamage(b));
        var rain = new RaceWorld(line, new RaceWorldSettings { Seed = 6, Wetness = 0.8f, Water = 0.3f });
        Check($"rain: wet grip {rain.RainGrip(b):F2}", rain.RainGrip(b) < 0.9f);
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
