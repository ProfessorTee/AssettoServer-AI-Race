using System.Collections.Generic;
using System.IO;

namespace AssettoServer.Server.Configuration;

public class ConfigurationLocations
{
    public required string BaseFolder { get; init; }
    public required string ServerCfgPath { get; init; }
    public required string EntryListPath { get; init; }
    public required string ExtraCfgPath { get; init; }
    public required string CSPExtraOptionsPath { get; init; }
    public required string CMContentJsonPath { get; init; }
    public required string CMWrapperParamsPath { get; init; }

    /// <summary>Race AI patch: the folders the configuration is merged from, bottom to top (see PresetOverlay).</summary>
    public required IReadOnlyList<string> Layers { get; init; }

    public static ConfigurationLocations FromOptions(string? preset, string? serverCfgPath, string? entryListPath)
    {
        // Race AI patch: cfg/ → presets/tracks/<track>/ → presets/classes/<class>/; a given server_cfg.ini path is used alone
        var layers = string.IsNullOrEmpty(serverCfgPath) ? PresetOverlay.Layers(preset) : [Path.GetDirectoryName(serverCfgPath)!];
        // the track's folder (where default configs are written), not the class's
        var baseFolder = layers.Count > 1 && layers[^1].StartsWith(PresetOverlay.ClassesFolder) ? layers[^2] : layers[^1];

        return new ConfigurationLocations
        {
            BaseFolder = baseFolder,
            Layers = layers,
            ServerCfgPath = string.IsNullOrEmpty(serverCfgPath) ? Path.Join(baseFolder, "server_cfg.ini") : serverCfgPath,
            EntryListPath = string.IsNullOrEmpty(entryListPath) ? PresetOverlay.Resolve(layers, "entry_list.ini") : entryListPath,
            ExtraCfgPath = PresetOverlay.Resolve(layers, "extra_cfg.yml"),
            CSPExtraOptionsPath = PresetOverlay.Resolve(layers, "csp_extra_options.ini"),
            CMContentJsonPath = PresetOverlay.Resolve(layers, Path.Join("cm_content", "content.json")),
            CMWrapperParamsPath = PresetOverlay.Resolve(layers, "cm_wrapper_params.json")
        };
    }

    public string DrsZonePath(string track, string trackLayout)
    {
        return string.IsNullOrEmpty(trackLayout)
            ? Path.Join("content", "tracks", track, "data", "drs_zones.ini")
            : Path.Join("content","tracks", track, trackLayout, "data","drs_zones.ini");
    }
}
