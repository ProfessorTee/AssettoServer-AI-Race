using AssettoServer.Server.Configuration;
using AssettoServer.Server.Extensions;
using SharedConfig;
using AssettoServer.Server.Plugin;
using Autofac;
using Microsoft.Extensions.Hosting;

namespace ServerToolsPlugin;

/// <summary>
/// Server tools: track rotation and vehicle classes, player statistics and safety rating, real weather, player/bot counts in the
/// server name, restarts. Works on its own; the settings every plugin shares (chat language, AC path, public address) are set here.
/// </summary>
public class ServerToolsModule : AssettoServerModule<ServerToolsConfiguration>
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<SharedSettingsProvider>().As<ISharedSettings>().SingleInstance();
        builder.RegisterType<ServerTrack>().AsSelf().SingleInstance();
        builder.RegisterType<ListedName>().AsSelf().As<IHostedService>().SingleInstance();
        builder.RegisterType<TrackRotation>().AsSelf().As<IHostedService>().SingleInstance();
        builder.RegisterType<RealWeatherService>().AsSelf().As<IHostedService>().SingleInstance();
        builder.RegisterType<PlayerStats>().AsSelf().As<IPlayerRating>().As<IHostedService>().SingleInstance();
        builder.RegisterType<ServerRestart>().AsSelf().SingleInstance();
        builder.RegisterType<RaceResults>().AsSelf().As<IHostedService>().SingleInstance();
        // one writer for plugin_server_tools_cfg.yml (its lock keeps commands and the web portal from writing at the same time)
        builder.Register(c => new ConfigWriter(c.Resolve<ACServerConfiguration>(), "plugin_server_tools_cfg.yml", "Server tools")).AsSelf().SingleInstance();
    }
}

public sealed class SharedSettingsProvider(ServerToolsConfiguration config) : ISharedSettings
{
    public string ChatLanguage => config.ChatLanguage;
    public string? AssettoCorsaPath => config.AssettoCorsaPath;
    public string PublicAddress => config.PublicAddress;
}
