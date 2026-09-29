using AssettoServer.Network.ClientMessages;

namespace RaceAiPlugin;

/// <summary>Server to client (driverswap.lua): banner state, camera target and reconnect for the driver swap.</summary>
[OnlineEvent(Key = "RAI_swap")]
public class RaiSwapPacket : OnlineEvent<RaiSwapPacket>
{
    /// <summary>0 nothing, 1 clone drives, 2 clone comes to the pits, 3 reconnecting into the own car, 4 drive into your box.</summary>
    [OnlineEventField(Name = "phase")]
    public byte Phase;
    /// <summary>Session id of the player's own car (driven by the clone).</summary>
    [OnlineEventField(Name = "car")]
    public byte Car = 255;
    [OnlineEventField(Name = "position")]
    public byte Position;
    /// <summary>Seconds until the clone is in the pits (about).</summary>
    [OnlineEventField(Name = "eta")]
    public ushort Eta;
    /// <summary>&gt; 0: reconnect after this many seconds.</summary>
    [OnlineEventField(Name = "reconnect")]
    public byte Reconnect;
    /// <summary>Car model to reconnect into (empty = the current one).</summary>
    [OnlineEventField(Name = "model", Size = 64)]
    public string Model = "";
}
