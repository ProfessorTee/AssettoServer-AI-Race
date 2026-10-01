using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssettoServer.Server.Configuration;
using RaceAiPlugin.Core;
using Serilog;
using YamlDotNet.Serialization;

namespace RaceAiPlugin;

/// <summary>
/// Calibration (strength in % -> driver pace) without making the server wait:
/// at start each car gets the final calibration from the cache if there is one, otherwise a provisional one
/// (an older build's from the cache, or a quick coarse measurement of a few seconds). The server starts right away;
/// the final calibrations are measured in the background on all cores and swapped in, then the other tracks of the
/// rotation are calibrated too, so a track change never has to wait for it.
/// </summary>
public sealed partial class RaceAiService
{
    private readonly CancellationTokenSource _background = new();
    private readonly List<(CarSpec Spec, string Variant)> _provisional = [];
    private RaceWorldSettings? _calibrationSettings;

    /// <summary>Calibration jobs in the background (for the dashboard): what's running, how many are left.</summary>
    public string? CalibrationStatus { get; private set; }

    private static string BuildId => typeof(RaceAiService).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>Cache key without the plugin build: same track, car and driving settings.</summary>
    private static string CalibrationBaseKey(string trackKey, RacingLine line, CarSpec spec, RaceWorldSettings s, string variant)
        => Hash(string.Join("|", trackKey, line.Length.ToString("F1", CultureInfo.InvariantCulture),
            spec.Model, variant, spec.TopSpeed, spec.DragCoefficient, spec.LateralGrip, spec.BrakeGrip, spec.ReferenceMass, spec.FuelCapacity,
            s.HumanErrors, s.LineErrors, s.Spins, s.GrassMoments, s.UseTrackHints, s.EdgeMargin, s.TyreWearScale, RaceWorld.DefaultStep));

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
            Log.Debug(ex, "Race AI: calibration cache not readable");
            return null;
        }
    }

    /// <summary>For the server start: (calibration, final?). Never measures for long.</summary>
    private (StrengthCalibration Cal, bool Final) StartupCalibration(CarSpec spec, RaceWorldSettings settings, string variant)
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
            Log.Debug(ex, "Race AI: calibration cache not readable");
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
            Log.Debug(ex, "Race AI: calibration cache not writable");
        }
        return cal;
    }

    /// <summary>100 % reference (median best lap of the bot cars) and every bot's pace from its strength.</summary>
    private void ApplyCalibrations(RaceWorld world, bool log)
    {
        if (_config.AiStrengthReference == StrengthReference.Field && _calibrations.Count > 0)
        {
            var bests = _calibrations.Values.Select(c => c.BestLap).OrderBy(x => x).ToList();
            _referenceBestLap = bests[bests.Count / 2];
            if (log)
                Log.Information("Race AI: 100 % = {Lap} (median best lap of the bot cars); e.g. 95 % = {Lap95}, 90 % = {Lap90}",
                    FormatLap(_referenceBestLap.Value), FormatLap(_referenceBestLap.Value / 0.95f), FormatLap(_referenceBestLap.Value / 0.9f));
        }
        foreach (var bot in world.Bots)
            if (_calibrations.TryGetValue(bot.Car, out var cal))
                ApplyStrength(bot, bot.Driver.Level, cal);
    }

    private void StartBackgroundCalibration(RaceWorldSettings settings)
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
                Log.Warning(ex, "Race AI: background calibration failed");
            }
            finally
            {
                CalibrationStatus = null;
            }
        })
        {
            Name = "RaceAiCalibration",
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
            Log.Information("Race AI: {Model} calibrated in the background: 100 % = {Best}", spec.Model, FormatLap(cal.BestLap));
        }
        Log.Information("Race AI: final calibration of {Count} car(s) done in {Seconds:F0} s", _provisional.Count, sw.Elapsed.TotalSeconds);
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
        string current = string.IsNullOrEmpty(_serverConfig.Preset) ? "default" : _serverConfig.Preset;
        var mainIni = File.Exists(Path.Join("cfg", "server_cfg.ini")) ? IniFile.Load(Path.Join("cfg", "server_cfg.ini")) : null;
        var sw = Stopwatch.StartNew();
        int measured = 0;
        foreach (var preset in cfg.Tracks.Distinct().Where(t => t != current))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                string dir = preset == "default" ? "cfg" : Path.Join("presets", preset);
                string? Read(string file) => File.Exists(Path.Join(dir, file)) ? Path.Join(dir, file)
                    : File.Exists(Path.Join("cfg", file)) ? Path.Join("cfg", file) : null;
                var ini = Read("server_cfg.ini") is { } sp ? IniFile.Load(sp) : null;
                string? track = ini?.Get("SERVER", "TRACK") ?? mainIni?.Get("SERVER", "TRACK");
                string layout = ini?.Get("SERVER", "CONFIG_TRACK") ?? (ini?.Get("SERVER", "TRACK") != null ? "" : mainIni?.Get("SERVER", "CONFIG_TRACK") ?? "");
                if (string.IsNullOrEmpty(track)) continue;
                track = CSPTrackOptions.Parse(track).Track;
                var td = TrackData.Load(track, layout, _config);
                string trackKey = TrackKeyFor(track, layout);
                var settings = CopySettings(_calibrationSettings, td.StartLineS);

                var entries = Read("entry_list.ini") is { } ep ? IniFile.Load(ep) : null;
                if (entries == null) continue;
                var cars = entries.Sections.Where(s => s.StartsWith("CAR_", StringComparison.OrdinalIgnoreCase))
                    .Select(s => (Model: entries.Get(s, "MODEL") ?? "", Ballast: entries.GetFloat(s, "BALLAST", 0), Restrictor: (int)entries.GetFloat(s, "RESTRICTOR", 0)))
                    .Where(c => c.Model != "").Distinct().ToList();
                foreach (var (model, ballast, restrictor) in cars)
                {
                    cancel.ThrowIfCancellationRequested();
                    var root = _carRoots.FirstOrDefault(r => Directory.Exists(Path.Join(r, model)));
                    if (root == null) continue;
                    var spec = CarDataLoader.Load(root, model, ballast, restrictor, _ => { });
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
                Log.Debug(ex, "Race AI: could not prepare rotation track {Preset}", preset);
            }
        }
        if (measured > 0)
            Log.Information("Race AI: {Count} car(s) on the other rotation tracks calibrated in advance ({Seconds:F0} s)", measured, sw.Elapsed.TotalSeconds);
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
}
