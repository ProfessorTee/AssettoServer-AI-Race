using System.Globalization;
using System.Text.RegularExpressions;
using AssettoServer.Server.Configuration;
using Serilog;

namespace RaceAiPlugin;

/// <summary>
/// Writes settings changed in the dashboard (or by admin commands) back into plugin_race_ai_cfg.yml, so they survive a restart.
/// Top-level keys only, the line is replaced in place (comments stay). The key goes into the top layer (class, track) that sets it,
/// otherwise into cfg/ (valid for all tracks and classes).
/// </summary>
public sealed class ConfigWriter
{
    private const string FileName = "plugin_race_ai_cfg.yml";
    private readonly ACServerConfiguration _serverConfig;
    private readonly object _lock = new();

    public ConfigWriter(ACServerConfiguration serverConfig) => _serverConfig = serverConfig;

    public void Set(string key, object value)
    {
        string text = value switch
        {
            bool b => b ? "true" : "false",
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            Enum e => e.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
        lock (_lock)
        {
            try
            {
                var pattern = new Regex($"^{Regex.Escape(key)}:.*$", RegexOptions.Multiline);
                // the top layer (class, track, cfg/) that sets the key, else cfg/
                string path = _serverConfig.Layers.Reverse().Select(l => Path.Join(l, FileName))
                    .FirstOrDefault(f => File.Exists(f) && pattern.IsMatch(File.ReadAllText(f))) ?? Path.Join(_serverConfig.Layers[0], FileName);
                string content = File.Exists(path) ? File.ReadAllText(path) : "";
                string line = $"{key}: {text}";
                content = pattern.IsMatch(content)
                    ? pattern.Replace(content, line.Replace("$", "$$"), 1)
                    : content.TrimEnd('\n', '\r') + Environment.NewLine + line + Environment.NewLine;
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, content);
                File.Move(tmp, path, overwrite: true);
                Log.Information("Race AI: saved {Key}: {Value} in {File}", key, text, path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Race AI: could not save {Key} in the configuration", key);
            }
        }
    }
}
