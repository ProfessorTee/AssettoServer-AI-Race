using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RaceAiPlugin.Core;

/// <summary>
/// Builds a <see cref="CarSpec"/> from the real car data (data folder or data.acd, like Content Manager reads it):
/// mass, torque curve, turbo, gearing, tyre size and grip, aero lift/drag, dimensions and the Kunos ai.ini shift points.
/// Falls back to ui_car.json specs and finally to GT3 defaults for anything that cannot be read.
/// </summary>
public static partial class CarDataLoader
{
    private const float AirDensityHalf = 0.5f * 1.225f;

    public static CarSpec Load(string carsRoot, string model, float extraMassKg = 0, float restrictorPercent = 0, Action<string>? log = null)
    {
        var spec = new CarSpec { Model = model };
        string carDir = Path.Join(carsRoot, model);
        if (!Directory.Exists(carDir))
        {
            log?.Invoke($"Car folder {carDir} not found, using GT3 defaults for {model}");
            return spec;
        }

        Dictionary<string, byte[]>? files = null;
        string dataDir = Path.Join(carDir, "data");
        string acdPath = Path.Join(carDir, "data.acd");
        try
        {
            if (Directory.Exists(dataDir))
            {
                files = Directory.GetFiles(dataDir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
                spec.Source = "data folder";
            }
            else if (File.Exists(acdPath))
            {
                files = AcdReader.Read(acdPath, model);
                spec.Source = "data.acd";
                if (!files.ContainsKey("car.ini")) files = null;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not read car data of {model}: {ex.Message}");
            files = null;
        }

        var tags = ReadUiTags(carDir, out float uiBhp, out float uiWeight, out float uiTopSpeedKmh);
        bool hasWings = tags.Any(t => t.Contains("gt3", StringComparison.OrdinalIgnoreCase) || t.Contains("gte", StringComparison.OrdinalIgnoreCase)
                                      || t.Contains("prototype", StringComparison.OrdinalIgnoreCase) || t.Contains("race", StringComparison.OrdinalIgnoreCase));

        if (files == null)
        {
            ApplyUiFallback(spec, uiBhp, uiWeight, uiTopSpeedKmh, hasWings, extraMassKg);
            spec.Source = "ui_car.json";
            return spec;
        }

        try
        {
            FromData(spec, files, extraMassKg, restrictorPercent, hasWings);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Car data of {model} incomplete ({ex.Message}), using ui_car.json/defaults");
            ApplyUiFallback(spec, uiBhp, uiWeight, uiTopSpeedKmh, hasWings, extraMassKg);
            spec.Source = "ui_car.json";
        }

        return spec;
    }

    /// <summary>
    /// Recorded laps (RaceAiTool accel: GT3 cars on Trial Mountain and the Nordschleife, RX-7) pull harder at high speed than the aero.ini
    /// drag gives; 0.8 matches them within about 10 % up to 260 km/h.
    /// </summary>
    private const float DragScale = 0.8f;

    private static string Text(Dictionary<string, byte[]> files, string name)
        => files.TryGetValue(name, out var b) ? Encoding.UTF8.GetString(b) : throw new FileNotFoundException(name);

    private static IniFile Ini(Dictionary<string, byte[]> files, string name) => IniFile.Parse(Text(files, name));

    private static void FromData(CarSpec spec, Dictionary<string, byte[]> files, float extraMassKg, float restrictorPercent, bool hasWings)
    {
        var car = Ini(files, "car.ini");
        float mass = car.GetFloat("BASIC", "TOTALMASS", 1300) + 25 /* fuel */ + extraMassKg;
        spec.ReferenceMass = mass;
        spec.FuelCapacity = car.GetFloat("FUEL", "MAX_FUEL", 100);
        spec.DefaultFuel = car.GetFloat("FUEL", "FUEL", 30);
        spec.TyreChangeTime = car.GetFloat("PIT_STOP", "TYRE_CHANGE_TIME_SEC", 20);
        spec.FuelLiterTime = car.GetFloat("PIT_STOP", "FUEL_LITER_TIME_SEC", 0.2f);
        spec.BodyRepairTime = car.GetFloat("PIT_STOP", "BODY_REPAIR_TIME_SEC", 20);
        spec.SuspRepairTime = car.GetFloat("PIT_STOP", "SUSP_REPAIR_TIME_SEC", 30);
        if (files.ContainsKey("fuel_cons.ini"))
            spec.KmPerLiter = Math.Clamp(Ini(files, "fuel_cons.ini").GetFloat("FUEL_EVAL", "KM_PER_LITER", 1.6f), 0.3f, 30f);
        else
        {
            // AC: litres per second = rpm * gas * CONSUMPTION / 1000; at ~75 % rpm, 60 % gas, 45 m/s average
            float c = car.GetFloat("FUEL", "CONSUMPTION", 0.0055f);
            float lps = 7000 * 0.75f * 0.6f * c / 1000;
            spec.KmPerLiter = Math.Clamp(45f / 1000 / MathF.Max(lps, 1e-4f), 0.3f, 30f);
        }
        spec.SteerRatio = car.GetFloat("CONTROLS", "STEER_RATIO", 13);
        spec.SteerLock = car.GetFloat("CONTROLS", "STEER_LOCK", 320);

        var engine = Ini(files, "engine.ini");
        var torque = Lut.Parse(Text(files, engine.Get("HEADER", "POWER_CURVE") ?? "power.lut"));
        int limiter = engine.GetInt("ENGINE_DATA", "LIMITER", 0);
        if (limiter <= 0) limiter = (int)torque.X.Where((x, i) => torque.Y[i] > 0).DefaultIfEmpty(7500).Max();
        spec.MaxRpm = limiter;
        spec.IdleRpm = engine.GetInt("ENGINE_DATA", "MINIMUM", 1000);
        spec.CoastTorque = engine.GetFloat("COAST_REF", "TORQUE", 0);
        spec.CoastRpm = MathF.Max(1000, engine.GetFloat("COAST_REF", "RPM", limiter));

        // turbos (like AC): each builds its boost up to REFERENCE_RPM along a curve with exponent GAMMA, capped by the wastegate; the boosts
        // add up and multiply the torque of power.lut. A ctrl_turboN.ini driven by RPMS sets the wastegate by rpm (RX-7, Supra: the first
        // turbo hands over to the second one; GT3 cars: boost by rpm)
        var turbos = engine.Sections.Where(s => s.StartsWith("TURBO_", StringComparison.OrdinalIgnoreCase)).Select(section =>
        {
            float maxBoost = engine.GetFloat(section, "MAX_BOOST", 0);
            float wastegate = engine.GetFloat(section, "WASTEGATE", maxBoost);
            return (Max: MathF.Min(maxBoost, wastegate > 0 ? wastegate : maxBoost), Full: maxBoost,
                Ref: MathF.Max(1, engine.GetFloat(section, "REFERENCE_RPM", 0)), Gamma: engine.GetFloat(section, "GAMMA", 1),
                Ctrl: TurboController(files, section["TURBO_".Length..]));
        }).ToList();
        // spool time: AC moves the boost by (1 - LAG) per physics step (333 Hz)
        if (turbos.Count > 0)
        {
            float Tau(string key, float dflt) => engine.Sections.Where(s => s.StartsWith("TURBO_", StringComparison.OrdinalIgnoreCase))
                .Select(s => 1 / (333f * MathF.Max(1e-4f, 1 - engine.GetFloat(s, key, dflt)))).Average();
            spec.SpoolUp = Math.Clamp(Tau("LAG_UP", 0.995f), 0.05f, 4f);
            spec.SpoolDown = Math.Clamp(Tau("LAG_DN", 0.995f), 0.05f, 4f);
        }
        float BoostAt(float rpm) => turbos.Sum(t => MathF.Min(t.Ctrl?.Invoke(rpm) ?? t.Max, t.Full * MathF.Pow(Math.Clamp(rpm / t.Ref, 0, 1), MathF.Max(0.1f, t.Gamma))));
        float restrictor = 1 - Math.Clamp(restrictorPercent, 0, 100) / 100f * 0.5f;

        var drivetrain = Ini(files, "drivetrain.ini");
        int gearCount = drivetrain.GetInt("GEARS", "COUNT", 6);
        var ratios = new List<float>();
        for (int g = 1; g <= gearCount; g++)
        {
            float r = drivetrain.GetFloat("GEARS", $"GEAR_{g}", 0);
            if (r > 0) ratios.Add(r);
        }
        float final = drivetrain.GetFloat("GEARS", "FINAL", 3.5f);
        spec.ShiftUpTime = Math.Clamp(drivetrain.GetFloat("GEARBOX", "CHANGE_UP_TIME", 150) / 1000f, 0.03f, 0.6f);
        spec.ShiftDownTime = Math.Clamp(drivetrain.GetFloat("GEARBOX", "CHANGE_DN_TIME", 200) / 1000f, 0.03f, 0.6f);
        if (files.ContainsKey("damage.ini"))
        {
            // the softest visual part of each zone decides when it shows damage; glass of the zone as a fallback
            var dmg = Ini(files, "damage.ini");
            string[] zn = ["FRONT", "REAR", "LEFT", "RIGHT", "CENTER"];
            for (int z = 0; z < 5; z++)
            {
                var mins = dmg.Sections.Where(s => s.StartsWith("VISUAL_OBJECT_", StringComparison.OrdinalIgnoreCase)
                                                   && string.Equals(dmg.Get(s, "DAMAGE_ZONE")?.Trim(), zn[z], StringComparison.OrdinalIgnoreCase))
                    .Select(s => dmg.GetFloat(s, "MIN_SPEED", 20)).ToList();
                if (mins.Count == 0 && dmg.HasSection($"DAMAGE_GLASS_{zn[z]}")) mins.Add(dmg.GetFloat($"DAMAGE_GLASS_{zn[z]}", "MIN_SPEED", 20));
                if (mins.Count > 0) spec.DamageMinKmh[z] = Math.Clamp(mins.Min(), 0, 120);
            }
        }
        if (files.ContainsKey("electronics.ini"))
        {
            var el = Ini(files, "electronics.ini");
            spec.HasAbs = el.GetInt("ABS", "PRESENT", 0) == 1;
            spec.HasTc = el.GetInt("TRACTION_CONTROL", "PRESENT", 0) == 1;
        }
        string traction = drivetrain.Get("TRACTION", "TYPE") ?? "RWD";
        float diffPower = Math.Clamp(drivetrain.GetFloat("DIFFERENTIAL", "POWER", 0.3f), 0, 1);
        spec.ExitSlide = (traction.ToUpperInvariant() switch { "FWD" => 0.4f, "AWD" or "AWD2" => 0.7f, _ => 0.8f + 0.8f * diffPower });
        if (files.ContainsKey("brakes.ini")) spec.BrakeFront = Math.Clamp(Ini(files, "brakes.ini").GetFloat("DATA", "FRONT_SHARE", 0.66f), 0.4f, 0.9f);

        var tyres = Ini(files, "tyres.ini");
        {
            // wear curve of the default compound (front and rear averaged)
            int idx = tyres.GetInt("COMPOUND_DEFAULT", "INDEX", 0);
            string sfx = idx == 0 ? "" : $"_{idx}";
            string front = $"FRONT{sfx}", rear = $"REAR{sfx}";
            if (!tyres.HasSection(front)) { front = "FRONT"; rear = "REAR"; }
            spec.TyreCompound = tyres.Get(front, "NAME") ?? "";
            // temperature window: thermal sections carry the same suffix as the compound
            string tsfx = front == "FRONT" ? "" : sfx;
            var curves = new[] { tyres.Get($"THERMAL_FRONT{tsfx}", "PERFORMANCE_CURVE"), tyres.Get($"THERMAL_REAR{tsfx}", "PERFORMANCE_CURVE") }
                .Where(n => n != null && files.ContainsKey(n.Trim())).Select(n => Lut.Parse(Encoding.UTF8.GetString(files[n!.Trim()]))).Where(l => l.X.Length > 1).ToList();
            if (curves.Count > 0)
            {
                var c0 = curves[0];
                var ys = c0.X.Select(x => curves.Average(c => c.At(x))).ToArray();
                float max = ys.Max();
                if (max > 0)
                {
                    spec.TyreTempCurve = new Lut(c0.X, ys.Select(y => y / max).ToArray());
                    // the middle of the range at full grip
                    var top = c0.X.Where((x, k) => ys[k] >= max * 0.999f).ToList();
                    spec.TyreOptimum = Math.Clamp((top.Min() + top.Max()) / 2, 40, 120);
                }
            }
            var wf = tyres.Get(front, "WEAR_CURVE");
            var wr = tyres.Get(rear, "WEAR_CURVE");
            if (wf != null && files.TryGetValue(wf, out var fb))
            {
                var lf = Lut.Parse(Encoding.UTF8.GetString(fb));
                var lr = wr != null && files.TryGetValue(wr, out var rb) ? Lut.Parse(Encoding.UTF8.GetString(rb)) : lf;
                spec.TyreWear = new Lut(lf.X, lf.X.Select(x => (lf.At(x) + lr.At(x)) / 2).ToArray());
            }
        }
        float rearRadius = tyres.GetFloat("REAR", "RADIUS", 0.34f);
        float frontRadius = tyres.GetFloat("FRONT", "RADIUS", rearRadius);
        float dy = (tyres.GetFloat("FRONT", "DY_REF", 0) + tyres.GetFloat("REAR", "DY_REF", 0)) / 2;
        float dx = (tyres.GetFloat("FRONT", "DX_REF", 0) + tyres.GetFloat("REAR", "DX_REF", 0)) / 2;
        if (dy < 0.5f) dy = hasWings ? 1.58f : 1.25f;
        if (dx < 0.5f) dx = dy;
        spec.LateralGrip = dy * 0.98f;
        // braking and pulling out of slow corners are at the driver's limit, not the car's: recorded laps of an 80-90 % driver are no
        // reference for these (only full-throttle acceleration on the straights is)
        spec.BrakeGrip = dx * 0.95f;
        spec.TyreDiameter = 2 * rearRadius;

        float cgFront = 0.5f, wheelbase = 2.6f, track = 1.65f;
        if (files.ContainsKey("suspensions.ini"))
        {
            var susp = Ini(files, "suspensions.ini");
            spec.SuspDamageMinKmh = susp.GetFloat("DAMAGE", "MIN_VELOCITY", spec.SuspDamageMinKmh);
            wheelbase = susp.GetFloat("BASIC", "WHEELBASE", 2.6f);
            cgFront = susp.GetFloat("BASIC", "CG_LOCATION", 0.5f);
            track = MathF.Max(susp.GetFloat("FRONT", "TRACK", 1.6f), susp.GetFloat("REAR", "TRACK", 1.6f));
        }
        spec.Wheelbase = wheelbase;
        spec.Length = Math.Clamp(wheelbase + 1.9f, 3.8f, 5.2f);
        spec.Width = Math.Clamp(track + 0.3f, 1.7f, 2.15f);

        // aero: sum lift and drag areas of all wings at their default angle
        float clA = 0, cdA = 0;
        if (files.ContainsKey("aero.ini"))
        {
            var aero = Ini(files, "aero.ini");
            string[] zones = ["FRONT", "REAR", "LEFT", "RIGHT"];
            var zcd = new float[4];
            var zcl = new float[4];
            foreach (var wing in aero.Sections.Where(s => s.StartsWith("WING_", StringComparison.OrdinalIgnoreCase)))
            {
                float area = aero.GetFloat(wing, "CHORD", 0) * aero.GetFloat(wing, "SPAN", 0);
                float angle = aero.GetFloat(wing, "ANGLE", 0);
                float cl = LutValue(files, aero.Get(wing, "LUT_AOA_CL"), angle) * aero.GetFloat(wing, "CL_GAIN", 1) * area;
                float cd = LutValue(files, aero.Get(wing, "LUT_AOA_CD"), angle) * aero.GetFloat(wing, "CD_GAIN", 1) * area;
                clA += cl;
                cdA += cd;
                for (int z = 0; z < 4; z++)
                {
                    zcd[z] += cd * aero.GetFloat(wing, $"ZONE_{zones[z]}_CD", 0);
                    zcl[z] += MathF.Max(0, cl) * aero.GetFloat(wing, $"ZONE_{zones[z]}_CL", 0);
                }
            }
            if (cdA > 0.05f) spec.ZoneCd = zcd.Select(x => x / cdA).ToArray();
            if (clA > 0.05f) spec.ZoneCl = zcl.Select(x => x / clA).ToArray();
        }
        if (cdA < 0.2f) cdA = hasWings ? 0.9f : 0.7f;
        cdA *= DragScale;
        clA = MathF.Max(0, clA);
        spec.Downforce = clA * AirDensityHalf * spec.LateralGrip * 0.9f / mass;
        spec.DragCoefficient = cdA * AirDensityHalf / mass;
        spec.AeroBrake = (clA * AirDensityHalf * spec.BrakeGrip * 0.9f + cdA * AirDensityHalf) / mass;

        // full throttle acceleration table over speed (best gear at every speed, traction limited)
        float drivenShare = traction.Equals("AWD", StringComparison.OrdinalIgnoreCase) ? 1f
            : traction.Equals("FWD", StringComparison.OrdinalIgnoreCase) ? cgFront - 0.05f
            : (1 - cgFront) + 0.08f;
        float driveRadius = traction.Equals("FWD", StringComparison.OrdinalIgnoreCase) ? frontRadius : rearRadius;
        const float efficiency = 0.85f;
        float torqueScale = restrictor;

        var ers = ErsData.Read(files);
        var table = new List<float>();
        var turboAccel = new List<float>();
        var ersGain = new List<float>();
        var ersPower = new List<float>();
        float topSpeed = 0;
        for (int v = 0; v < 130; v++)
        {
            float best = 0, bestRatio = 0;
            int bestGear = 0;
            for (int g = 0; g < ratios.Count; g++)
            {
                float ratio = ratios[g];
                float rpm = v / (2 * MathF.PI * driveRadius) * 60 * ratio * final;
                if (rpm > limiter) continue;
                rpm = MathF.Max(rpm, MathF.Min(limiter * 0.6f, 4500)); // launch: clutch slips at a useful rpm
                float force = torque.At(rpm) * (1 + BoostAt(rpm)) * torqueScale * ratio * final * efficiency / driveRadius;
                if (force > best) { best = force; bestRatio = ratio; bestGear = g + 1; }
            }
            float tractionLimit = spec.BrakeGrip * CarSpec.G * mass * drivenShare + clA * AirDensityHalf * v * v * spec.BrakeGrip * drivenShare * 0.9f;
            float drag = cdA * AirDensityHalf * v * v + 0.012f * mass * CarSpec.G;
            float a = (MathF.Min(best, tractionLimit) - drag) / mass;
            if (turbos.Count > 0 && bestRatio > 0)
            {
                // what the boost adds at this speed (nothing where the tyres can't put it down anyway)
                float rpmBest = MathF.Max(v / (2 * MathF.PI * driveRadius) * 60 * bestRatio * final, MathF.Min(limiter * 0.6f, 4500));
                float noBoost = best / (1 + BoostAt(rpmBest));
                turboAccel.Add(MathF.Max(0, a - (MathF.Min(noBoost, tractionLimit) - drag) / mass));
            }
            if (a <= 0.05f && v > 20)
            {
                table.Add(0);
                break;
            }
            table.Add(a);
            topSpeed = v;

            if (ers != null)
            {
                // hybrid: electric motors on top of the engine (front motors bring their own traction)
                float wheelRpm = v / (2 * MathF.PI * frontRadius) * 60;
                float engineRpm = v / (2 * MathF.PI * driveRadius) * 60 * bestRatio * final;
                var (front, rear) = ers.Force(v * 3.6f, bestGear, wheelRpm, engineRpm, bestRatio * final * efficiency, frontRadius, driveRadius);
                float frontTraction = spec.BrakeGrip * CarSpec.G * mass * cgFront + clA * AirDensityHalf * v * v * spec.BrakeGrip * cgFront * 0.9f;
                float total = MathF.Min(best + rear, tractionLimit) + MathF.Min(front, frontTraction);
                float aWith = (total - drag) / mass;
                ersGain.Add(MathF.Max(0, aWith - a));
                ersPower.Add(MathF.Max(0, aWith - a) * mass * v);
            }
        }
        spec.ExitSlide *= spec.HasTc ? 0.6f : 1.3f;
        spec.AccelTable = table.ToArray();
        if (turboAccel.Count > 0) spec.TurboAccel = turboAccel.ToArray();
        if (ers != null && ersGain.Any(g => g > 0.05f))
        {
            spec.ErsGain = ersGain.ToArray();
            spec.ErsPower = ersPower.ToArray();
            spec.ErsKjPerLap = ers.KjPerLap;
        }
        spec.Acceleration = table.Count > 5 ? table[5] : 7;
        spec.TopSpeed = MathF.Max(30, topSpeed);

        spec.GearTopSpeedsKmh = ratios.Select(r => limiter / 60f * 2 * MathF.PI * driveRadius / (r * final) * 3.6f).ToArray();
        if (spec.GearTopSpeedsKmh.Length == 0) spec.GearTopSpeedsKmh = [spec.TopSpeed * 3.6f];

        if (files.ContainsKey("ai.ini"))
        {
            var ai = Ini(files, "ai.ini");
            spec.UpshiftRpm = ai.GetInt("GEARS", "UP", 0);
            spec.DownshiftRpm = ai.GetInt("GEARS", "DOWN", 0);
            // some cars have nonsense here (ks_ferrari_488_gt3: 85): then the estimate below the upshift point is used
            if (spec.DownshiftRpm < spec.IdleRpm + 500 || spec.DownshiftRpm >= spec.MaxRpm * 0.95f) spec.DownshiftRpm = 0;
        }

        // crests: cars without much downforce get light earlier
        spec.CrestFactor = hasWings ? 1.35f : 1.2f;
    }

    /// <summary>Wastegate by rpm from ctrl_turbo&lt;n&gt;.ini (controllers with INPUT=RPMS, ADD or MULT), null when there is none.</summary>
    private static Func<float, float>? TurboController(Dictionary<string, byte[]> files, string index)
    {
        if (!files.ContainsKey($"ctrl_turbo{index}.ini")) return null;
        var ini = Ini(files, $"ctrl_turbo{index}.ini");
        var parts = new List<(Lut Lut, bool Mult)>();
        foreach (var c in ini.Sections.Where(x => x.StartsWith("CONTROLLER_", StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.Equals(ini.Get(c, "INPUT")?.Trim(), "RPMS", StringComparison.OrdinalIgnoreCase)) continue;
            string? lut = ini.Get(c, "LUT")?.Trim();
            if (string.IsNullOrEmpty(lut)) continue;
            // inline "(|0=0.74|1000=0.74|...)" or a file name
            string text = lut.StartsWith('(')
                ? string.Join('\n', lut.Trim('(', ')').Split('|', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Replace('=', '|')))
                : files.TryGetValue(lut, out var b) ? Encoding.UTF8.GetString(b) : "";
            var parsed = Lut.Parse(text);
            if (parsed.X.Length > 0)
                parts.Add((parsed, string.Equals(ini.Get(c, "COMBINATOR")?.Trim(), "MULT", StringComparison.OrdinalIgnoreCase)));
        }
        if (parts.Count == 0) return null;
        return rpm =>
        {
            float v = 0;
            foreach (var (lut, mult) in parts) v = mult ? v * lut.At(rpm) : v + lut.At(rpm);
            return MathF.Max(0, v);
        };
    }

    private static float LutValue(Dictionary<string, byte[]> files, string? lutName, float x)
    {
        if (string.IsNullOrWhiteSpace(lutName) || !files.TryGetValue(lutName, out var bytes)) return 0;
        return Lut.Parse(Encoding.UTF8.GetString(bytes)).At(x);
    }

    private static void ApplyUiFallback(CarSpec spec, float bhp, float weight, float topSpeedKmh, bool hasWings, float extraMassKg)
    {
        if (topSpeedKmh > 100) spec.TopSpeed = topSpeedKmh / 3.6f;
        if (bhp > 50 && weight > 300)
        {
            float mass = weight + 100 + extraMassKg;
            // average acceleration at low speed from power-to-weight, capped by traction
            spec.Acceleration = Math.Clamp(bhp * 745.7f / mass / 12f, 3f, 9f);
        }
        if (!hasWings)
        {
            spec.Downforce = 0.0003f;
            spec.AeroBrake = 0.0006f;
            spec.LateralGrip = 1.2f;
            spec.BrakeGrip = 1.15f;
            spec.CrestFactor = 1.2f;
        }
    }

    private static List<string> ReadUiTags(string carDir, out float bhp, out float weight, out float topSpeedKmh)
    {
        bhp = weight = topSpeedKmh = 0;
        var tags = new List<string>();
        string path = Path.Join(carDir, "ui", "ui_car.json");
        if (!File.Exists(path)) return tags;
        try
        {
            // ui_car.json files are often not strictly valid JSON (raw line breaks in strings, trailing commas)
            var text = File.ReadAllText(path).Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
                tags.AddRange(t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.TrimStart('#')));
            if (doc.RootElement.TryGetProperty("class", out var cls) && cls.ValueKind == JsonValueKind.String)
                tags.Add(cls.GetString()!);
            if (doc.RootElement.TryGetProperty("specs", out var specs))
            {
                bhp = Number(specs, "bhp");
                weight = Number(specs, "weight");
                topSpeedKmh = Number(specs, "topspeed");
            }
        }
        catch
        {
            // ignore broken ui files
        }
        return tags;
    }

    private static float Number(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return 0;
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        var m = NumberRegex().Match(s);
        return m.Success && float.TryParse(m.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 0;
    }

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex NumberRegex();

    /// <summary>ers.ini of a hybrid (AC: KINETIC motor on the engine shaft, FRONT_MOTORS on the front axle) with its default deploy controller.</summary>
    private sealed class ErsData
    {
        public Lut? Rear, Front, Speed, Gear;
        public float KjPerLap;

        public static ErsData? Read(Dictionary<string, byte[]> files)
        {
            if (!files.ContainsKey("ers.ini")) return null;
            var ini = Ini(files, "ers.ini");
            var d = new ErsData
            {
                KjPerLap = ini.GetFloat("KINETIC", "MAX_KJ_PER_LAP", 0),
                Rear = LutRef(files, ini.Get("KINETIC", "TORQUE_CURVE")),
                Front = ini.HasSection("FRONT_MOTORS") ? LutRef(files, ini.Get("FRONT_MOTORS", "TORQUE_CURVE")) : null
            };
            if (d.KjPerLap <= 0) return null;
            string ctrl = $"ctrl_ers_{ini.GetInt("KINETIC", "DEFAULT_CONTROLLER", 0)}.ini";
            if (files.ContainsKey(ctrl))
            {
                var c = Ini(files, ctrl);
                foreach (var sec in c.Sections.Where(x => x.StartsWith("CONTROLLER_", StringComparison.OrdinalIgnoreCase)))
                {
                    string input = (c.Get(sec, "INPUT") ?? "").ToUpperInvariant();
                    if (input == "SPEED_KMH") d.Speed = LutRef(files, c.Get(sec, "LUT"));
                    else if (input == "GEAR") d.Gear = LutRef(files, c.Get(sec, "LUT"));
                }
            }
            return d;
        }

        /// <summary>Full-deploy push force (N) of the front motors and of the engine-shaft motor at this speed and gear.</summary>
        public (float Front, float Rear) Force(float kmh, int gear, float wheelRpm, float engineRpm, float overallRatio, float frontRadius, float driveRadius)
        {
            float k = Speed is { X.Length: > 0 } sp ? Math.Clamp(sp.At(kmh), 0, 1) : 1;
            if (Gear is { X.Length: > 0 } gl) k *= Math.Clamp(gl.At(gear), 0, 1);
            if (k <= 0) return (0, 0);
            float front = Front is { X.Length: > 0 } f ? MathF.Max(0, f.At(wheelRpm)) * k / frontRadius : 0;
            float rear = Rear is { X.Length: > 0 } r ? MathF.Max(0, r.At(engineRpm)) * k * overallRatio / driveRadius : 0;
            return (front, rear);
        }

        /// <summary>A file name or an inline table like (0=1|240=1|245=0).</summary>
        private static Lut? LutRef(Dictionary<string, byte[]> files, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (name.StartsWith('('))
                return Lut.Parse(name.Trim('(', ')').Replace('|', '\n').Replace('=', '|'));
            return files.TryGetValue(name, out var b) ? Lut.Parse(Encoding.UTF8.GetString(b)) : null;
        }
    }
}
