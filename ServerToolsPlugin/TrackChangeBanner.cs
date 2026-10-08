using System.Reflection;
using AssettoServer.Network.ClientMessages;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using Serilog;

namespace ServerToolsPlugin;

/// <summary>Server to client (trackchange.lua): banner with the next track and a countdown until the players can rejoin.</summary>
[OnlineEvent(Key = "ST_trackchange")]
public class TrackChangePacket : OnlineEvent<TrackChangePacket>
{
    /// <summary>Seconds until the new server is ready.</summary>
    [OnlineEventField(Name = "eta")]
    public ushort Eta;
    /// <summary>The next track (with class).</summary>
    [OnlineEventField(Name = "info", Size = 48)]
    public string Info = "";
}

public static class TrackChangeBanner
{
    public static void Register(CSPServerScriptProvider scriptProvider)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ServerToolsPlugin.lua.trackchange.lua");
        if (stream == null)
        {
            Log.Warning("Server tools: trackchange.lua not embedded, no banner on track changes");
            return;
        }
        scriptProvider.AddScript(stream, "servertools_trackchange.lua");
    }

    public static void Send(ACTcpClient client, string track, int seconds)
    {
        try
        {
            // no automatic reconnect: CSP's reconnect keeps the loaded track and the game crashes when the server has another one
            client.SendPacket(new TrackChangePacket { Eta = (ushort)Math.Clamp(seconds, 0, 65535), Info = track });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Server tools: track change packet not sent");
        }
    }
}
