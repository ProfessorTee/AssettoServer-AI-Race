using TrackGeometry;
namespace RaceAiPlugin.Core;

/// <summary>
/// Turns an AI strength in percent into a driver pace, per car:
/// 100 % = the best lap the car can do on this track (driven at its limit), 95 % = a lap time of best / 0.95, and so on.
/// Measured once per car by driving simulated flying laps at a few pace values.
/// </summary>
public sealed class StrengthCalibration
{
    private static readonly float[] Paces = [0.4f, 0.55f, 0.7f, 0.8f, 0.9f, 0.95f, 1.0f];
    private readonly float[] _lapTimes;

    /// <summary>Flying lap at 100 % (seconds).</summary>
    public float BestLap => _lapTimes[^1];
    public IReadOnlyList<float> LapTimes => _lapTimes;

    /// <summary>Seconds per lap lost to human errors at error level 0.5 and 1 (see <see cref="DriverProfile.Errors"/>).</summary>
    public float ErrorLossHalf { get; private set; }
    public float ErrorLossFull { get; private set; }

    private StrengthCalibration(float[] lapTimes) => _lapTimes = lapTimes;

    /// <summary>Average time lost per lap to mistakes at an error level (0..1).</summary>
    public float ErrorLoss(float errors)
    {
        errors = Math.Clamp(errors, 0, 1);
        return errors <= 0.5f ? ErrorLossHalf * errors / 0.5f : ErrorLossHalf + (ErrorLossFull - ErrorLossHalf) * (errors - 0.5f) / 0.5f;
    }

    /// <summary>
    /// Drives the calibration laps (7 pace values, plus 1 + 2 x 5 laps with mistakes). All laps are independent, so they run in parallel
    /// on <paramref name="threads"/> cores (0 = all). <paramref name="step"/>: simulation step (the live world's step for a final
    /// calibration, a coarse one for a quick provisional one); <paramref name="errorLaps"/>: laps averaged per error level.
    /// </summary>
    public static StrengthCalibration Measure(RacingLine line, CarSpec car, RaceWorldSettings template, float step = RaceWorld.DefaultStep,
        int threads = 0, int errorLaps = 5, CancellationToken cancel = default)
    {
        const float errorPace = 0.75f; // mistakes are measured at a typical pace of a weaker bot
        var jobs = new List<(float Pace, float Errors, int Seed)>();
        foreach (var p in Paces) jobs.Add((p, 0, 1));
        if (template.HumanErrors)
        {
            jobs.Add((errorPace, 0, 1));
            for (int k = 0; k < errorLaps; k++) jobs.Add((errorPace, 0.5f, 100 + k * 7));
            for (int k = 0; k < errorLaps; k++) jobs.Add((errorPace, 1f, 100 + k * 7));
        }
        var results = new float[jobs.Count];
        float fuel = 0, vkm = 0;
        Parallel.For(0, jobs.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = threads > 0 ? threads : Environment.ProcessorCount,
            CancellationToken = cancel
        }, i =>
        {
            var j = jobs[i];
            results[i] = FlyingLap(line, car, j.Pace, template, out var f, out var v, j.Errors, j.Seed, step: step);
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
        var cal = new StrengthCalibration(times);
        if (template.HumanErrors)
        {
            int o = Paces.Length;
            float clean = results[o];
            float half = results.Skip(o + 1).Take(errorLaps).Average();
            float full = results.Skip(o + 1 + errorLaps).Take(errorLaps).Average();
            cal.ErrorLossHalf = MathF.Max(0, half - clean);
            cal.ErrorLossFull = MathF.Max(cal.ErrorLossHalf, full - clean);
        }
        return cal;
    }

    /// <summary>Saved form (cache between server starts).</summary>
    public sealed class Saved
    {
        public float[] LapTimes { get; set; } = [];
        public float ErrorLossHalf { get; set; }
        public float ErrorLossFull { get; set; }
        public float FuelPerLap { get; set; }
        public float TyreVkmPerLap { get; set; }
    }

    public Saved Save(CarSpec car) => new()
    {
        LapTimes = _lapTimes.ToArray(), ErrorLossHalf = ErrorLossHalf, ErrorLossFull = ErrorLossFull,
        FuelPerLap = car.CalibratedFuelPerLap, TyreVkmPerLap = car.CalibratedTyreVkmPerLap
    };

    public static StrengthCalibration? Load(Saved saved, CarSpec car)
    {
        if (saved.LapTimes.Length != Paces.Length) return null;
        car.CalibratedFuelPerLap = saved.FuelPerLap;
        car.CalibratedTyreVkmPerLap = saved.TyreVkmPerLap;
        return new StrengthCalibration(saved.LapTimes) { ErrorLossHalf = saved.ErrorLossHalf, ErrorLossFull = saved.ErrorLossFull };
    }

    /// <summary>Target lap time for a strength in percent.</summary>
    public float LapTimeFor(float strengthPercent, float? referenceBestLap = null)
        => (referenceBestLap ?? BestLap) / Math.Clamp(strengthPercent / 100f, 0.3f, 1.2f);

    /// <summary>Driver pace (share of grip) that gives the lap time of <paramref name="strengthPercent"/>.</summary>
    public float PaceFor(float strengthPercent, float? referenceBestLap = null, float errors = 0)
    {
        // part of the time is lost to mistakes, the rest to a slower pace
        float target = LapTimeFor(strengthPercent, referenceBestLap) - ErrorLoss(errors);
        // lap time falls with pace: interpolate / extrapolate in the table
        for (int i = 0; i < Paces.Length - 1; i++)
        {
            if (target <= _lapTimes[i] && target >= _lapTimes[i + 1])
            {
                float t = (_lapTimes[i] - target) / MathF.Max(1e-3f, _lapTimes[i] - _lapTimes[i + 1]);
                return Paces[i] + (Paces[i + 1] - Paces[i]) * t;
            }
        }
        if (target > _lapTimes[0])
        {
            float slope = (Paces[1] - Paces[0]) / MathF.Max(1e-3f, _lapTimes[0] - _lapTimes[1]);
            return MathF.Max(0.25f, Paces[0] - (target - _lapTimes[0]) * slope);
        }
        {
            int n = Paces.Length;
            float slope = (Paces[n - 1] - Paces[n - 2]) / MathF.Max(1e-3f, _lapTimes[n - 2] - _lapTimes[n - 1]);
            return MathF.Min(1.08f, Paces[n - 1] + (_lapTimes[n - 1] - target) * slope);
        }
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
        float errors = 0, int seed = 1, float fuelLitres = 33.6f, float step = RaceWorld.DefaultStep)
    {
        var settings = new RaceWorldSettings
        {
            StartLineS = template.StartLineS,
            UseTrackHints = template.UseTrackHints,
            EdgeMargin = template.EdgeMargin,
            SideMargin = template.SideMargin,
            PlayerSideMargin = template.PlayerSideMargin,
            PlayerOverlap = template.PlayerOverlap,
            Seed = seed,
            HumanErrors = errors > 0,
            Spins = template.Spins,
            GrassMoments = template.GrassMoments,
            LineErrors = template.LineErrors,
            GrassAllowance = template.GrassAllowance,
            BotContacts = false,
            Damage = false,
            FuelRate = 1,
            TyreWearRate = 1,
            TyreWearScale = template.TyreWearScale,
            PitStops = false
        };
        var world = new RaceWorld(line, settings) { MaxStep = step };
        var bot = new RaceBot
        {
            Id = 0,
            Car = car,
            Driver = new DriverProfile { Pace = pace, Aggression = 0.5f, Consistency = 1f, Errors = errors }
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
                fuelAtLine = bot.Fuel;
                vkmAtLine = bot.TyreVirtualKm;
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
