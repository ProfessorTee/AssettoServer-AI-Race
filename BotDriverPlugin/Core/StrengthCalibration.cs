using TrackGeometry;
namespace BotDriverPlugin.Core;

/// <summary>
/// The flying lap times of a car at a few AC AI levels (strength in % = AI level, as in AC), for the target laps and the
/// fuel and tyre use. Measured once per car by driving simulated flying laps.
/// </summary>
public sealed class StrengthCalibration
{
    private static readonly float[] Paces = [0.4f, 0.55f, 0.7f, 0.8f, 0.9f, 0.95f, 1.0f];
    private readonly float[] _lapTimes;

    /// <summary>Flying lap at 100 % (seconds).</summary>
    public float BestLap => _lapTimes[^1];
    public IReadOnlyList<float> LapTimes => _lapTimes;

    private StrengthCalibration(float[] lapTimes) => _lapTimes = lapTimes;

    /// <summary>
    /// Drives the calibration laps (one flying lap per level). All laps are independent, so they run in parallel on
    /// <paramref name="threads"/> cores (0 = all). <paramref name="step"/>: simulation step (the live world's step for a final calibration,
    /// a coarse one for a quick provisional one).
    /// </summary>
    public static StrengthCalibration Measure(RacingLine line, CarSpec car, RaceWorldSettings template, float step = RaceWorld.DefaultStep,
        int threads = 0, CancellationToken cancel = default)
    {
        var results = new float[Paces.Length];
        float fuel = 0, vkm = 0;
        Parallel.For(0, Paces.Length, new ParallelOptions
        {
            MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount,
            CancellationToken = cancel
        }, i =>
        {
            results[i] = FlyingLap(line, car, Paces[i], template, out var f, out var v, step: step);
            if (i == Paces.Length - 2)
            {
                // consumption and wear of a typical race pace
                fuel = f;
                vkm = v;
            }
        });
        car.CalibratedFuelPerLap = fuel;
        car.CalibratedTyreVkmPerLap = vkm;

        var times = results.Take(Paces.Length).ToArray();
        // make sure the table is strictly decreasing (guards against noise)
        for (int i = times.Length - 2; i >= 0; i--)
            times[i] = MathF.Max(times[i], times[i + 1] + 0.01f);
        return new StrengthCalibration(times);
    }

    /// <summary>Saved form (cache between server starts).</summary>
    public sealed class Saved
    {
        public float[] LapTimes { get; set; } = [];
        public float FuelPerLap { get; set; }
        public float TyreVkmPerLap { get; set; }
    }

    public Saved Save(CarSpec car) => new()
    {
        LapTimes = _lapTimes.ToArray(),
        FuelPerLap = car.CalibratedFuelPerLap, TyreVkmPerLap = car.CalibratedTyreVkmPerLap
    };

    public static StrengthCalibration? Load(Saved saved, CarSpec car)
    {
        if (saved.LapTimes.Length != Paces.Length) return null;
        car.CalibratedFuelPerLap = saved.FuelPerLap;
        car.CalibratedTyreVkmPerLap = saved.TyreVkmPerLap;
        return new StrengthCalibration(saved.LapTimes);
    }

    /// <summary>The AC AI level for a strength: like AC's AI_LEVEL, 95 % = level 0.95 (above 100 % the push goes past AC's maximum).</summary>
    public static float PaceFor(float strengthPercent) => Math.Clamp(strengthPercent / 100f, 0.25f, 1.1f);

    /// <summary>The flying lap at a strength's AI level (interpolated in the measured table, extrapolated past it).</summary>
    public float LapTimeFor(float strengthPercent)
    {
        float pace = PaceFor(strengthPercent);
        int i = 0;
        while (i < Paces.Length - 2 && pace > Paces[i + 1]) i++;
        float t = (pace - Paces[i]) / (Paces[i + 1] - Paces[i]);
        return MathF.Max(_lapTimes[^1] * 0.95f, _lapTimes[i] + (_lapTimes[i + 1] - _lapTimes[i]) * t);
    }

    /// <summary>
    /// Spreads strengths over <paramref name="count"/> drivers: evenly from strength - spread to strength + spread
    /// (or random in that range) and shuffled, so every bot gets a different value.
    /// </summary>
    public static float[] Distribute(int count, float strength, float spread, bool random, Random rng)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            float t = random ? (float)rng.NextDouble() : count == 1 ? 0.5f : i / (float)(count - 1);
            values[i] = strength - spread + 2 * spread * t;
        }
        // shuffle, so grid order / slot order says nothing about the strength
        for (int i = count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
        return values;
    }

    public static float FlyingLap(RacingLine line, CarSpec car, float pace, RaceWorldSettings template)
        => FlyingLap(line, car, pace, template, out _, out _);

    public static float FlyingLap(RacingLine line, CarSpec car, float pace, RaceWorldSettings template, out float fuelPerLap, out float tyreVkmPerLap,
        int seed = 1, float fuelLitres = 33.6f, float step = RaceWorld.DefaultStep)
    {
        var settings = new RaceWorldSettings
        {
            StartLineS = template.StartLineS,
            UseTrackHints = template.UseTrackHints,
            EdgeMargin = template.EdgeMargin,
            GrassAllowance = template.GrassAllowance,
            Seed = seed,
            // the lap time of the level itself, not of a mistake or one random genome / push drift
            HumanErrors = false,
            PushVariation = false,
            BotContacts = false,
            Damage = false,
            FuelRate = 1,
            TyreWearRate = 1,
            TyreWearScale = template.TyreWearScale,
            PitStops = false,
            PhysicsCorneringGrip = template.PhysicsCorneringGrip,
        };
        var world = new RaceWorld(line, settings) { MaxStep = step };
        var bot = new RaceBot
        {
            Id = 0,
            Car = car,
            Driver = new DriverProfile { Pace = pace, Aggression = 0.5f }
        };
        world.Bots.Add(bot);
        world.PlaceAt(bot, settings.StartLineS - 1500, 0, BotPhase.Racing);
        bot.Speed = 50;
        // constant light fuel load and fresh tyres, so the lap time only depends on the pace
        world.ResetCarCondition(bot, fuelLitres); // 33.6 l = 25 kg, the fuel load of the reference mass

        float lap = 0, fuelAtLine = -1, vkmAtLine = 0;
        fuelPerLap = 0;
        tyreVkmPerLap = 0;
        float fuelUsed = 0, vkmUsed = 0;
        world.LapCompleted += (b, t) =>
        {
            lap = t;
            fuelUsed = fuelAtLine - b.Fuel;
            vkmUsed = b.TyreVirtualKm - vkmAtLine;
        };
        double now = 0;
        world.Advance(0);
        while (lap == 0 && now < 3600)
        {
            now += 0.25;
            world.Advance(now);
            if (fuelAtLine < 0 && bot.TimingValid)
            {
                // first crossing: from here the lap is measured
                bot.Fuel = fuelLitres; // keep the weight constant-ish for the lap
                fuelAtLine = bot.Fuel;
                bot.TyreVirtualKm = 0;
                vkmAtLine = 0;
            }
        }
        fuelPerLap = fuelUsed;
        tyreVkmPerLap = vkmUsed;
        return lap > 0 ? lap : 3600;
    }
}
