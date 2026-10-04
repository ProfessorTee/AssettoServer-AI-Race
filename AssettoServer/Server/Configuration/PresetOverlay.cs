using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IniParser;
using IniParser.Model;
using YamlDotNet.RepresentationModel;

namespace AssettoServer.Server.Configuration;

/// <summary>
/// Race AI patch: the configuration is made of layers, each only with what differs from the one below:
/// cfg/ → presets/tracks/&lt;track&gt;/ → presets/classes/&lt;class&gt;/. Preset name "nordschleife+gte" = track + vehicle class,
/// "nordschleife" = track only (cars from cfg/), "+gte" = class on the track of cfg/. An old flat presets/&lt;name&gt;/ still works as track layer.
/// server_cfg.ini is merged key by key ([WEATHER_x] as a whole block), yml files key by key, any other file is taken from the top layer that has it.
/// {track} and {class} in NAME, the welcome message and ServerDescription are replaced with [PRESET] TRACK_TITLE / CLASS_TITLE.
/// </summary>
public static class PresetOverlay
{
    public const string MainFolder = "cfg";
    public const char ClassSeparator = '+';
    public static readonly string TracksFolder = Path.Join("presets", "tracks");
    public static readonly string ClassesFolder = Path.Join("presets", "classes");

    public static (string Track, string? Class) Split(string? preset)
    {
        if (string.IsNullOrEmpty(preset)) return ("", null);
        int i = preset.IndexOf(ClassSeparator);
        return i < 0 ? (preset, null) : (preset[..i], preset[(i + 1)..] is { Length: > 0 } c ? c : null);
    }

    public static string Join(string track, string? cls)
        => string.IsNullOrEmpty(cls) ? track : $"{(track == "default" ? "" : track)}{ClassSeparator}{cls}";

    public static string TrackFolder(string track)
        => Directory.Exists(Path.Join(TracksFolder, track)) ? Path.Join(TracksFolder, track) : Path.Join("presets", track);

    public static string ClassFolder(string cls) => Path.Join(ClassesFolder, cls);

    /// <summary>The folders of a preset, bottom (cfg/) to top.</summary>
    public static List<string> Layers(string? preset)
    {
        var layers = new List<string> { MainFolder };
        var (track, cls) = Split(preset);
        if (track is not ("" or "default")) layers.Add(TrackFolder(track));
        if (cls != null) layers.Add(ClassFolder(cls));
        return layers;
    }

    /// <summary>The file from the top layer that has it, else the path in the top layer.</summary>
    public static string Resolve(IReadOnlyList<string> layers, string fileName)
        => layers.Reverse().Select(l => Path.Join(l, fileName)).FirstOrDefault(File.Exists) ?? Path.Join(layers[^1], fileName);

    /// <summary>server_cfg.ini of all layers of <paramref name="preset"/>, each on top of the one below.</summary>
    public static IniData MergeServerCfg(string? preset)
    {
        var layers = Layers(preset);
        var parser = new FileIniDataParser();
        IniData? merged = null;
        foreach (var layer in layers)
        {
            var path = Path.Join(layer, "server_cfg.ini");
            if (!File.Exists(path)) continue;
            var data = parser.ReadFile(path);
            // WELCOME_MESSAGE: relative to the server folder in cfg/, to the layer's folder elsewhere
            var welcome = data["SERVER"]["WELCOME_MESSAGE"];
            if (layer != MainFolder && !string.IsNullOrWhiteSpace(welcome) && !Path.IsPathRooted(welcome))
                data["SERVER"]["WELCOME_MESSAGE"] = Path.Join(layer, welcome).Replace('\\', '/');
            if (merged == null)
            {
                merged = data;
                continue;
            }

            // weather: the layer's [WEATHER_x] replace all below (otherwise leftover weathers would mix in)
            if (data.Sections.Any(s => s.SectionName.StartsWith("WEATHER_", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var name in merged.Sections.Select(s => s.SectionName)
                             .Where(n => n.StartsWith("WEATHER_", StringComparison.OrdinalIgnoreCase)).ToList())
                    merged.Sections.RemoveSection(name);
            }

            foreach (var section in data.Sections)
            {
                if (!merged.Sections.ContainsSection(section.SectionName)) merged.Sections.AddSection(section.SectionName);
                foreach (var key in section.Keys)
                    merged[section.SectionName][key.KeyName] = key.Value;
            }
        }

        if (merged == null) throw new FileNotFoundException("no server_cfg.ini in " + string.Join(", ", layers));
        var (track, cls) = Split(preset);
        if (string.IsNullOrEmpty(merged["PRESET"]["TRACK_TITLE"]) && track is not ("" or "default")) merged["PRESET"]["TRACK_TITLE"] = track;
        if (string.IsNullOrEmpty(merged["PRESET"]["CLASS_TITLE"]) && cls != null) merged["PRESET"]["CLASS_TITLE"] = cls.ToUpperInvariant();
        merged["SERVER"]["NAME"] = Fill(merged["SERVER"]["NAME"], merged);
        return merged;
    }

    /// <summary>{track} and {class} replaced with the titles of the merged server_cfg.ini.</summary>
    public static string Fill(string? text, IniData merged)
        => (text ?? "").Replace("{track}", merged["PRESET"]["TRACK_TITLE"] ?? "").Replace("{class}", merged["PRESET"]["CLASS_TITLE"] ?? "").Trim();

    /// <summary>Text of a yml file, every layer's on top of the one below. Null if no layer has it.</summary>
    public static string? ReadYaml(IReadOnlyList<string> layers, string fileName)
    {
        var files = layers.Select(l => Path.Join(l, fileName)).Where(File.Exists).ToList();
        if (files.Count == 0) return null;
        if (files.Count == 1) return File.ReadAllText(files[0]);

        YamlMappingNode? merged = null;
        foreach (var f in files)
        {
            var doc = Load(f);
            if (doc == null) continue;
            if (merged == null) merged = doc;
            else Merge(merged, doc);
        }
        if (merged == null) return File.ReadAllText(files[^1]);

        var stream = new YamlStream(new YamlDocument(merged));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static YamlMappingNode? Load(string path)
    {
        var stream = new YamlStream();
        using (var reader = File.OpenText(path)) stream.Load(reader);
        return stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
    }

    private static void Merge(YamlMappingNode target, YamlMappingNode overlay)
    {
        foreach (var (key, value) in overlay.Children)
        {
            if (value is YamlMappingNode map && target.Children.TryGetValue(key, out var existing) && existing is YamlMappingNode existingMap)
                Merge(existingMap, map);
            else
                target.Children[key] = value;
        }
    }
}
