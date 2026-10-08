using AssettoServer.Server.Extensions;
using AssettoServer.Server.Plugin;
using Autofac;
using Microsoft.Extensions.Hosting;

namespace WebPortalPlugin;

/// <summary>
/// Web pages of the server: join page, live timing with map, best laps, admin dashboard. Works on its own with what the server knows;
/// shows more when other plugins are there (bots: BotDriver / Race AI, statistics and rotation: ServerToolsPlugin).
/// </summary>
public class WebPortalModule : AssettoServerModule<WebPortalConfiguration>
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<PortalAccess>().As<IAdminWebAccess>().SingleInstance();
        builder.RegisterType<RaceView>().AsSelf().As<IHostedService>().SingleInstance();
        builder.RegisterType<JoinInfo>().AsSelf().SingleInstance();
        builder.RegisterType<LiveFeed>().AsSelf().SingleInstance();
    }
}

/// <summary>Remote access to the admin pages of all plugins (DashboardRemoteAccess).</summary>
public sealed class PortalAccess(WebPortalConfiguration config) : IAdminWebAccess
{
    public bool RemoteAccess => config.DashboardRemoteAccess;
}
