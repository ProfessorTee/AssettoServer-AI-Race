using System.Text;
using AssettoServer.Commands;
using AssettoServer.Commands.Attributes;
using AssettoServer.Server.Configuration;
using JetBrains.Annotations;
using Qmmands;
using SharedConfig;
using SharedPresets;

namespace ServerToolsPlugin;

/// <summary>Chat commands of the server tools: /server_... (admin), /top and /profile for everybody. Old /raceai_... names still work.</summary>
[UsedImplicitly(ImplicitUseKindFlags.Default, ImplicitUseTargetFlags.WithMembers)]
public class ServerToolsCommandModule : ACModuleBase
{
    private readonly TrackRotation _rotation;
    private readonly PlayerStats _stats;
    private readonly ServerTrack _track;
    private readonly ServerRestart _restart;
    private readonly ServerToolsConfiguration _config;
    private readonly ConfigWriter _configWriter;

    public ServerToolsCommandModule(TrackRotation rotation, PlayerStats stats, ServerTrack track, ServerRestart restart, ServerToolsConfiguration config,
        ConfigWriter configWriter)
    {
        _rotation = rotation;
        _stats = stats;
        _track = track;
        _restart = restart;
        _config = config;
        _configWriter = configWriter;
    }

    [Command("server_nexttrack", "raceai_nexttrack"), RequireAdmin]
    public void NextTrack(string? track = null)
    {
        Reply(_rotation.Active
            ? _rotation.StartChange("admin", track) ? $"Track change to {track ?? _rotation.NextTrack()} started" : "A track change is already running"
            : "No track rotation (rotation.yml)");
    }

    /// <summary>/server_class (list), /server_class gte (change now), /server_class gte next (from the next track change on).</summary>
    [Command("server_class", "raceai_class", "class"), RequireAdmin]
    public void Class(string? cls = null, string? when = null)
    {
        if (string.IsNullOrWhiteSpace(cls))
        {
            var running = ClassCatalog.Get(PresetOverlay.Split(_rotation.Current).Class);
            Reply($"Class: {_rotation.ConfiguredClass?.Label ?? "-"} (running: {running?.Label ?? "cfg/"}). " +
                  string.Join(" | ", ClassCatalog.All.Select(c => c.Key + (c.MissingModels().Count > 0 ? " (cars missing)" : ""))) +
                  ". /server_class <class> = now, /server_class <class> next = from the next track change");
            return;
        }
        bool now = !string.Equals(when, "next", StringComparison.OrdinalIgnoreCase) && !string.Equals(when, "later", StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(when, "danach", StringComparison.OrdinalIgnoreCase);
        Reply(_rotation.SetClass(cls, now));
    }

    /// <summary>/server_set realweather on|off</summary>
    [Command("server_set"), RequireAdmin]
    public void Set(string feature, string state)
    {
        bool on = state.ToLowerInvariant() is "on" or "1" or "an" or "ein" or "true";
        switch (feature.ToLowerInvariant())
        {
            case "realweather":
                _config.RealWeather = on;
                _configWriter.Set("RealWeather", on);
                Reply($"Real weather {(on ? "on" : "off")}");
                break;
            default:
                Reply("Unknown setting. Use: realweather");
                break;
        }
    }

    /// <summary>/server_restart, /server_restart update (only with the supervisor script).</summary>
    [Command("server_restart"), RequireAdmin]
    public void Restart(string? mode = null)
    {
        Reply(_restart.Request(string.Equals(mode, "update", StringComparison.OrdinalIgnoreCase)) ?? "Restarting …");
    }

    /// <summary>Best laps on the current track: /top (this week), /top all.</summary>
    [Command("top")]
    public void Top(string which = "week")
    {
        if (!_stats.Enabled) { Reply("No statistics on this server."); return; }
        bool week = !(which.ToLowerInvariant() is "all" or "alltime" or "allzeit" or "ever");
        var list = _stats.Top(_track.Key, week, 10);
        if (list.Count == 0)
        {
            Reply(week ? "No lap times this week yet. /top all for all time." : "No lap times yet.");
            return;
        }
        var sb = new StringBuilder(week ? $"Best laps this week ({PlayerStats.Week(DateTime.UtcNow)}):" : "Best laps of all time:");
        for (int i = 0; i < list.Count; i++)
            sb.Append($"\n{i + 1}. {PlayerStats.Fmt(list[i].Ms)} {list[i].Name} ({list[i].Car})");
        Reply(sb.ToString());
    }

    /// <summary>Profile with safety rating: /profile, /profile name, /sr.</summary>
    [Command("profile", "sr", "stats")]
    public void Profile([Remainder] string? name = null)
    {
        if (!_stats.Enabled) { Reply("No statistics on this server."); return; }
        Reply(_stats.ProfileText(Client?.Guid ?? 0, name));
    }
}
