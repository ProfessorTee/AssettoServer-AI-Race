using System.Numerics;
using System.Text.Json;
using RaceAiPlugin.Core;
using Serilog;

namespace RaceAiPlugin;

/// <summary>Finds and loads the racing line, layout data and grid positions of the server's track.</summary>
public sealed class TrackData
{
    public required RacingLine Line { get; init; }
    public required TrackInfo Info { get; init; }
    public float StartLineS { get; init; }

    public static TrackData Load(string track, string layout, RaceAiConfiguration config)
    {
        var roots = ContentRoots(config);

        string? fastLane = null;
        string? trackRoot = null;
        foreach (var root in roots)
        {
            var dir = Path.Join(root, "tracks", track);
            var candidates = new[]
            {
                string.IsNullOrEmpty(layout) ? null : Path.Join(dir, layout, "ai", "fast_lane.ai"),
                Path.Join(dir, "ai", "fast_lane.ai")
            };
            fastLane = candidates.FirstOrDefault(p => p != null && File.Exists(p));
            if (fastLane != null)
            {
                trackRoot = dir;
                break;
            }
        }

        if (fastLane == null || trackRoot == null)
            throw new FileNotFoundException($"fast_lane.ai for {track}/{layout} not found. Searched: {string.Join(", ", roots)}. " +
                                            "Copy the track's ai folder into the server content folder or set AssettoCorsaPath in the plugin configuration.");

        Log.Information("Race AI: loading racing line {Path}", fastLane);
        var line = new RacingLine(FastLaneFile.Read(fastLane));

        // layout data (ai_hints.ini, sections.ini) from any root that has it
        TrackInfo info = new();
        foreach (var root in roots)
        {
            var dataDir = string.IsNullOrEmpty(layout) ? Path.Join(root, "tracks", track, "data") : Path.Join(root, "tracks", track, layout, "data");
            if (File.Exists(Path.Join(dataDir, "ai_hints.ini")) || File.Exists(Path.Join(dataDir, "sections.ini")))
            {
                info = TrackInfo.LoadLayoutData(dataDir);
                break;
            }
        }
        if (config.UseTrackHints)
            line.ApplyHints(info.SpeedHints, info.MaxSpeedsKmh);

        // grid, pit boxes, start line
        if (!string.IsNullOrEmpty(config.GridFile) && File.Exists(config.GridFile))
        {
            LoadGridFile(info, config.GridFile);
            Log.Information("Race AI: grid loaded from {GridFile}", config.GridFile);
        }
        else
        {
            foreach (var root in roots)
            {
                var dir = Path.Join(root, "tracks", track);
                if (!Directory.Exists(dir) || Directory.GetFiles(dir, "*.kn5").Length == 0) continue;
                try
                {
                    info.LoadSpotsFromKn5(dir, layout);
                    Log.Information("Race AI: {Grid} start positions and {Pits} pit boxes read from the kn5 files in {Dir}", info.StartGrid.Count, info.PitBoxes.Count, dir);
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Race AI: could not read grid positions from {Dir}", dir);
                }
            }
        }

        if (info.StartGrid.Count == 0)
            Log.Warning("Race AI: no start grid found (AC_START_x). Bots will use an estimated grid behind the start line. Set AssettoCorsaPath or GridFile");

        float startLineS = info.StartFinish is { } sf ? line.Project(sf).S : 0;
        Log.Information("Race AI: racing line {Length:F0} m, start/finish at {Start:F0} m, {Hints} AI hints, {Sections} sections",
            line.Length, startLineS, info.SpeedHints.Count + info.MaxSpeedsKmh.Count, info.Sections.Count);

        return new TrackData { Line = line, Info = info, StartLineS = startLineS };
    }

    /// <summary>Content folders to search: the server's own content folder first, then the game installation.</summary>
    public static List<string> ContentRoots(RaceAiConfiguration config)
    {
        var roots = new List<string>();
        if (Directory.Exists("content")) roots.Add("content");
        if (Directory.Exists("content~tmp")) roots.Add("content~tmp");
        if (!string.IsNullOrWhiteSpace(config.AssettoCorsaPath))
        {
            var p = Path.Join(config.AssettoCorsaPath, "content");
            if (Directory.Exists(p)) roots.Add(p);
            else Log.Warning("Race AI: AssettoCorsaPath {Path} has no content folder", config.AssettoCorsaPath);
        }
        return roots;
    }

    private static void LoadGridFile(TrackInfo info, string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        static Vector3 V(JsonElement a, int o) => new(a[o].GetSingle(), a[o + 1].GetSingle(), a[o + 2].GetSingle());

        var dummies = new List<Kn5Reader.Kn5Dummy>();
        if (root.TryGetProperty("Grid", out var grid))
        {
            int i = 0;
            foreach (var g in grid.EnumerateArray())
                dummies.Add(new Kn5Reader.Kn5Dummy($"AC_START_{i++}", V(g, 0), g.GetArrayLength() >= 6 ? V(g, 3) : Vector3.UnitZ));
        }
        if (root.TryGetProperty("Pits", out var pits))
        {
            int i = 0;
            foreach (var g in pits.EnumerateArray())
                dummies.Add(new Kn5Reader.Kn5Dummy($"AC_PIT_{i++}", V(g, 0), g.GetArrayLength() >= 6 ? V(g, 3) : Vector3.UnitZ));
        }
        info.AddSpots(dummies);
        if (root.TryGetProperty("StartFinish", out var sf) && sf.ValueKind == JsonValueKind.Array)
            info.StartFinish = V(sf, 0);
    }
}
