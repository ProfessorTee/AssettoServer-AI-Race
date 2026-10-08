using TrackGeometry;
using System.Numerics;
using System.Text.Json;
using RaceAiPlugin.Core;
using Serilog;

namespace RaceAiPlugin;

/// <summary>The track for the bots: the shared <see cref="TrackMap"/> plus what only driving needs (road heights, surfaces, ideal line, AI hints).</summary>
public sealed class TrackData
{
    public required TrackMap Map { get; init; }
    public RacingLine Line => Map.Line;
    public TrackInfo Info => Map.Info;
    public float StartLineS => Map.StartLineS;
    public PitLane? PitLane => Map.PitLane;
    /// <summary>Real road height beside the line (from the track's physics meshes), null when the kn5 files aren't on the server.</summary>
    public LineHeights? Heights { get; init; }
    /// <summary>Grass, gravel, sand ... beside the track with their grip and damping (surfaces.ini), null without kn5 files.</summary>
    public RoadSurface? OffTrack { get; init; }
    /// <summary>data/ideal_line.ai as an offset from the AI line, null when the track has none (or a copy of the AI line).</summary>
    public IdealLine? Ideal { get; init; }

    /// <param name="gridFile">Grid file of another preset than the running one (null = <paramref name="config"/>'s).</param>
    public static TrackData Load(string track, string layout, RaceAiConfiguration config, string? gridFile = null)
    {
        gridFile ??= config.GridFile;
        var map = TrackMap.Load(track, layout, ContentRoots(config), gridFile,
            m => Log.Information("Race AI: {Message}", m), m => Log.Warning("Race AI: {Message}", m));
        var line = map.Line;
        string fastLane = map.FastLanePath, trackRoot = map.TrackRoot;

        IdealLine? ideal = null;
        try
        {
            var dataDir = Path.GetDirectoryName(Path.GetDirectoryName(fastLane)!)!;
            ideal = IdealLine.Load(Path.Join(dataDir, "data", "ideal_line.ai"), line);
            if (ideal != null) Log.Information("Race AI: ideal line found, bots mix it into their own line");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Race AI: ideal_line.ai not readable");
        }
        LineHeights? heights = null;
        RoadSurface? offTrack = null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (RoadSurface.Load(trackRoot, layout) is { } road)
            {
                heights = LineHeights.Build(line, road);
                Log.Information("Race AI: road surface {Triangles} triangles, height beside the line up to {Max:F2} m off ({Ms} ms)", road.Triangles, heights.Max, sw.ElapsedMilliseconds);
            }
            offTrack = RoadSurface.Load(trackRoot, layout, valid: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Race AI: road surface not readable, cars follow the AI line's height");
        }

        var info = map.Info;
        if (config.UseTrackHints)
            line.ApplyHints(info.SpeedHints, info.MaxSpeedsKmh);
        if (info.StartGrid.Count == 0)
            Log.Warning("Race AI: no start grid found (AC_START_x). Bots will use an estimated grid behind the start line. Set AssettoCorsaPath or GridFile");
        Log.Information("Race AI: racing line {Length:F0} m, start/finish at {Start:F0} m, {Hints} AI hints, {Sections} sections",
            line.Length, map.StartLineS, info.SpeedHints.Count + info.MaxSpeedsKmh.Count, info.Sections.Count);

        return new TrackData { Map = map, Heights = heights, OffTrack = offTrack, Ideal = ideal };
    }

    /// <summary>Content folders to search: the server's own content folder first, then the game installation.</summary>
    public static List<string> ContentRoots(RaceAiConfiguration config)
        => TrackMap.ContentRoots(config.AssettoCorsaPath, m => Log.Warning("Race AI: {Message}", m));
}
