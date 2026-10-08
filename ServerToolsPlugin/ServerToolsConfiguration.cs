using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace ServerToolsPlugin;

/// <summary>plugin_server_tools_cfg.yml. Track rotation and vehicle class are in rotation.yml (server folder).</summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class ServerToolsConfiguration
{
    // ---- settings other plugins read too (they can override them in their own file)
    [YamlMember(Description = "Language of chat messages and in-game banners of all plugins: en or de")]
    public string ChatLanguage { get; set; } = "en";
    [YamlMember(Description = "Assetto Corsa installation, for track and car data that isn't in the server's content/ folder (optional)")]
    public string? AssettoCorsaPath { get; set; }
    [YamlMember(Description = "Address players use to join (host name or IP). Empty = the public IP is detected")]
    public string PublicAddress { get; set; } = "";

    // ---- server list
    [YamlMember(Description = "Show the number of bots and players in the server name of the server lists (only while a plugin drives bots)")]
    public bool ServerNameCounts { get; set; } = true;
    [YamlMember(Description = "Format for ServerNameCounts: {bots}, {players} and {name} (the name from server_cfg.ini)")]
    public string ServerNameFormat { get; set; } = "Bots:{bots},Player:{players} - {name}";

    // ---- real weather
    [YamlMember(Description = "Real weather at the track from Open-Meteo (needs EnableWeatherFx: true in extra_cfg.yml)")]
    public bool RealWeather { get; set; } = false;
    [YamlMember(Description = "Minutes between real weather updates")]
    public int RealWeatherUpdateMinutes { get; set; } = 10;
    [YamlMember(Description = "Seconds a weather change takes")]
    public int RealWeatherTransitionSeconds { get; set; } = 120;

    // ---- race
    [YamlMember(Description = "Final classification of every race in the chat (the game's result screen doesn't always list bots)")]
    public bool AnnounceRaceResult { get; set; } = true;

    // ---- statistics
    [YamlMember(Description = "Player statistics, best laps and safety rating (stats/players.json, /top, /profile)")]
    public bool PlayerStats { get; set; } = true;
}
