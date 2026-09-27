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

        float boost = 0;
        foreach (var section in engine.Sections.Where(s => s.StartsWith("TURBO_", StringComparison.OrdinalIgnoreCase)))
        {
            float maxBoost = engine.GetFloat(section, "MAX_BOOST", 0);
            float wastegate = engine.GetFloat(section, "WASTEGATE", maxBoost);
            boost += MathF.Min(maxBoost, wastegate > 0 ? wastegate : maxBoost);
        }
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
        string traction = drivetrain.Get("TRACTION", "TYPE") ?? "RWD";

        var tyres = Ini(files, "tyres.ini");
        {
            // wear curve of the default compound (front and rear averaged)
            int idx = tyres.GetInt("COMPOUND_DEFAULT", "INDEX", 0);
            string sfx = idx == 0 ? "" : $"_{idx}";
            string front = $"FRONT{sfx}", rear = $"REAR{sfx}";
            if (!tyres.HasSection(front)) { front = "FRONT"; rear = "REAR"; }
            spec.TyreCompound = tyres.Get(front, "NAME") ?? "";
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
        spec.BrakeGrip = dx * 0.95f;
        spec.TyreDiameter = 2 * rearRadius;

        float cgFront = 0.5f, wheelbase = 2.6f, track = 1.65f;
        if (files.ContainsKey("suspensions.ini"))
        {
            var susp = Ini(files, "suspensions.ini");
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
            foreach (var wing in aero.Sections.Where(s => s.StartsWith("WING_", StringComparison.OrdinalIgnoreCase)))
            {
                float area = aero.GetFloat(wing, "CHORD", 0) * aero.GetFloat(wing, "SPAN", 0);
                float angle = aero.GetFloat(wing, "ANGLE", 0);
                clA += LutValue(files, aero.Get(wing, "LUT_AOA_CL"), angle) * aero.GetFloat(wing, "CL_GAIN", 1) * area;
                cdA += LutValue(files, aero.Get(wing, "LUT_AOA_CD"), angle) * aero.GetFloat(wing, "CD_GAIN", 1) * area;
            }
        }
        if (cdA < 0.2f) cdA = hasWings ? 0.9f : 0.7f;
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
        float torqueScale = (1 + boost) * restrictor;

        var table = new List<float>();
        float topSpeed = 0;
        for (int v = 0; v < 130; v++)
        {
            float best = 0;
            foreach (var ratio in ratios)
            {
                float rpm = v / (2 * MathF.PI * driveRadius) * 60 * ratio * final;
                if (rpm > limiter) continue;
                rpm = MathF.Max(rpm, MathF.Min(limiter * 0.6f, 4500)); // launch: clutch slips at a useful rpm
                float force = torque.At(rpm) * torqueScale * ratio * final * efficiency / driveRadius;
                best = MathF.Max(best, force);
            }
            float tractionLimit = spec.BrakeGrip * CarSpec.G * mass * drivenShare + clA * AirDensityHalf * v * v * spec.BrakeGrip * drivenShare * 0.9f;
            float drag = cdA * AirDensityHalf * v * v + 0.012f * mass * CarSpec.G;
            float a = (MathF.Min(best, tractionLimit) - drag) / mass;
            if (a <= 0.05f && v > 20)
            {
                table.Add(0);
                break;
            }
            table.Add(a);
            topSpeed = v;
        }
        spec.AccelTable = table.ToArray();
        spec.Acceleration = table.Count > 5 ? table[5] : 7;
        spec.TopSpeed = MathF.Max(30, topSpeed);

        spec.GearTopSpeedsKmh = ratios.Select(r => limiter / 60f * 2 * MathF.PI * driveRadius / (r * final) * 3.6f).ToArray();
        if (spec.GearTopSpeedsKmh.Length == 0) spec.GearTopSpeedsKmh = [spec.TopSpeed * 3.6f];

        if (files.ContainsKey("ai.ini"))
        {
            var ai = Ini(files, "ai.ini");
            spec.UpshiftRpm = ai.GetInt("GEARS", "UP", 0);
        }

        // crests: cars without much downforce get light earlier
        spec.CrestFactor = hasWings ? 1.35f : 1.2f;
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
}
