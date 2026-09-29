using AssettoServer.Server.Plugin;
using Autofac;
using Microsoft.Extensions.Hosting;

namespace DriverRecorderPlugin;

public class DriverRecorderModule : AssettoServerModule<DriverRecorderConfiguration>
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<DriverRecorderService>().AsSelf().As<IHostedService>().SingleInstance();
    }

    public override DriverRecorderConfiguration ReferenceConfiguration => new();
}
