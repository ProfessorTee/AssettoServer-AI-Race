using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssettoServer.Server.Configuration;
using RaceAiPlugin.Core;
using Serilog;
using YamlDotNet.RepresentationModel;

namespace RaceAiPlugin;

/// <summary>A vehicle class (GT3, GTE, ...): its models in order, each with its skins (first ones are used first).</summary>
public sealed class VehicleClass
{
    public string Key = "";
    public string Label = "";
    public string Description = "";
    public List<(string Model, List<string> Skins)> Models = [];

    public bool Contains(string model) => Models.Any(m => string.Equals(m.Model, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>Models without content/cars/&lt;model&gt;/data.acd on this server.</summary>
    public List<string> MissingModels()
        => Models.Select(m => m.Model).Where(m => !File.Exists(Path.Join("content", "cars", m, "data.acd"))).ToList();
}

/// <summary>
/// The vehicle classes: built in (same as race-ai/classes/classes.json), or classes.json in the server folder (or cfg/) instead.
/// </summary>
public static class ClassCatalog
{
    private static List<VehicleClass>? _all;
    private static readonly object Lock = new();

    public static IReadOnlyList<VehicleClass> All
    {
        get
        {
            lock (Lock) return _all ??= Load();
        }
    }

    public static VehicleClass? Get(string? key)
        => string.IsNullOrWhiteSpace(key) ? null
            : All.FirstOrDefault(c => string.Equals(c.Key, key.Trim(), StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(c.Label, key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The class most of these models belong to (more than half of them), else null.</summary>
    public static VehicleClass? Detect(IEnumerable<string> models)
    {
        var distinct = models.Where(m => !string.IsNullOrEmpty(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0) return null;
        var best = All.Select(c => (Class: c, Count: distinct.Count(c.Contains))).OrderByDescending(x => x.Count).First();
        return best.Count * 2 > distinct.Count ? best.Class : null;
    }

    /// <summary>Class labels (and GT2) in names and descriptions, to replace them with the new class.</summary>
    public static Regex LabelRegex()
        => new(@"\b(" + string.Join("|", All.Select(c => Regex.Escape(c.Label)).Append("GT2").Distinct()) + @")\b");

    private static List<VehicleClass> Load()
    {
        foreach (var path in new[] { "classes.json", Path.Join("cfg", "classes.json") })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var list = Parse(File.ReadAllText(path));
                Log.Information("Race AI: vehicle classes from {Path}: {Classes}", path, string.Join(", ", list.Select(c => c.Key)));
                return list;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Race AI: {Path} not readable, using the built-in vehicle classes", path);
            }
        }
        using var stream = typeof(ClassCatalog).Assembly.GetManifestResourceStream("RaceAiPlugin.classes.json");
        using var reader = new StreamReader(stream!);
        return Parse(reader.ReadToEnd());
    }

    private static List<VehicleClass> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<VehicleClass>();
        foreach (var c in doc.RootElement.GetProperty("classes").EnumerateObject())
        {
            var vc = new VehicleClass
            {
                Key = c.Name.ToLowerInvariant(),
                Label = c.Value.TryGetProperty("label", out var l) ? l.GetString() ?? c.Name.ToUpperInvariant() : c.Name.ToUpperInvariant(),
                Description = c.Value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : ""
            };
            var models = c.Value.GetProperty("models");
            if (models.ValueKind == JsonValueKind.Array)
                foreach (var m in models.EnumerateArray()) vc.Models.Add((m.GetString() ?? "", []));
            else
                foreach (var m in models.EnumerateObject())
                    vc.Models.Add((m.Name, m.Value.ValueKind == JsonValueKind.Array ? m.Value.EnumerateArray().Select(s => s.GetString() ?? "").ToList() : []));
            vc.Models.RemoveAll(m => m.Model == "");
            if (vc.Models.Count > 0) list.Add(vc);
        }
        return list;
    }
}

/// <summary>
/// Finds the preset of a track for a vehicle class, or makes one: presets/&lt;track&gt;-&lt;class&gt;/ with only what differs from
/// the track's preset (entry_list.ini with the class's cars, name, description, welcome text). Every other setting keeps coming from
/// the track's preset and cfg/. Made presets carry a marker file and are refreshed on every class change, so changes in cfg/ reach them.
/// </summary>
public static class ClassPresets
{
    private const string Marker = ".raceai-class.json";
    private static readonly object Lock = new();

    public sealed record PresetInfo(string Name, string Dir, string TrackKey, string Track, VehicleClass? Class, bool Generated, string? Base);

    private sealed class MarkerData
    {
        public string Base { get; set; } = "";
        public string Class { get; set; } = "";
    }

    public static string DirOf(string preset) => preset == "default" ? "cfg" : Path.Join("presets", preset);

    /// <summary>All Race AI presets ("default" = cfg/ and every presets/&lt;name&gt;/ with a plugin_race_ai_cfg.yml).</summary>
    public static List<PresetInfo> Scan()
    {
        var list = new List<PresetInfo>();
        var names = new List<string> { "default" };
        if (Directory.Exists("presets"))
            names.AddRange(Directory.GetDirectories("presets").Select(Path.GetFileName).OfType<string>()
                .Where(n => File.Exists(Path.Join("presets", n, "plugin_race_ai_cfg.yml"))).OrderBy(n => n, StringComparer.Ordinal));
        foreach (var n in names)
        {
            try
            {
                if (Info(n) is { } info) list.Add(info);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Race AI: preset {Preset} not readable", n);
            }
        }
        return list;
    }

    public static PresetInfo? Info(string name)
    {
        string dir = DirOf(name);
        if (!Directory.Exists(dir)) return null;
        var own = File.Exists(Path.Join(dir, "server_cfg.ini")) ? IniFile.Load(Path.Join(dir, "server_cfg.ini")) : null;
        var main = name != "default" && File.Exists(Path.Join("cfg", "server_cfg.ini")) ? IniFile.Load(Path.Join("cfg", "server_cfg.ini")) : null;
        string? track = own?.Get("SERVER", "TRACK") ?? main?.Get("SERVER", "TRACK");
        if (string.IsNullOrEmpty(track)) return null;
        string layout = own?.Get("SERVER", "CONFIG_TRACK") ?? main?.Get("SERVER", "CONFIG_TRACK") ?? "";
        track = CSPTrackOptions.Parse(track).Track;
        string folder = track[(track.LastIndexOf('/') + 1)..];
        string trackKey = string.IsNullOrEmpty(layout) ? folder : $"{folder}-{layout}";

        var models = new List<string>();
        if (EntryListPath(dir) is { } el)
        {
            var ini = IniFile.Load(el);
            models.AddRange(ini.Sections.Where(s => s.StartsWith("CAR_", StringComparison.OrdinalIgnoreCase))
                .Select(s => ini.Get(s, "MODEL") ?? ""));
        }
        MarkerData? marker = null;
        try { if (File.Exists(Path.Join(dir, Marker))) marker = JsonSerializer.Deserialize<MarkerData>(File.ReadAllText(Path.Join(dir, Marker))); } catch { }
        return new PresetInfo(name, dir, trackKey, folder, ClassCatalog.Detect(models), marker != null, marker?.Base);
    }

    private static string? EntryListPath(string dir)
        => File.Exists(Path.Join(dir, "entry_list.ini")) ? Path.Join(dir, "entry_list.ini")
            : File.Exists(Path.Join("cfg", "entry_list.ini")) ? Path.Join("cfg", "entry_list.ini") : null;

    /// <summary>
    /// The preset to run for track preset <paramref name="preset"/> with class <paramref name="cls"/>: the preset itself when it already
    /// has that class, else another preset of the same track with that class (own presets first), else a new one (<paramref name="create"/>).
    /// <paramref name="refresh"/>: made presets are written again from their track's preset. Null when there is none.
    /// </summary>
    public static string? Resolve(string preset, VehicleClass cls, bool create, bool refresh = false)
    {
        lock (Lock)
        {
            var all = Scan();
            var p = all.FirstOrDefault(x => x.Name == preset);
            if (p == null) return null;
            if (p.Class?.Key == cls.Key && !p.Generated) return p.Name;
            // a made preset: the track preset it was made from is the base
            var basePreset = p.Generated && p.Base != null ? all.FirstOrDefault(x => x.Name == p.Base) ?? p : p;

            var same = all.Where(x => x.TrackKey == basePreset.TrackKey && x.Class?.Key == cls.Key).ToList();
            var own = same.FirstOrDefault(x => !x.Generated);
            if (own != null) return own.Name;
            var made = same.FirstOrDefault(x => x.Generated && x.Base == basePreset.Name) ?? same.FirstOrDefault(x => x.Generated);
            if (made != null && !refresh) return made.Name;
            if (!create && made == null) return null;

            if (basePreset.Generated) return made?.Name; // base got lost: don't build on a made preset
            string name = made?.Name ?? NewName(basePreset, cls);
            Generate(basePreset, cls, name);
            return name;
        }
    }

    private static string NewName(PresetInfo b, VehicleClass cls)
    {
        string stem = b.Name == "default" ? b.Track.StartsWith("ks_") ? b.Track[3..] : b.Track : b.Name;
        string name = $"{stem}-{cls.Key}";
        // a folder of that name that isn't ours (other track or class): don't touch it
        for (int i = 2; Directory.Exists(Path.Join("presets", name)) && !File.Exists(Path.Join("presets", name, Marker)); i++)
            name = $"{stem}-{cls.Key}-{i}";
        return name;
    }

    /// <summary>Write presets/&lt;name&gt;/ for class <paramref name="cls"/> on the track of <paramref name="b"/>.</summary>
    private static void Generate(PresetInfo b, VehicleClass cls, string name)
    {
        string dir = Path.Join("presets", name);
        Directory.CreateDirectory(dir);
        // the track preset's own files (overlays, grid etc.); cfg/ ones are not copied, they apply anyway
        if (b.Name != "default")
            foreach (var f in Directory.GetFiles(b.Dir))
            {
                string fn = Path.GetFileName(f);
                if (fn.Contains(".bak") || fn == Marker) continue;
                File.Copy(f, Path.Join(dir, fn), overwrite: true);
            }
        var labels = ClassCatalog.LabelRegex();

        // entry_list.ini: every slot gets a car of the class (players and bots each cycle through all models)
        string elText = Read(EntryListPath(b.Dir)!, out string nl);
        File.WriteAllText(Path.Join(dir, "entry_list.ini"), AssignCars(elText, cls).Replace("\n", nl), new UTF8Encoding(false));

        // server_cfg.ini: name, car list, own welcome text
        var mainIni = File.Exists(Path.Join("cfg", "server_cfg.ini")) ? IniFile.Load(Path.Join("cfg", "server_cfg.ini")) : null;
        string ownCfg = Path.Join(b.Dir, "server_cfg.ini");
        var ownIni = b.Name != "default" && File.Exists(ownCfg) ? IniFile.Load(ownCfg) : null;
        string serverName = ownIni?.Get("SERVER", "NAME") ?? mainIni?.Get("SERVER", "NAME") ?? "Race AI";
        serverName = labels.IsMatch(serverName) ? labels.Replace(serverName, cls.Label) : $"{serverName} [{cls.Label}]";

        string? welcomeSrc = null;
        if (ownIni?.Get("SERVER", "WELCOME_MESSAGE") is { Length: > 0 } ow) welcomeSrc = Path.Join(b.Dir, ow);
        else if (mainIni?.Get("SERVER", "WELCOME_MESSAGE") is { Length: > 0 } mw) welcomeSrc = mw;
        bool welcome = welcomeSrc != null && File.Exists(welcomeSrc);
        if (welcome)
        {
            string w = Read(welcomeSrc!, out string wnl);
            File.WriteAllText(Path.Join(dir, "welcome.txt"), labels.Replace(w, cls.Label).Replace("\n", wnl), new UTF8Encoding(false));
        }

        string cfgText = ownIni != null ? Read(ownCfg, out nl) : "[SERVER]\n";
        cfgText = SetIni(cfgText, "SERVER", "NAME", serverName);
        cfgText = SetIni(cfgText, "SERVER", "CARS", string.Join(";", cls.Models.Select(m => m.Model)));
        if (welcome) cfgText = SetIni(cfgText, "SERVER", "WELCOME_MESSAGE", "welcome.txt");
        if (ownIni == null)
            cfgText = $"; {name}: only what differs from cfg/server_cfg.ini (made by Race AI for class {cls.Label}, rewritten on a class change)\n" + cfgText;
        File.WriteAllText(Path.Join(dir, "server_cfg.ini"), cfgText.Replace("\n", nl), new UTF8Encoding(false));

        // extra_cfg.yml: the description in Content Manager's server list
        string? desc = null;
        try
        {
            string? merged = PresetOverlay.ReadYaml(b.Dir, "extra_cfg.yml");
            if (merged != null)
            {
                var ys = new YamlStream();
                ys.Load(new StringReader(merged));
                if (ys.Documents.Count > 0 && ys.Documents[0].RootNode is YamlMappingNode map
                    && map.Children.TryGetValue(new YamlScalarNode("ServerDescription"), out var dn) && dn is YamlScalarNode ds)
                    desc = ds.Value;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Race AI: extra_cfg.yml of {Preset} not readable", b.Name);
        }
        if (!string.IsNullOrEmpty(desc))
        {
            string extraPath = Path.Join(dir, "extra_cfg.yml");
            string extra = File.Exists(extraPath) ? Read(extraPath, out nl) : "";
            string line = "ServerDescription: \"" + labels.Replace(desc, cls.Label).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            extra = Regex.IsMatch(extra, @"(?m)^ServerDescription:")
                ? Regex.Replace(extra, @"(?m)^ServerDescription:.*$", _ => line)
                : (extra.Length > 0 && !extra.EndsWith('\n') ? extra + "\n" : extra) + line + "\n";
            File.WriteAllText(extraPath, extra.Replace("\n", nl), new UTF8Encoding(false));
        }

        // plugin_race_ai_cfg.yml: marks the folder as a Race AI preset; settings come from the track's preset and cfg/
        string rc = Path.Join(dir, "plugin_race_ai_cfg.yml");
        if (!File.Exists(rc))
            File.WriteAllText(rc, $"# {name}: only what differs from cfg/plugin_race_ai_cfg.yml (made by Race AI for class {cls.Label})\n");

        File.WriteAllText(Path.Join(dir, Marker), JsonSerializer.Serialize(new MarkerData { Base = b.Name, Class = cls.Key }));
        var missing = cls.MissingModels();
        Log.Information("Race AI: preset {Name} written ({Class} on {Track}, from {Base}){Missing}", name, cls.Label, b.TrackKey, b.Name,
            missing.Count > 0 ? " – no content/cars/<model>/data.acd for " + string.Join(", ", missing) : "");
    }

    private static string Read(string path, out string nl)
    {
        string text = File.ReadAllText(path);
        nl = text.Contains("\r\n") ? "\r\n" : "\n";
        return text.Replace("\r\n", "\n").TrimStart('﻿');
    }

    /// <summary>Slot i gets model i % n, each further round the next skin; bots continue after the players' skins.</summary>
    public static string AssignCars(string entryList, VehicleClass cls)
    {
        var blocks = Regex.Split(entryList, @"(?m)^(?=\[CAR_\d+\])");
        var cars = blocks.Where(b => b.StartsWith("[CAR_")).ToList();
        var isAi = cars.Select(b => Regex.IsMatch(b, @"(?mi)^AI\s*=\s*(fixed|auto)")).ToList();
        int nAi = isAi.Count(x => x), nPl = cars.Count - nAi, n = cls.Models.Count;
        int skip = (nPl + n - 1) / n * n;
        (string, string) Pick(int i)
        {
            var (m, skins) = cls.Models[i % n];
            return (m, skins.Count > 0 ? skins[i / n % skins.Count] : "");
        }
        int pl = 0, ai = 0, k = 0;
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            var block = b;
            if (block.StartsWith("[CAR_"))
            {
                var (m, s) = isAi[k++] ? Pick(skip + ai++) : Pick(pl++);
                block = Regex.IsMatch(block, @"(?m)^MODEL=") ? Regex.Replace(block, @"(?m)^MODEL=.*$", _ => "MODEL=" + m) : Insert(block, "MODEL=" + m);
                block = Regex.IsMatch(block, @"(?m)^SKIN=") ? Regex.Replace(block, @"(?m)^SKIN=.*$", _ => "SKIN=" + s) : Insert(block, "SKIN=" + s);
            }
            sb.Append(block);
        }
        return sb.ToString();

        static string Insert(string block, string line)
        {
            int eol = block.IndexOf('\n');
            return eol < 0 ? block + "\n" + line + "\n" : block[..(eol + 1)] + line + "\n" + block[(eol + 1)..];
        }
    }

    /// <summary>Set KEY=value in [section] of an ini text (added when missing, section too).</summary>
    public static string SetIni(string text, string section, string key, string value)
    {
        var lines = text.Split('\n').ToList();
        int start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            lines.Add("");
            return string.Join("\n", lines);
        }
        int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        for (int i = start + 1; i < end; i++)
        {
            var t = lines[i].TrimStart();
            int eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return string.Join("\n", lines);
            }
        }
        lines.Insert(start + 1, $"{key}={value}");
        return string.Join("\n", lines);
    }
}
