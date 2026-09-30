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

    public static ConfigurationLocations FromOptions(string? preset, string? serverCfgPath, string? entryListPath)
    {
        var baseFolder = string.IsNullOrEmpty(preset) ? "cfg" : Path.Join("presets", preset);

        if (string.IsNullOrEmpty(entryListPath))
        {
            entryListPath = Path.Join(baseFolder, "entry_list.ini");
        }

        if (string.IsNullOrEmpty(serverCfgPath))
        {
            serverCfgPath = Path.Join(baseFolder, "server_cfg.ini");
        }
        else
        {
            baseFolder = Path.GetDirectoryName(serverCfgPath)!;
        }

        // Race AI patch: files a preset doesn't have come from cfg/ (extra_cfg.yml is merged, see PresetOverlay)
        if (!File.Exists(entryListPath) && PresetOverlay.Applies(baseFolder) && File.Exists(PresetOverlay.MainPath("entry_list.ini")))
            entryListPath = PresetOverlay.MainPath("entry_list.ini");
        var extraCfgPath = Path.Join(baseFolder, "extra_cfg.yml");
        if (!File.Exists(extraCfgPath) && PresetOverlay.Applies(baseFolder) && File.Exists(PresetOverlay.MainPath("extra_cfg.yml")))
            extraCfgPath = PresetOverlay.MainPath("extra_cfg.yml");

        return new ConfigurationLocations
        {
            BaseFolder = baseFolder,
            ServerCfgPath = serverCfgPath,
            EntryListPath = entryListPath,
            ExtraCfgPath = extraCfgPath,
            CSPExtraOptionsPath = PresetOverlay.Resolve(baseFolder, "csp_extra_options.ini"),
            CMContentJsonPath = PresetOverlay.Resolve(baseFolder, Path.Join("cm_content", "content.json")),
            CMWrapperParamsPath = PresetOverlay.Resolve(baseFolder, "cm_wrapper_params.json")
        };
    }

    public string DrsZonePath(string track, string trackLayout)
    {
        return string.IsNullOrEmpty(trackLayout)
            ? Path.Join("content", "tracks", track, "data", "drs_zones.ini")
            : Path.Join("content","tracks", track, trackLayout, "data","drs_zones.ini");
    }
}
