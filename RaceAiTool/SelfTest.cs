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
                Driver = DriverProfile.FromLevel(85 + i * 1.5f, 60)
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
        Check($"best lap by a strong driver ({fastest.Name}, level {fastest.Driver.Level:F0}, {fastest.BestLapSeconds:F1} s)", fastest.Driver.Level >= 92);

        // pose sanity
        var pose = world.GetPose(world.Bots[0]);
        Check("pose finite", !float.IsNaN(pose.Position.X) && !float.IsNaN(pose.Rotation.X) && pose.Gear >= 1);

        Console.WriteLine(_failed == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({_failed})");
        return _failed == 0 ? 0 : 3;
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
