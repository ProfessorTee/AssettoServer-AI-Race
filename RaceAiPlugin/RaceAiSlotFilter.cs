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
        // a player's car his clone drives while he's away: only he can have it back, and he gets his own car back, not another one
        if (_service.TakeoverOwner(entryCar) is { } owner)
            return owner == guid && await base.IsSlotOpen(entryCar, guid);
        if (_service.TakeoverCarOf(guid) is { } own && own.Model == entryCar.Model)
            return false;

        if (_service.IsBotSlot(entryCar) && !_service.PlayersCanTakeBotSlots)
            return false;

        return await base.IsSlotOpen(entryCar, guid);
    }
}
