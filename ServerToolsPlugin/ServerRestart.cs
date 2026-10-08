using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ServerToolsPlugin;

/// <summary>
/// Restarting the server: with tools/server-supervisor.sh (RACEAI_SUPERVISED=1) by stopping it with a request file the supervisor reads
/// ("restart", or "update" = pull and install the newest version first); without it inside this process (same preset and ports).
/// </summary>
public sealed class ServerRestart
{
    private readonly ACServerConfiguration _serverConfig;
    private readonly EntryCarManager _entryCarManager;
    private readonly IHostApplicationLifetime _lifetime;

    public ServerRestart(ACServerConfiguration serverConfig, EntryCarManager entryCarManager, IHostApplicationLifetime lifetime)
    {
        _serverConfig = serverConfig;
        _entryCarManager = entryCarManager;
        _lifetime = lifetime;
    }

    /// <summary>Started by the supervisor script, which starts the server again after a restart request.</summary>
    public static bool Supervised => Environment.GetEnvironmentVariable("RACEAI_SUPERVISED") == "1";

    /// <summary>Restart the server in this process with a preset (null = cfg/), same ports.</summary>
    public void RestartInto(string? preset)
        => AssettoServer.Program.RestartServer(preset, portOverrides: new PortOverrides
        {
            TcpPort = _serverConfig.Server.TcpPort,
            UdpPort = _serverConfig.Server.UdpPort,
            HttpPort = _serverConfig.Server.HttpPort
        });

    /// <summary>Restart (or update and restart); returns an error text when not possible.</summary>
    public string? Request(bool update)
    {
        string mode = update ? "update" : "restart";
        if (!Supervised)
        {
            // e.g. at a game server host: restart inside the process (same track, same ports); updates are uploaded there
            if (update)
                return "Update geht nur, wenn der Server über tools/start-server.sh läuft. Beim Hoster: neue Dateien hochladen und neu starten.";
            Log.Information("Server tools: server restart requested (in-process)");
            _entryCarManager.BroadcastChat("Server-Neustart … / server restart …");
            var preset = _serverConfig.Preset;
            _ = Task.Run(async () => { await Task.Delay(1500); RestartInto(string.IsNullOrEmpty(preset) ? null : preset); });
            return null;
        }
        File.WriteAllText("restart.request", mode);
        Log.Information("Server tools: server {Mode} requested", mode);
        _entryCarManager.BroadcastChat(update ? "Server-Update und Neustart … / server update and restart …" : "Server-Neustart … / server restart …");
        _ = Task.Run(async () => { await Task.Delay(1500); _lifetime.StopApplication(); });
        return null;
    }
}
