using System;
using System.IO;
using System.Linq;
using IniParser;
using IniParser.Model;
using YamlDotNet.RepresentationModel;

namespace AssettoServer.Server.Configuration;

/// <summary>
/// Race AI patch: a preset (presets/&lt;name&gt;/) only needs the settings that differ from cfg/. The files in cfg/ are loaded first,
/// the preset's files on top (server_cfg.ini key by key, [WEATHER_x] as a whole block; yml files key by key). A file the preset
/// doesn't have is taken from cfg/ as it is.
/// </summary>
public static class PresetOverlay
{
    public const string MainFolder = "cfg";

    /// <summary>True when <paramref name="baseFolder"/> is a preset folder and cfg/ exists.</summary>
    public static bool Applies(string baseFolder)
        => Directory.Exists(MainFolder)
           && Path.GetFullPath(baseFolder).TrimEnd(Path.DirectorySeparatorChar) != Path.GetFullPath(MainFolder).TrimEnd(Path.DirectorySeparatorChar);

    public static string MainPath(string fileName) => Path.Join(MainFolder, fileName);

    /// <summary>The file to read: the preset's own, or the one in cfg/ when the preset doesn't have it.</summary>
    public static string Resolve(string baseFolder, string fileName)
    {
        var own = Path.Join(baseFolder, fileName);
        if (File.Exists(own) || !Applies(baseFolder)) return own;
        var main = MainPath(fileName);
        return File.Exists(main) ? main : own;
    }

    /// <summary>cfg/server_cfg.ini with the preset's server_cfg.ini on top.</summary>
    public static IniData MergeServerCfg(string presetFolder, string presetPath)
    {
        var parser = new FileIniDataParser();
        var main = parser.ReadFile(MainPath("server_cfg.ini"));
        var preset = parser.ReadFile(presetPath);

        // weather: the preset's [WEATHER_x] replace all of cfg/ (otherwise leftover weathers from cfg/ would mix in)
        if (preset.Sections.Any(s => s.SectionName.StartsWith("WEATHER_", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var name in main.Sections.Select(s => s.SectionName)
                         .Where(n => n.StartsWith("WEATHER_", StringComparison.OrdinalIgnoreCase)).ToList())
                main.Sections.RemoveSection(name);
        }

        // WELCOME_MESSAGE of cfg/ is relative to the server folder, a preset's one relative to the preset folder
        var welcome = main["SERVER"]["WELCOME_MESSAGE"];
        if (!string.IsNullOrWhiteSpace(welcome) && !preset["SERVER"].ContainsKey("WELCOME_MESSAGE"))
            main["SERVER"]["WELCOME_MESSAGE"] = Path.GetRelativePath(presetFolder, welcome).Replace('\\', '/');

        foreach (var section in preset.Sections)
        {
            if (!main.Sections.ContainsSection(section.SectionName)) main.Sections.AddSection(section.SectionName);
            foreach (var key in section.Keys)
                main[section.SectionName][key.KeyName] = key.Value;
        }
        return main;
    }

    /// <summary>Text of a yml file: the preset's on top of the one in cfg/ (or just one of them). Null if neither exists.</summary>
    public static string? ReadYaml(string baseFolder, string fileName)
    {
        var own = Path.Join(baseFolder, fileName);
        var main = MainPath(fileName);
        bool hasOwn = File.Exists(own), hasMain = Applies(baseFolder) && File.Exists(main);
        if (!hasOwn && !hasMain) return null;
        if (!hasMain) return File.ReadAllText(own);
        if (!hasOwn) return File.ReadAllText(main);

        var mainDoc = Load(main);
        var ownDoc = Load(own);
        if (mainDoc == null) return File.ReadAllText(own);
        if (ownDoc == null) return File.ReadAllText(main);
        Merge(mainDoc, ownDoc);

        var stream = new YamlStream(new YamlDocument(mainDoc));
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
