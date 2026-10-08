using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Extensions;
using Microsoft.Extensions.Hosting;

namespace ServerToolsPlugin;

/// <summary>"Bots:16,Player:2 - Name" in the server lists, while a plugin drives bots (<see cref="IDrivenCars"/>).</summary>
public sealed class ListedName : BackgroundService
{
    public ListedName(ServerToolsConfiguration config, ACServerConfiguration serverConfig, EntryCarManager entryCarManager, IEnumerable<IDrivenCars> drivers)
    {
        if (!config.ServerNameCounts || string.IsNullOrWhiteSpace(config.ServerNameFormat)) return;
        var drivenCars = drivers.ToList();
        if (drivenCars.Count == 0) return;
        serverConfig.ListedNameProvider = name =>
        {
            int bots = entryCarManager.EntryCars.Count(c => c.Client == null && drivenCars.Any(d => d.Get(c) != null));
            if (bots == 0) return name;
            int players = entryCarManager.ConnectedCars.Count;
            return config.ServerNameFormat.Replace("{bots}", bots.ToString()).Replace("{players}", players.ToString()).Replace("{name}", name);
        };
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
