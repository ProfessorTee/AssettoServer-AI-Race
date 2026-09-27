using AssettoServer.Server;
using AssettoServer.Server.OpenSlotFilters;

namespace RaceAiPlugin;

/// <summary>Keeps players out of the slots that are driven by racing bots.</summary>
public class RaceAiSlotFilter : OpenSlotFilterBase
{
    private readonly RaceAiService _service;

    public RaceAiSlotFilter(RaceAiService service)
    {
        _service = service;
    }

    public override async ValueTask<bool> IsSlotOpen(EntryCar entryCar, ulong guid)
    {
        if (_service.IsBotSlot(entryCar))
            return false;

        return await base.IsSlotOpen(entryCar, guid);
    }
}
