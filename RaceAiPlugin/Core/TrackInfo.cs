using System.Numerics;

namespace RaceAiPlugin.Core;

/// <summary>A range of the lap in normalized position (0..1 along the AI line), can wrap over the start line.</summary>
public readonly record struct TrackRange(float Start, float End, float Value, string Name = "")
{
    public bool Contains(float normalized)
        => Start <= End ? normalized >= Start && normalized <= End : normalized >= Start || normalized <= End;
}

public readonly record struct GridSpot(int Index, Vector3 Position, Vector3 Forward);

/// <summary>
/// Everything the racing AI reads from a track layout besides the racing line:
/// Kunos AI hints (data/ai_hints.ini), section names (data/sections.ini), start grid, pit boxes and the start/finish line.
/// </summary>
public sealed class TrackInfo
{
    public List<TrackRange> SpeedHints { get; } = [];
    public List<TrackRange> MaxSpeedsKmh { get; } = [];
    public List<TrackRange> Sections { get; } = [];
    public List<GridSpot> StartGrid { get; } = [];
    public List<GridSpot> PitBoxes { get; } = [];
    /// <summary>Middle of AC_TIME_0_L/R, the start/finish line.</summary>
    public Vector3? StartFinish { get; set; }

    public static TrackInfo LoadLayoutData(string layoutDataDir)
    {
        var info = new TrackInfo();
        string hints = Path.Join(layoutDataDir, "ai_hints.ini");
        if (File.Exists(hints))
        {
            var ini = IniFile.Load(hints);
            foreach (var s in ini.Sections)
            {
                float start = ini.GetFloat(s, "START", -1), end = ini.GetFloat(s, "END", -1), value = ini.GetFloat(s, "VALUE", -1);
                if (start < 0 || end < 0 || value <= 0) continue;
                if (s.StartsWith("HINT_", StringComparison.OrdinalIgnoreCase)) info.SpeedHints.Add(new TrackRange(start, end, value, s));
                else if (s.StartsWith("MAXSPEED_", StringComparison.OrdinalIgnoreCase)) info.MaxSpeedsKmh.Add(new TrackRange(start, end, value, s));
            }
        }

        string sections = Path.Join(layoutDataDir, "sections.ini");
        if (File.Exists(sections))
        {
            var ini = IniFile.Load(sections);
            foreach (var s in ini.Sections)
            {
                float start = ini.GetFloat(s, "IN", -1), end = ini.GetFloat(s, "OUT", -1);
                var text = ini.Get(s, "TEXT");
                if (start >= 0 && end >= 0 && !string.IsNullOrWhiteSpace(text)) info.Sections.Add(new TrackRange(start, end, 0, text.Trim()));
            }
        }

        return info;
    }

    /// <summary>Reads AC_START_x, AC_PIT_x and AC_TIME_0_L/R from the kn5 files of the layout.</summary>
    public void LoadSpotsFromKn5(string trackRoot, string? layout)
    {
        var dummies = Kn5Reader.ReadTrackDummies(trackRoot, layout);
        AddSpots(dummies);
    }

    public void AddSpots(IEnumerable<Kn5Reader.Kn5Dummy> dummies)
    {
        Vector3? timeL = null, timeR = null;
        var seen = new HashSet<string>();
        foreach (var d in dummies)
        {
            if (!seen.Add(d.Name)) continue; // some tracks contain the same dummy more than once
            if (TryIndex(d.Name, "AC_START_", out int i) || TryIndex(d.Name, "AC_GRID_", out i))
                StartGrid.Add(new GridSpot(i, d.Position, d.Forward));
            else if (TryIndex(d.Name, "AC_PIT_", out i))
                PitBoxes.Add(new GridSpot(i, d.Position, d.Forward));
            else if (d.Name == "AC_TIME_0_L") timeL = d.Position;
            else if (d.Name == "AC_TIME_0_R") timeR = d.Position;
        }
        StartGrid.Sort((a, b) => a.Index.CompareTo(b.Index));
        PitBoxes.Sort((a, b) => a.Index.CompareTo(b.Index));
        if (timeL.HasValue && timeR.HasValue) StartFinish = (timeL.Value + timeR.Value) / 2;
    }

    private static bool TryIndex(string name, string prefix, out int index)
    {
        index = -1;
        return name.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(name.AsSpan(prefix.Length), out index);
    }

    public string? SectionAt(float normalized)
    {
        foreach (var s in Sections)
            if (s.Contains(normalized)) return s.Name;
        return null;
    }
}
