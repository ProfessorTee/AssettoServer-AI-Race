using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Serilog;
using YamlDotNet.RepresentationModel;

namespace AssettoServer.Server.Configuration;

/// <summary>
/// The Race AI plugin was split into BotDriverPlugin, WebPortalPlugin and ServerToolsPlugin. Servers set up before keep working:
/// "RaceAiPlugin" in EnablePlugins loads its successors that are installed, and a successor without a config of its own gets the keys
/// it knows from plugin_race_ai_cfg.yml (in every layer that has one).
/// </summary>
public static class LegacyPluginConfig
{
    public const string LegacyPlugin = "RaceAiPlugin";
    public const string LegacyConfigFile = "plugin_race_ai_cfg.yml";
    public const string BotSuccessor = "BotDriverPlugin";
    public static readonly string[] Successors = ["ServerToolsPlugin", "WebPortalPlugin", BotSuccessor];

    /// <summary>EnablePlugins with "RaceAiPlugin" replaced by its installed successors (and itself while it's still installed).</summary>
    public static List<string> ExpandPluginNames(IEnumerable<string> enabled, Func<string, bool> isAvailable)
    {
        var result = new List<string>();
        foreach (var name in enabled)
        {
            if (name != LegacyPlugin)
            {
                if (!result.Contains(name)) result.Add(name);
                continue;
            }
            // the old plugin only while its successor isn't installed (an old plugins/RaceAiPlugin/ folder left on the server would
            // otherwise run a second set of bots)
            if (isAvailable(name) && !isAvailable(BotSuccessor) && !result.Contains(name)) result.Add(name);
            else if (isAvailable(name)) Log.Warning("{Legacy} is replaced by {Plugin}: the old plugins/{Legacy} folder isn't loaded and can be deleted", LegacyPlugin, BotSuccessor, LegacyPlugin);
            foreach (var s in Successors.Where(s => isAvailable(s) && !result.Contains(s) && !enabled.Contains(s)))
            {
                result.Add(s);
                Log.Information("{Legacy} in EnablePlugins: loading {Plugin}", LegacyPlugin, s);
            }
        }
        return result;
    }

    /// <summary>
    /// Writes <paramref name="fileName"/> next to every plugin_race_ai_cfg.yml of a layer that doesn't have it yet, with the keys
    /// <paramref name="configType"/> has. Returns true when a file was written.
    /// </summary>
    public static bool Migrate(IReadOnlyList<string> layers, string fileName, Type configType)
    {
        if (fileName == LegacyConfigFile) return false;
        var keys = configType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite).Select(p => p.Name).ToHashSet();
        bool written = false;
        foreach (var layer in layers)
        {
            // every layer on its own: cfg/ may already have been taken over while a track's layer is used for the first time now
            var legacy = Path.Join(layer, LegacyConfigFile);
            if (!File.Exists(legacy) || File.Exists(Path.Join(layer, fileName))) continue;
            try
            {
                var stream = new YamlStream();
                using (var reader = File.OpenText(legacy)) stream.Load(reader);
                if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) continue;
                var mine = new YamlMappingNode();
                foreach (var (key, value) in root.Children)
                    if (key is YamlScalarNode { Value: { } k } && keys.Contains(k))
                        mine.Children.Add(key, value);
                if (mine.Children.Count == 0) continue;

                using var writer = new StringWriter();
                writer.WriteLine($"# Taken over from {LegacyConfigFile} ({DateTime.Now:yyyy-MM-dd}). Settings of this plugin only.");
                new YamlStream(new YamlDocument(mine)).Save(writer, assignAnchors: false);
                // YamlStream ends a document with "...": not needed in a config file
                var text = writer.ToString().TrimEnd();
                if (text.EndsWith("...")) text = text[..^3].TrimEnd();
                File.WriteAllText(Path.Join(layer, fileName), text + Environment.NewLine);
                Log.Information("Configuration: {File} made from {Legacy} ({Count} settings)", Path.Join(layer, fileName), legacy, mine.Children.Count);
                written = true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Configuration: {File} could not be made from {Legacy}", fileName, legacy);
            }
        }
        return written;
    }
}
