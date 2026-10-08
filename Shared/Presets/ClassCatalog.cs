using TrackGeometry;
using AssettoServer.Server.Configuration;

namespace SharedPresets;

/// <summary>A vehicle class: the folder presets/classes/&lt;key&gt;/ (entry_list.ini with its cars, server_cfg.ini with CARS= and [PRESET] CLASS_TITLE).</summary>
public sealed record VehicleClass(string Key, string Label, string Description, List<string> Models)
{
    /// <summary>Models without content/cars/&lt;model&gt;/data.acd on this server.</summary>
    public List<string> MissingModels()
        => Models.Where(m => !File.Exists(Path.Join("content", "cars", m, "data.acd"))).ToList();
}

/// <summary>The vehicle classes in presets/classes/, and the titles of tracks and classes ([PRESET] in their server_cfg.ini).</summary>
public static class ClassCatalog
{
    public static IReadOnlyList<VehicleClass> All
        => Directory.Exists(PresetOverlay.ClassesFolder)
            ? Directory.GetDirectories(PresetOverlay.ClassesFolder).Select(d => Load(Path.GetFileName(d))).OfType<VehicleClass>()
                .OrderBy(c => c.Key, StringComparer.Ordinal).ToList()
            : [];

    public static VehicleClass? Get(string? key)
        => string.IsNullOrWhiteSpace(key) ? null : Load(key.Trim().ToLowerInvariant());

    private static VehicleClass? Load(string key)
    {
        string dir = PresetOverlay.ClassFolder(key);
        string el = Path.Join(dir, "entry_list.ini");
        if (!File.Exists(el)) return null;
        var ini = IniFile.Load(el);
        var models = ini.Sections.Where(s => s.StartsWith("CAR_", StringComparison.OrdinalIgnoreCase))
            .Select(s => ini.Get(s, "MODEL") ?? "").Where(m => m != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cfg = Path.Join(dir, "server_cfg.ini");
        var c = File.Exists(cfg) ? IniFile.Load(cfg) : null;
        return new VehicleClass(key, c?.Get("PRESET", "CLASS_TITLE") ?? key.ToUpperInvariant(), c?.Get("PRESET", "DESCRIPTION") ?? "", models);
    }

    /// <summary>Display name of a track ("default" = the one in cfg/): [PRESET] TRACK_TITLE, else the folder name.</summary>
    public static string TrackTitle(string track)
    {
        string dir = track is "" or "default" ? PresetOverlay.MainFolder : PresetOverlay.TrackFolder(track);
        var cfg = Path.Join(dir, "server_cfg.ini");
        return (File.Exists(cfg) ? IniFile.Load(cfg).Get("PRESET", "TRACK_TITLE") : null) ?? track;
    }
}
