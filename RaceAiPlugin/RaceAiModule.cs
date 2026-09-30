using AssettoServer.Server.OpenSlotFilters;
using AssettoServer.Server.Plugin;
using Autofac;
using Microsoft.Extensions.Hosting;

namespace RaceAiPlugin;

public class RaceAiModule : AssettoServerModule<RaceAiConfiguration>
{
    public override object ReferenceConfiguration => new RaceAiConfiguration
    {
        BotSlots = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
        AssettoCorsaPath = "C:/Program Files (x86)/Steam/steamapps/common/assettocorsa",
        Personalities = PersonalityConfiguration.Defaults(),
        Drivers =
        [
            new BotDriverConfiguration { Slot = 1, Name = "Max Reuter", Nation = "AUT", Strength = 97, Aggression = 70, Personality = "DiveBomber" }
        ]
    };

    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<RaceAiService>().AsSelf().As<IHostedService>().SingleInstance();
        builder.RegisterType<RaceAiSlotFilter>().As<IOpenSlotFilter>().SingleInstance();
        builder.RegisterType<JoinInfo>().AsSelf().SingleInstance();
        builder.RegisterType<TrackRotation>().AsSelf().As<IHostedService>().SingleInstance();
        builder.RegisterType<RealWeatherService>().AsSelf().As<IHostedService>().SingleInstance();
    }
}
