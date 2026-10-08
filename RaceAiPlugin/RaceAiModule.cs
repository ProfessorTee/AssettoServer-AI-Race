using AssettoServer.Server.Extensions;
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
        builder.RegisterType<RaceAiService>().AsSelf().As<IHostedService>().As<IDrivenCars>().SingleInstance();
        builder.RegisterType<RaceAiSlotFilter>().As<IOpenSlotFilter>().SingleInstance();
    }
}

/// <summary>Settings a plugin shares with the others (<see cref="ISharedSettings"/>): own value when set, else the shared one.</summary>
public static class SharedSettingsResolver
{
    public static void Resolve(RaceAiConfiguration config, IEnumerable<ISharedSettings> shared)
    {
        var list = shared.ToList();
        config.ChatLanguage = SharedSettings.ChatLanguage(list, config.ChatLanguage);
        config.AssettoCorsaPath = SharedSettings.AssettoCorsaPath(list, config.AssettoCorsaPath);
        config.PublicAddress = SharedSettings.PublicAddress(list, config.PublicAddress);
    }
}
