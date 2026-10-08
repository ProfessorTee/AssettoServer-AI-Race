using AssettoServer.Server.Configuration;
using Serilog;
using TrackGeometry;

namespace ServerToolsPlugin;

/// <summary>The running track: its key for statistics and its length (from the AI line, read in the background when first needed).</summary>
public sealed class ServerTrack
{
    private readonly ACServerConfiguration _serverConfig;
    private readonly ServerToolsConfiguration _config;
    private float _length = -1;
    private readonly object _lock = new();

    public ServerTrack(ACServerConfiguration serverConfig, ServerToolsConfiguration config)
    {
        _serverConfig = serverConfig;
        _config = config;
        Key = TrackMap.Key(serverConfig.Server.Track, serverConfig.Server.TrackConfig);
        _ = Task.Run(() => LengthMeters);
    }

    /// <summary>"ks_nordschleife-nordschleife".</summary>
    public string Key { get; }

    /// <summary>Lap length in metres, 0 when the track's AI line isn't on the server.</summary>
    public float LengthMeters
    {
        get
        {
            lock (_lock)
            {
                if (_length >= 0) return _length;
                try
                {
                    string track = _serverConfig.CSPTrackOptions.Track;
                    var roots = TrackMap.ContentRoots(_config.AssettoCorsaPath);
                    _length = TrackMap.Load(track, _serverConfig.Server.TrackConfig, roots).Line.Length;
                }
                catch (Exception ex)
                {
                    Log.Warning("Server tools: track length unknown, statistics without distances ({Message})", ex.Message);
                    _length = 0;
                }
                return _length;
            }
        }
    }
}
