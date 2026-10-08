using SharedPresets;
using TrackGeometry;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssettoServer.Server.Configuration;
using SharedConfig;
using BotDriverPlugin.Core;
using Serilog;
using YamlDotNet.Serialization;

namespace BotDriverPlugin;

/// <summary>
/// The strength of the field: strength in % per bot, personalities, aggression, rubber band, the bots' feature switches, and the
/// calibration (strength in % -> driver pace) behind it.
///
/// Calibration without making the server wait:
/// at start each car gets the final calibration from the cache if there is one, otherwise a provisional one
/// (an older build's from the cache, or a quick coarse measurement of a few seconds). The server starts right away;
/// the final calibrations are measured in the background on all cores and swapped in, then the other tracks of the
/// rotation are calibrated too, so a track change never has to wait for it.
/// </summary>
public sealed class FieldStrength
{
    private readonly BotRace _race;
    private readonly ConfigWriter _configWriter;
    private readonly Dictionary<CarSpec, StrengthCalibration> _calibrations = new();
    private readonly List<(Personality Personality, float Share)> _personalities = [];
    private float? _referenceBestLap;
    /// <summary>The lap time of 100 % (median of the bot cars), null when every car has its own.</summary>
    public float? ReferenceBestLap => _referenceBestLap;

    public FieldStrength(BotRace race, ConfigWriter configWriter)
    {
        _race = race;
        _configWriter = configWriter;
    }

    private BotDriverConfiguration _config => _race.Config;
    private object _lock => _race.Lock;
    private TrackData? _track => _race.Track;
    private RaceWorld? _world => _race.World;
    private List<BotSlot> _slots => _race.Slots;
    private List<string> _carRoots => _race.CarRoots;
    private string T(string en, string de) => _race.T(en, de);
    private static string FormatLap(float seconds) => BotRace.FormatLap(seconds);
    private string TrackKey() => _race.TrackKey;

    private readonly CancellationTokenSource _background = new();
    private readonly List<(CarSpec Spec, string Variant)> _provisional = [];
    private RaceWorldSettings? _calibrationSettings;

    /// <summary>Calibration jobs in the background (for the dashboard): what's running, how many are left.</summary>
    public string? CalibrationStatus { get; private set; }

    private static string BuildId => typeof(FieldStrength).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Cache key without the plugin build: same track, car and driving settings.</summary>
    private static string CalibrationBaseKey(string trackKey, RacingLine line, CarSpec spec, RaceWorldSettings s, string variant)
        => Hash(string.Join("|", trackKey, line.Length.ToString("F1", CultureInfo.InvariantCulture),
            spec.Model, variant, spec.TopSpeed, spec.DragCoefficient, spec.LateralGrip, spec.BrakeGrip, spec.ReferenceMass, spec.FuelCapacity,
            s.HumanErrors, s.LineErrors, s.Spins, s.GrassMoments, s.UseTrackHints, s.EdgeMargin, s.TyreWearScale, RaceWorld.DefaultStep));

    // still cache/raceai/ (the folder of the Race AI plugin this was before): existing calibrations stay valid
    private static string CalibrationDir(string trackKey) => Path.Join("cache", "raceai", trackKey);

    private static string CalibrationPath(string trackKey, CarSpec spec, string baseKey)
        => Path.Join(CalibrationDir(trackKey), $"{spec.Model}-{baseKey}-{BuildId}.json");

    private static StrengthCalibration? LoadCalibration(string path, CarSpec spec)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var saved = JsonSerializer.Deserialize<StrengthCalibration.Saved>(File.ReadAllText(path));
            return saved != null ? StrengthCalibration.Load(saved, spec) : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "BotDriver: calibration cache not readable");
            return null;
        }
    }

    /// <summary>For the server start: (calibration, final?). Never measures for long.</summary>
    public (StrengthCalibration Cal, bool Final) StartupCalibration(CarSpec spec, RaceWorldSettings settings, string variant)
    {
        string trackKey = TrackKey();
        string baseKey = CalibrationBaseKey(trackKey, _track!.Line, spec, settings, variant);
        if (LoadCalibration(CalibrationPath(trackKey, spec, baseKey), spec) is { } exact) return (exact, true);
        // the same car on the same track from an older plugin build: very close, good enough until the new one is measured
        try
        {
            var dir = CalibrationDir(trackKey);
            if (Directory.Exists(dir))
                foreach (var f in new DirectoryInfo(dir).GetFiles($"{spec.Model}-{baseKey}-*.json").OrderByDescending(f => f.LastWriteTimeUtc))
                    if (LoadCalibration(f.FullName, spec) is { } older) return (older, false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "BotDriver: calibration cache not readable");
        }
        // quick: coarse steps and fewer laps with mistakes (a few seconds), refined in the background
        return (StrengthCalibration.Measure(_track.Line, spec, settings, step: 0.05f, errorLaps: 2), false);
    }

    /// <summary>The final calibration (the live world's step), saved in the cache.</summary>
    private static StrengthCalibration FinalCalibration(string trackKey, RacingLine line, CarSpec spec, RaceWorldSettings settings, string variant,
        int threads, CancellationToken cancel)
    {
        string baseKey = CalibrationBaseKey(trackKey, line, spec, settings, variant);
        string path = CalibrationPath(trackKey, spec, baseKey);
        var cal = StrengthCalibration.Measure(line, spec, settings, threads: threads, cancel: cancel);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(cal.Save(spec)));
            // older builds' files of this car are no longer needed
            foreach (var f in new DirectoryInfo(Path.GetDirectoryName(path)!).GetFiles($"{spec.Model}-{baseKey}-*.json"))
                if (f.FullName != Path.GetFullPath(path)) f.Delete();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "BotDriver: calibration cache not writable");
        }
        return cal;
    }

    /// <summary>100 % reference (median best lap of the bot cars) and every bot's pace from its strength.</summary>
    public void ApplyCalibrations(RaceWorld world, bool log)
    {
        if (_config.AiStrengthReference == StrengthReference.Field && _calibrations.Count > 0)
        {
            var bests = _calibrations.Values.Select(c => c.BestLap).OrderBy(x => x).ToList();
            _referenceBestLap = bests[bests.Count / 2];
            if (log)
                Log.Information("BotDriver: 100 % = {Lap} (median best lap of the bot cars); e.g. 95 % = {Lap95}, 90 % = {Lap90}",
                    FormatLap(_referenceBestLap.Value), FormatLap(_referenceBestLap.Value / 0.95f), FormatLap(_referenceBestLap.Value / 0.9f));
        }
        foreach (var bot in world.Bots)
            if (_calibrations.TryGetValue(bot.Car, out var cal))
                ApplyStrength(bot, bot.Driver.Level, cal);
    }

    public void StartBackgroundCalibration(RaceWorldSettings settings)
    {
        _calibrationSettings = settings;
        var cancel = _background.Token;
        var thread = new Thread(() =>
        {
            try
            {
                RefineProvisional(cancel);
                PrecalibrateRotation(cancel);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warning(ex, "BotDriver: background calibration failed");
            }
            finally
            {
                CalibrationStatus = null;
            }
        })
        {
            Name = "BotDriverCalibration",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        thread.Start();
    }

    /// <summary>Cores for background work: all but one (the main loop keeps one for itself).</summary>
    private static int BackgroundThreads => Math.Max(1, Environment.ProcessorCount - 1);

    private void RefineProvisional(CancellationToken cancel)
    {
        if (_provisional.Count == 0 || _track == null || _calibrationSettings == null) return;
        var sw = Stopwatch.StartNew();
        string trackKey = TrackKey();
        int done = 0;
        foreach (var (spec, variant) in _provisional)
        {
            cancel.ThrowIfCancellationRequested();
            CalibrationStatus = $"Kalibrierung: noch {_provisional.Count - done} Auto(s) hier";
            var cal = FinalCalibration(trackKey, _track.Line, spec, _calibrationSettings, variant, BackgroundThreads, cancel);
            lock (_lock)
            {
                _calibrations[spec] = cal;
                if (_world != null) ApplyCalibrations(_world, log: false);
            }
            done++;
            Log.Information("BotDriver: {Model} calibrated in the background: 100 % = {Best}", spec.Model, FormatLap(cal.BestLap));
        }
        Log.Information("BotDriver: final calibration of {Count} car(s) done in {Seconds:F0} s", _provisional.Count, sw.Elapsed.TotalSeconds);
        lock (_lock)
            if (_world != null) ApplyCalibrations(_world, log: true);
    }

    /// <summary>The other tracks of the rotation: their cars calibrated now, so the next start finds them in the cache.</summary>
    private void PrecalibrateRotation(CancellationToken cancel)
    {
        if (!File.Exists("rotation.yml") || _calibrationSettings == null) return;
        TrackRotationConfiguration cfg;
        try
        {
            cfg = new DeserializerBuilder().IgnoreUnmatchedProperties().Build()
                .Deserialize<TrackRotationConfiguration>(File.ReadAllText("rotation.yml")) ?? new();
        }
        catch
        {
            return;
        }
        if (!cfg.Enabled) return;
        string current = string.IsNullOrEmpty(_race.ServerConfig.Preset) ? "default" : _race.ServerConfig.Preset;
        var sw = Stopwatch.StartNew();
        int measured = 0;
        // every track with its class (own one in the list, else the configured one)
        var presets = cfg.Tracks.Select(t =>
        {
            var (track, cls) = PresetOverlay.Split(t);
            return PresetOverlay.Join(track == "" ? "default" : track, cls ?? ClassCatalog.Get(cfg.Class)?.Key);
        });
        foreach (var preset in presets.Distinct().Where(t => t != current))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var layers = PresetOverlay.Layers(preset == "default" ? null : preset);
                var ini = PresetOverlay.MergeServerCfg(preset == "default" ? null : preset);
                string? track = ini["SERVER"]["TRACK"];
                string layout = ini["SERVER"]["CONFIG_TRACK"] ?? "";
                if (string.IsNullOrEmpty(track)) continue;
                track = CSPTrackOptions.Parse(track).Track;
                // that preset's grid file (start line, pit boxes), not the running one's
                var yml = PresetOverlay.ReadYaml(layers, "plugin_bot_driver_cfg.yml") ?? PresetOverlay.ReadYaml(layers, "plugin_race_ai_cfg.yml") ?? "";
                var grid = System.Text.RegularExpressions.Regex.Match(yml, @"(?m)^GridFile:[ \t]*['""]?([^'""\r\n]*)");
                var td = TrackData.Load(track, layout, _config, grid.Success ? grid.Groups[1].Value.Trim() : "", layers);
                string trackKey = TrackKeyFor(track, layout);
                var settings = CopySettings(_calibrationSettings, td.StartLineS);

                var ep = PresetOverlay.Resolve(layers, "entry_list.ini");
                if (!File.Exists(ep)) continue;
                var entries = IniFile.Load(ep);
                var cars = entries.Sections.Where(s => s.StartsWith("CAR_", StringComparison.OrdinalIgnoreCase))
                    .Select(s => (Model: entries.Get(s, "MODEL") ?? "", Ballast: entries.GetFloat(s, "BALLAST", 0), Restrictor: (int)entries.GetFloat(s, "RESTRICTOR", 0)))
                    .Where(c => c.Model != "").Distinct().ToList();
                foreach (var (model, ballast, restrictor) in cars)
                {
                    cancel.ThrowIfCancellationRequested();
                    var root = _carRoots.FirstOrDefault(r => Directory.Exists(Path.Join(r, model)));
                    if (root == null) continue;
                    var spec = CarDataLoader.LoadForTrack(root, model, td.Line, ballast, restrictor, _ => { });
                    string variant = $"{ballast}/{restrictor}";
                    string baseKey = CalibrationBaseKey(trackKey, td.Line, spec, settings, variant);
                    if (File.Exists(CalibrationPath(trackKey, spec, baseKey))) continue;
                    CalibrationStatus = $"Kalibrierung: {preset} wird vorbereitet";
                    FinalCalibration(trackKey, td.Line, spec, settings, variant, BackgroundThreads, cancel);
                    measured++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "BotDriver: could not prepare rotation track {Preset}", preset);
            }
        }
        if (measured > 0)
            Log.Information("BotDriver: {Count} car(s) on the other rotation tracks calibrated in advance ({Seconds:F0} s)", measured, sw.Elapsed.TotalSeconds);
    }

    private static RaceWorldSettings CopySettings(RaceWorldSettings s, float startLineS) => new()
    {
        StartLineS = startLineS,
        UseTrackHints = s.UseTrackHints,
        EdgeMargin = s.EdgeMargin,
        SideMargin = s.SideMargin,
        PlayerSideMargin = s.PlayerSideMargin,
        PlayerOverlap = s.PlayerOverlap,
        HumanErrors = s.HumanErrors,
        Spins = s.Spins,
        GrassMoments = s.GrassMoments,
        LineErrors = s.LineErrors,
        GrassAllowance = s.GrassAllowance,
        TyreWearScale = s.TyreWearScale
    };

    private static string TrackKeyFor(string track, string layout)
    {
        int slash = track.LastIndexOf('/');
        if (slash >= 0) track = track[(slash + 1)..];
        return string.IsNullOrEmpty(layout) ? track : $"{track}-{layout}";
    }

    // ------------------------------------------------------------------ strength, personalities, switches


    public Personality PickPersonality(string? name)
    {
        if (!_config.UsePersonalities) return Personality.Balanced;
        if (_personalities.Count == 0)
        {
            var list = _config.Personalities.Count > 0
                ? _config.Personalities.Select(p => (p.ToPersonality(), p.Share)).ToList()
                : Personality.Defaults();
            _personalities.AddRange(list);
        }
        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = _personalities.FirstOrDefault(p => p.Personality.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match.Personality != null) return match.Personality;
            Log.Warning("BotDriver: unknown personality {Name}", name);
        }
        float total = _personalities.Sum(p => MathF.Max(0, p.Share));
        if (total <= 0) return Personality.Balanced;
        float r = (float)_race.Rng.NextDouble() * total;
        foreach (var (p, share) in _personalities)
        {
            r -= MathF.Max(0, share);
            if (r <= 0) return p;
        }
        return _personalities[^1].Personality;
    }

    public void SetStrength(float strength, float? spread = null)
    {
        lock (_lock)
        {
            // the bots of the entry list; a car taken over from a player keeps his pace
            var slots = _slots.Where(s => s.TakeoverGuid == null).ToList();
            var values = StrengthCalibration.Distribute(slots.Count, strength, spread ?? _config.AiStrengthSpread,
                _config.AiStrengthDistribution == StrengthDistribution.Random, _race.Rng);
            for (int i = 0; i < slots.Count; i++)
            {
                var bot = slots[i].Bot;
                if (!_calibrations.TryGetValue(bot.Car, out var cal)) continue;
                ApplyStrength(bot, values[i], cal);
            }
        }
    }

    /// <summary>Strength in % -> consistency, human error level and the pace that gives the target lap time including the mistakes.</summary>
    public void ApplyStrength(RaceBot bot, float strength, StrengthCalibration cal)
    {
        if (bot.Clone is { } clone)
        {
            ApplyClone(bot, clone);
            return;
        }
        var d = DriverProfile.FromStrength(strength, 0, 0);
        bot.Driver.Level = strength;
        bot.Driver.Consistency = d.Consistency;
        bot.Driver.Errors = _config.HumanErrors ? DriverProfile.ErrorsFor(strength, _config.HumanErrorsBelow, _config.HumanErrorsFull) : 0;
        bot.Driver.Pace = cal.PaceFor(strength, _referenceBestLap, bot.Driver.Errors);
    }

    /// <summary>A clone drives the player's speeds (Pace 1 = no extra slowing) and makes mistakes as often as his lap times vary.</summary>
    public void ApplyClone(RaceBot bot, CloneProfile clone)
    {
        bot.Driver.Pace = 1;
        bot.Driver.Consistency = 0.97f;
        float spread = clone.AverageLap > 0 ? clone.LapSpread / clone.AverageLap : 0;
        bot.Driver.Errors = _config.HumanErrors ? Math.Clamp(spread * 25, 0.05f, 0.6f) : 0;
        float reference = _referenceBestLap ?? (_calibrations.TryGetValue(bot.Car, out var cal) ? cal.BestLap : clone.AverageLap);
        bot.Driver.Level = MathF.Round(reference / MathF.Max(1, clone.AverageLap / bot.ClonePace) * 1000) / 10;
    }

    public bool SetFeature(string feature, bool on)
    {
        lock (_lock)
        {
            var s = _world?.Settings;
            if (s == null) return false;
            switch (feature)
            {
                case "errors" or "humanerrors":
                    _config.HumanErrors = on;
                    _configWriter.Set(nameof(BotDriverConfiguration.HumanErrors), on);
                    s.HumanErrors = on;
                    foreach (var slot in _slots)
                        if (_calibrations.TryGetValue(slot.Bot.Car, out var cal)) ApplyStrength(slot.Bot, slot.Bot.Driver.Level, cal);
                    break;
                case "spins": s.Spins = on; _config.Spins = on; _configWriter.Set("Spins", on); break;
                case "grass": s.GrassMoments = on; _config.GrassMoments = on; _configWriter.Set("GrassMoments", on); break;
                case "lines": s.LineErrors = on; _config.LineErrors = on; _configWriter.Set("LineErrors", on); break;
                case "contacts": s.BotContacts = on; _config.BotContacts = on; _configWriter.Set("BotContacts", on); break;
                case "damage": s.Damage = on && s.DamageRate > 0; break;
                case "blueflags": _config.BlueFlags = on; s.BlueFlags = on && _race.SessionType == AssettoServer.Shared.Model.SessionType.Race; _configWriter.Set("BlueFlags", on); break;
                case "yellowflags": s.YellowFlags = on; _config.YellowFlags = on; _configWriter.Set("YellowFlags", on); break;
                case "flash": _config.FlashLights = on; _configWriter.Set("FlashLights", on); break;
                case "raincaution": _config.RainCaution = on; s.RainCaution = on; _configWriter.Set("RainCaution", on); break;
                case "highbeams": _config.HighBeams = on; _configWriter.Set("HighBeams", on); break;
                case "realstart": _config.RealisticStart = on; s.RealisticStart = on; _configWriter.Set("RealisticStart", on); break;
                case "personallines":
                    _config.PersonalLines = on;
                    s.PersonalLines = on;
                    _configWriter.Set("PersonalLines", on);
                    foreach (var slot in _slots) _world!.ResetStyle(slot.Bot);
                    break;
                default: return false;
            }
            return true;
        }
    }

    /// <summary>Rubber band strength 0-100 % (0 = off), saved in the configuration.</summary>
    public string SetRubberBand(float percent)
    {
        lock (_lock)
        {
            percent = Math.Clamp(percent, 0, 100);
            _config.RubberBanding = percent;
            _configWriter.Set("RubberBanding", percent);
            if (_world != null) _world.Settings.RubberBand = percent / 100f;
            return percent <= 0 ? T("Rubber band off.", "Gummiband aus.")
                : T($"Rubber band {percent:F0} %: bots ahead of you up to {_config.RubberBandingAhead * percent / 100:F1} % slower, behind you up to {_config.RubberBandingBehind * percent / 100:F1} % faster.",
                    $"Gummiband {percent:F0} %: Bots vor dir bis {_config.RubberBandingAhead * percent / 100:F1} % langsamer, hinter dir bis {_config.RubberBandingBehind * percent / 100:F1} % schneller.");
        }
    }


    // ------------------------------------------------------------------ for the other parts of the plugin

    public IReadOnlyList<string> PersonalityNames => _personalities.Select(p => p.Personality.Name).DefaultIfEmpty("Balanced").ToList();
    public bool HasCalibration(CarSpec car) => _calibrations.ContainsKey(car);
    public StrengthCalibration Calibration(CarSpec car) => _calibrations[car];
    public void SetCalibration(CarSpec car, StrengthCalibration cal, bool provisional, string variant)
    {
        _calibrations[car] = cal;
        if (provisional) _provisional.Add((car, variant));
    }
    public int ProvisionalCount => _provisional.Count;
    public bool AnyCalibrations => _calibrations.Count > 0;

    /// <summary>The lap time a bot aims for at its strength, null without a calibration of its car.</summary>
    public float? TargetLap(RaceBot bot)
        => _calibrations.TryGetValue(bot.Car, out var cal) ? cal.LapTimeFor(bot.Driver.Level, _referenceBestLap) : null;

    public void Stop() => _background.Cancel();

    // ------------------------------------------------------------------ admin page (/api/bots)

    public Dictionary<string, bool> FeatureStates()
    {
        var s = _world?.Settings;
        return new Dictionary<string, bool>
        {
            ["errors"] = s?.HumanErrors ?? false,
            ["spins"] = s?.Spins ?? false,
            ["grass"] = s?.GrassMoments ?? false,
            ["takeover"] = _config.TakeOverDisconnectedPlayers,
            ["lines"] = s?.LineErrors ?? false,
            ["contacts"] = s?.BotContacts ?? false,
            ["damage"] = s?.Damage ?? false,
            ["blueflags"] = _config.BlueFlags,
            ["yellowflags"] = s?.YellowFlags ?? false,
            ["flash"] = _config.FlashLights,
            ["highbeams"] = _config.HighBeams,
            ["raincaution"] = s?.RainCaution ?? false,
            ["yellowchat"] = _config.YellowFlagChat,
            ["overtakechat"] = _config.AnnounceOvertakes,
            ["personallines"] = s?.PersonalLines ?? _config.PersonalLines,
            ["realstart"] = s?.RealisticStart ?? _config.RealisticStart,
        };
    }

    public bool SetDashboardFeature(string feature, bool on)
    {
        lock (_lock)
        {
            switch (feature)
            {
                case "yellowchat": _config.YellowFlagChat = on; _configWriter.Set("YellowFlagChat", on); return true;
                case "overtakechat": _config.AnnounceOvertakes = on; _configWriter.Set("AnnounceOvertakes", on); return true;
                case "takeover": _config.TakeOverDisconnectedPlayers = on; _configWriter.Set("TakeOverDisconnectedPlayers", on); return true;
            }
        }
        return SetFeature(feature, on);
    }

    /// <summary>Changes one bot. Null values stay as they are.</summary>
    public bool UpdateBot(int id, float? strength, float? aggression, string? personality, bool pit)
    {
        lock (_lock)
        {
            var world = _world;
            if (world == null || !_race.SlotsBySessionId.TryGetValue((byte)id, out var slot) || !slot.Active) return false;
            var bot = slot.Bot;
            if (strength is { } st && _calibrations.TryGetValue(bot.Car, out var cal))
                ApplyStrength(bot, Math.Clamp(st, 50, 110), cal);
            if (aggression is { } ag) bot.Driver.Aggression = Math.Clamp(ag / 100f, 0, 1);
            if (!string.IsNullOrWhiteSpace(personality))
            {
                if (_personalities.Count == 0) PickPersonality(null);
                var p = _personalities.FirstOrDefault(x => x.Personality.Name.Equals(personality, StringComparison.OrdinalIgnoreCase)).Personality;
                if (p != null) { bot.Driver.Personality = p; world.ResetStyle(bot); }
            }
            if (pit) world.RequestPitStop(bot, "admin");
            Log.Information("BotDriver: dashboard changed {Name}: strength {Strength:F1} %, aggression {Aggression:F0}, {Personality}{Pit}",
                bot.Name, bot.Driver.Level, bot.Driver.Aggression * 100, bot.Driver.Personality.Name, pit ? ", to the pits" : "");
            return true;
        }
    }

    public float CurrentSpread => _config.AiStrengthSpread;

    public void SetGlobalStrength(float strength, float spread)
    {
        _config.AiStrength = Math.Clamp(strength, 50, 110);
        _config.AiStrengthSpread = Math.Clamp(spread, 0, 30);
        SetStrength(_config.AiStrength, _config.AiStrengthSpread);
        _configWriter.Set("AiStrength", _config.AiStrength);
        _configWriter.Set("AiStrengthSpread", _config.AiStrengthSpread);
    }

    /// <summary>The field's aggression: every bot shifted by the change, so the spread and the personalities stay.</summary>
    public void SetGlobalAggression(float aggression)
    {
        lock (_lock)
        {
            aggression = Math.Clamp(aggression, 0, 100);
            float delta = (aggression - _config.AiAggression) / 100f;
            _config.AiAggression = aggression;
            foreach (var slot in _slots)
                slot.Bot.Driver.Aggression = Math.Clamp(slot.Bot.Driver.Aggression + delta, 0, 1);
        }
        _configWriter.Set("AiAggression", _config.AiAggression);
    }
}
