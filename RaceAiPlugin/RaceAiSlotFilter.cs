using AssettoServer.Server;
using AssettoServer.Server.OpenSlotFilters;

namespace RaceAiPlugin;

/// <summary>
/// Bot slots are open to players (<see cref="RaceAiConfiguration.PlayersCanTakeBotSlots"/>): the server tries the free player slots of a car
/// first (they come first in the entry list), then a bot's slot; the bot leaves when the player connects.
/// </summary>
public class RaceAiSlotFilter : OpenSlotFilterBase
{
    private readonly RaceAiService _service;

    public RaceAiSlotFilter(RaceAiService service)
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
