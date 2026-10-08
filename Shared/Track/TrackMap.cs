using System.Numerics;
using System.Text.Json;

namespace TrackGeometry;

/// <summary>
/// The parts of a track every plugin may need: the AI line (fast_lane.ai) as the track's centre line, start/finish, timing sectors,
/// grid and pit boxes, the pit lane. Shared source: each plugin compiles its own copy (plugins can't share assemblies).
/// </summary>
public sealed class TrackMap
{
    public required RacingLine Line { get; init; }
    public required TrackInfo Info { get; init; }
    /// <summary>Position of the start/finish line on <see cref="Line"/>.</summary>
    public float StartLineS { get; init; }
    public PitLane? PitLane { get; init; }
    /// <summary>Folder of the track (content/tracks/&lt;track&gt;) the line was found in.</summary>
    public required string TrackRoot { get; init; }
    /// <summary>Path of the fast_lane.ai that was loaded.</summary>
    public required string FastLanePath { get; init; }
    /// <summary>Distances from the start line where sectors 2, 3 ... begin.</summary>
    public IReadOnlyList<float> SectorSplits { get; private set; } = [];

    /// <summary>"trialmountain" or "ks_nordschleife-nordschleife": the track's name for statistics and files.</summary>
    public static string Key(string track, string? layout)
    {
        int slash = track.LastIndexOf('/');
        if (slash >= 0) track = track[(slash + 1)..];
        return string.IsNullOrEmpty(layout) ? track : $"{track}-{layout}";
    }

    /// <summary>"&lt;track key&gt;-grid.json" (see <see cref="Key"/>) from the top configuration layer that has one, else null.</summary>
    public static string? FindGridFile(IEnumerable<string> layers, string key)
        => layers.Reverse().Select(l => Path.Join(l, key + "-grid.json")).FirstOrDefault(File.Exists);

    /// <summary>Content folders to search: the server's own content folder first, then the game installation.</summary>
    public static List<string> ContentRoots(string? assettoCorsaPath, Action<string>? warn = null)
    {
        var roots = new List<string>();
        if (Directory.Exists("content")) roots.Add("content");
        if (Directory.Exists("content~tmp")) roots.Add("content~tmp");
        if (!string.IsNullOrWhiteSpace(assettoCorsaPath))
        {
            var p = Path.Join(assettoCorsaPath, "content");
            if (Directory.Exists(p)) roots.Add(p);
            else warn?.Invoke($"AssettoCorsaPath {assettoCorsaPath} has no content folder");
        }
        return roots;
    }

    /// <summary>
    /// Loads the track. <paramref name="gridFile"/>: JSON with Grid/Pits/StartFinish/Sectors instead of reading the kn5 files.
    /// Throws <see cref="FileNotFoundException"/> when no fast_lane.ai is found.
    /// </summary>
    public static TrackMap Load(string track, string? layout, IReadOnlyList<string> roots, string? gridFile = null,
        Action<string>? info = null, Action<string>? warn = null)
    {
        layout ??= "";
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
                                            "Copy the track's ai folder into the server content folder or set AssettoCorsaPath.");

        info?.Invoke($"loading racing line {fastLane}");
        var line = new RacingLine(FastLaneFile.Read(fastLane));

        // layout data (ai_hints.ini, sections.ini) from any root that has it
        TrackInfo trackInfo = new();
        foreach (var root in roots)
        {
            var dataDir = string.IsNullOrEmpty(layout) ? Path.Join(root, "tracks", track, "data") : Path.Join(root, "tracks", track, layout, "data");
            if (File.Exists(Path.Join(dataDir, "ai_hints.ini")) || File.Exists(Path.Join(dataDir, "sections.ini")))
            {
                trackInfo = TrackInfo.LoadLayoutData(dataDir);
                break;
            }
        }

        // grid, pit boxes, start line
        if (!string.IsNullOrEmpty(gridFile) && File.Exists(gridFile))
        {
            LoadGridFile(trackInfo, gridFile);
            info?.Invoke($"grid loaded from {gridFile}");
        }
        else
        {
            foreach (var root in roots)
            {
                var dir = Path.Join(root, "tracks", track);
                if (!Directory.Exists(dir) || Directory.GetFiles(dir, "*.kn5").Length == 0) continue;
                try
                {
                    trackInfo.LoadSpotsFromKn5(dir, layout);
                    info?.Invoke($"{trackInfo.StartGrid.Count} start positions and {trackInfo.PitBoxes.Count} pit boxes read from the kn5 files in {dir}");
                    break;
                }
                catch (Exception ex)
                {
                    warn?.Invoke($"could not read grid positions from {dir}: {ex.Message}");
                }
            }
        }

        float startLineS = trackInfo.StartFinish is { } sf ? line.Project(sf).S : 0;

        PitLane? pitLane = null;
        var pitPath = Path.Join(Path.GetDirectoryName(fastLane)!, "pit_lane.ai");
        if (File.Exists(pitPath))
        {
            try
            {
                pitLane = new PitLane(FastLaneFile.Read(pitPath), line);
                info?.Invoke($"pit lane {pitLane.Length:F0} m, speed limit zone {pitLane.LimiterStart:F0}-{pitLane.LimiterEnd:F0} m");
            }
            catch (Exception ex)
            {
                warn?.Invoke($"could not read {pitPath}: {ex.Message}");
            }
        }

        var map = new TrackMap { Line = line, Info = trackInfo, StartLineS = startLineS, PitLane = pitLane, TrackRoot = trackRoot, FastLanePath = fastLane };
        map.UpdateSectors();
        return map;
    }

    /// <summary>Recomputes <see cref="SectorSplits"/> from <see cref="TrackInfo.SectorLines"/>.</summary>
    public void UpdateSectors()
        => SectorSplits = Info.SectorLines.Select(p => Line.WrapS(Line.Project(p).S - StartLineS)).OrderBy(x => x).ToList();

    /// <summary>Distance from the start line (0 .. length) of a position on the line.</summary>
    public float FromStart(float s) => Line.WrapS(s - StartLineS);

    /// <summary>Timing sector (1-based) of a position on the line.</summary>
    public int SectorAt(float s)
    {
        float fromStart = FromStart(s);
        return 1 + SectorSplits.Count(x => x < fromStart);
    }

    /// <summary>Where a car is: share of the lap from the start line (0..1) and its sector. <paramref name="hint"/>: last line index (-1 = search all).</summary>
    public (float LapFraction, int Sector, int Index) Locate(Vector3 position, int hint = -1)
    {
        var pr = Line.Project(position, hint);
        return (FromStart(pr.S) / Line.Length, SectorAt(pr.S), pr.Index);
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
        if (root.TryGetProperty("Sectors", out var sectors) && sectors.ValueKind == JsonValueKind.Array)
            foreach (var p in sectors.EnumerateArray()) info.SectorLines.Add(V(p, 0));
    }
}
