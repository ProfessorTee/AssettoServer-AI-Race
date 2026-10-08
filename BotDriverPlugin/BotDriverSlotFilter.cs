using AssettoServer.Server;
using AssettoServer.Server.OpenSlotFilters;

namespace BotDriverPlugin;

/// <summary>
/// Bot slots are open to players (<see cref="BotDriverConfiguration.PlayersCanTakeBotSlots"/>): the server tries the free player slots of a car
/// first (they come first in the entry list), then a bot's slot (also a car a bot took over from a player who left); the bot
/// leaves when the player connects.
/// </summary>
public class BotDriverSlotFilter : OpenSlotFilterBase
{
    private readonly BotDriverService _service;

    public BotDriverSlotFilter(BotDriverService service)
    {
        _service = service;
    }

    public override async ValueTask<bool> IsSlotOpen(EntryCar entryCar, ulong guid)
    {
        if (_service.IsBotSlot(entryCar) && !_service.PlayersCanTakeBotSlots)
            return false;

        return await base.IsSlotOpen(entryCar, guid);
    }
}
