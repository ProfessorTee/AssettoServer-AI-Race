using System.Numerics;
using AssettoServer.Network.ClientMessages;

namespace DriverRecorderPlugin.Packets;

/// <summary>Server to client: start / stop sending samples.</summary>
[OnlineEvent(Key = "DR_control")]
public class DrControlPacket : OnlineEvent<DrControlPacket>
{
    [OnlineEventField(Name = "recording")]
    public bool Recording;
    [OnlineEventField(Name = "sampleHz")]
    public byte SampleHz;
    /// <summary>Clean laps of this player with the current car on this track.</summary>
    [OnlineEventField(Name = "laps")]
    public ushort Laps;
    /// <summary>Best clean lap (ms), 0 = none yet.</summary>
    [OnlineEventField(Name = "bestMs")]
    public int BestMs;
}

/// <summary>Client to server: <see cref="DriverRecorderService.BatchSize"/> samples of the driver's inputs.</summary>
[OnlineEvent(Key = "DR_samples")]
public class DrSamplesPacket : OnlineEvent<DrSamplesPacket>
{
    public const int Size = 5;

    [OnlineEventField(Name = "count")]
    public byte Count;
    /// <summary>Client clock (s).</summary>
    [OnlineEventField(Name = "time", Size = Size)]
    public float[] Time = null!;
    /// <summary>Position along the track's AI spline, 0..1 from the start line.</summary>
    [OnlineEventField(Name = "spline", Size = Size)]
    public float[] Spline = null!;
    [OnlineEventField(Name = "position", Size = Size)]
    public Vector3[] Position = null!;
    /// <summary>km/h</summary>
    [OnlineEventField(Name = "speed", Size = Size)]
    public float[] Speed = null!;
    /// <summary>0..255</summary>
    [OnlineEventField(Name = "gas", Size = Size)]
    public byte[] Gas = null!;
    [OnlineEventField(Name = "brake", Size = Size)]
    public byte[] Brake = null!;
    [OnlineEventField(Name = "clutch", Size = Size)]
    public byte[] Clutch = null!;
    /// <summary>Steering wheel angle in 1/10 degree.</summary>
    [OnlineEventField(Name = "steer", Size = Size)]
    public short[] Steer = null!;
    /// <summary>0 = reverse, 1 = neutral, 2 = first gear.</summary>
    [OnlineEventField(Name = "gear", Size = Size)]
    public byte[] Gear = null!;
    /// <summary>Bits: 1 pit lane, 2 ABS active, 4 TC active, 8 wheels outside the track.</summary>
    [OnlineEventField(Name = "flags", Size = Size)]
    public byte[] Flags = null!;
}

/// <summary>Client to server, about once per second: tyres and fuel.</summary>
[OnlineEvent(Key = "DR_status")]
public class DrStatusPacket : OnlineEvent<DrStatusPacket>
{
    /// <summary>Core temperatures FL, FR, RL, RR (°C).</summary>
    [OnlineEventField(Name = "tyreTemp", Size = 4)]
    public float[] TyreTemp = null!;
    /// <summary>Tyre wear FL, FR, RL, RR as CSP reports it.</summary>
    [OnlineEventField(Name = "tyreWear", Size = 4)]
    public float[] TyreWear = null!;
    /// <summary>psi</summary>
    [OnlineEventField(Name = "tyrePressure", Size = 4)]
    public float[] TyrePressure = null!;
    /// <summary>Litres.</summary>
    [OnlineEventField(Name = "fuel")]
    public float Fuel;
}
