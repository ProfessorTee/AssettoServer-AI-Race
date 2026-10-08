using System.Globalization;
using System.Text.RegularExpressions;
using AssettoServer.Server.Configuration;
using Serilog;

namespace SharedConfig;

/// <summary>
/// Writes settings changed in the dashboard (or by admin commands) back into a plugin's yml file, so they survive a restart.
/// Top-level keys only, the line is replaced in place (comments stay). The key goes into the top layer (class, track) that sets it,
/// otherwise into cfg/ (valid for all tracks and classes).
/// </summary>
public sealed class ConfigWriter
{
    private readonly string FileName;
    private readonly string _logPrefix;
    private readonly ACServerConfiguration _serverConfig;
    private readonly object _lock = new();

    /// <param name="fileName">e.g. plugin_bot_driver_cfg.yml</param>
    /// <param name="logPrefix">e.g. "Bot driver"</param>
    public ConfigWriter(ACServerConfiguration serverConfig, string fileName, string logPrefix)
    {
        _serverConfig = serverConfig;
        FileName = fileName;
        _logPrefix = logPrefix;
    }

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
                Log.Information("{Prefix}: saved {Key}: {Value} in {File}", _logPrefix, key, text, path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "{Prefix}: could not save {Key} in the configuration", _logPrefix, key);
            }
        }
    }
}
