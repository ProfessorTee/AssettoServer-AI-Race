using AssettoServer.Server;
using AssettoServer.Server.Extensions;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Outgoing;

namespace BotDriverPlugin;

/// <summary>What other plugins (web portal, server tools) may know about the bots (<see cref="IDrivenCars"/>).</summary>
public sealed partial class BotDriverService
{
    // ------------------------------------------------------------------ IDrivenCars (other plugins: live page, server name)

    public bool Ready => _world != null;

    public DrivenCarInfo? Get(EntryCar car)
    {
        lock (_lock)
        {
            if (!_slotsBySessionId.TryGetValue(car.SessionId, out var slot) || !slot.Active) return null;
            var b = slot.Bot;
            return new DrivenCarInfo
            {
                Name = slot.TakeoverGuid != null ? slot.TakeoverPlayer : b.Name,
                Kind = slot.TakeoverGuid != null ? "clone" : "ai",
                Guid = slot.TakeoverGuid ?? 0,
                Status = slot.Status,
                InPitLane = b.InPitLane,
                PitStops = b.PitStops,
                TyrePercent = MathF.Round(Core.RaceWorld.WearGrip(b, b.TyreVirtualKm) * 100, 1)
            };
        }
    }

    public bool? PlayerInPitLane(EntryCar car)
    {
        lock (_lock) return _world?.ExternalInPitLane(car.SessionId);
    }
}

