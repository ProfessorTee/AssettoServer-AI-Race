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
        // a player's car his clone drives: nobody else gets it; the player himself watches from a spare car until the
        // driver change in the pits (or gets his car back at once when there's no spare car)
        if (_service.SwapSlotOpen(entryCar, guid) is { } open)
            return open && await base.IsSlotOpen(entryCar, guid);

        // a player who lost the connection in the race gets his car back (and only his car)
        if (_service.RejoinSlotOpen(entryCar, guid) is { } rejoin)
            return rejoin && await base.IsSlotOpen(entryCar, guid);

        if (_service.IsBotSlot(entryCar) && !_service.PlayersCanTakeBotSlots)
            return false;

        return await base.IsSlotOpen(entryCar, guid);
    }
}
