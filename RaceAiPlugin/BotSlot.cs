using System.Numerics;
using AssettoServer.Server;
using AssettoServer.Server.Ai;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Outgoing;
using RaceAiPlugin.Core;

namespace RaceAiPlugin;

/// <summary>One entry list slot driven by the racing AI. Writes the bot's pose into the slot's <see cref="EntryCar.Status"/>.</summary>
public enum SwapPhase
{
    /// <summary>Clone drives, the player is away.</summary>
    Away,
    /// <summary>Clone drives, the player watches from a spare car.</summary>
    Watching,
    /// <summary>Clone drives into the pits for the driver change.</summary>
    PitRequested,
    /// <summary>Clone waits in the box, the player is being reconnected into his car.</summary>
    Handover
}

public sealed class BotSlot : IExternalAiController
{
    public EntryCar EntryCar { get; }
    public RaceBot Bot { get; }
    public string Nation { get; }

    /// <summary>False while a real player occupies the slot (e.g. an admin joined it).</summary>
    public bool Active { get; set; } = true;

    /// <summary>Grid position index (AC_START_x) for the current race.</summary>
    public int GridIndex { get; set; }

    /// <summary>Last <see cref="RaceBot.DamageVersion"/> sent to the clients.</summary>
    public int SentDamageVersion { get; set; } = -1;

    /// <summary>Set when this is a player's car his clone drives while he's gone (the Steam ID of the player).</summary>
    public ulong? TakeoverGuid { get; init; }
    public string TakeoverPlayer { get; init; } = "";
    /// <summary>Driver swap state while the clone drives (see RaceAiService.DriverSwap.cs).</summary>
    public SwapPhase Swap { get; set; }
    /// <summary>The player, connected in a spare car, watching his clone.</summary>
    public AssettoServer.Network.Tcp.ACTcpClient? Watcher { get; set; }
    /// <summary>When the player comes back, the clone comes into the pits for him at once (false after /bot: he takes a break).</summary>
    public bool ReturnOnJoin { get; set; } = true;
    public double HandoverSince { get; set; }
    /// <summary>The player was sent into his car before the clone reached the box (his game needs time to load).</summary>
    public bool EarlyReconnect { get; set; }
    /// <summary>The player is loading into his car while the clone still drives it; the clone leaves when he's in.</summary>
    public AssettoServer.Network.Tcp.ACTcpClient? LoadingOwner { get; set; }

    /// <summary>Collisions switched off for the clients (emergency ghost).</summary>
    public bool Ghosted { get; set; }

    public BotSlot(EntryCar entryCar, RaceBot bot, string nation)
    {
        EntryCar = entryCar;
        Bot = bot;
        Nation = nation;
    }

    /// <summary>
    /// Where the pose goes: the slot's status, or for a player's car driven by his clone an own status object
    /// (the player may sit in the car for a moment on his way to a spare car, his updates must not move the clone).
    /// </summary>
    private readonly CarStatus _standInStatus = new();
    public CarStatus Status => TakeoverGuid != null ? _standInStatus : EntryCar.Status;

    public CarStatus? GetStatusForCar(EntryCar toCar) => Active ? Status : null;

    /// <summary>A player's clone keeps the race going while he's away, and he may come back even when the session is closed.</summary>
    public ulong? StandsInFor => Active ? TakeoverGuid : null;

    public void WriteStatus(in BotPose pose, long serverTimeMs, CarStatusFlags lights, CarStatusFlags wipers, bool flashLights = true, bool flashDaytime = true,
        bool highBeams = true)
    {
        var status = Status;
        status.Timestamp = serverTimeMs;
        status.Position = pose.Position;
        status.Rotation = pose.Rotation;
        status.Velocity = pose.Velocity;
        status.NormalizedPosition = pose.NormalizedPosition;

        // FL, FR, RL, RR: locked fronts under braking, spinning rears when the car steps out
        byte front = EncodeTyreSpeed(pose.Speed * pose.FrontTyreFactor, Bot.Car.TyreDiameter);
        byte rear = EncodeTyreSpeed(pose.Speed * pose.RearTyreFactor, Bot.Car.TyreDiameter);
        status.TyreAngularSpeed[0] = front;
        status.TyreAngularSpeed[1] = front;
        status.TyreAngularSpeed[2] = rear;
        status.TyreAngularSpeed[3] = rear;

        status.WheelAngle = (byte)Math.Clamp(127 + MathF.Round(pose.WheelAngleDeg), 0, 254);
        float steerDeg = pose.WheelAngleDeg * Bot.Car.SteerRatio;
        status.SteerAngle = (byte)Math.Clamp(127 + MathF.Round(steerDeg / MathF.Max(90, Bot.Car.SteerLock) * 127), 0, 254);
        status.EngineRpm = (ushort)Math.Clamp(pose.Rpm, 0, ushort.MaxValue);
        // network gear: 0 = reverse, 1 = neutral, 2 = first gear
        status.Gear = (byte)Math.Clamp(pose.Gear + 1, 0, 10);
        status.Gas = pose.Throttle;

        var flags = lights | wipers;
        bool lightsOn = (lights & CarStatusFlags.LightsOn) != 0;
        // at night: high beams when nobody is ahead
        bool high = lightsOn && highBeams && pose.HighBeam;
        if (pose.Flash && flashLights)
        {
            // flash: toggle the high beams at night, lights on during the day
            if (lightsOn) high = !high;
            else if (flashDaytime) flags |= CarStatusFlags.LightsOn;
        }
        if (high) flags &= ~CarStatusFlags.HighBeamsOff;
        if (pose.Braking) flags |= CarStatusFlags.BrakeLightsOn;
        if (pose.Hazards) flags |= CarStatusFlags.HazardsOn;
        else if (pose.Indicator < 0) flags |= CarStatusFlags.IndicateLeft;
        else if (pose.Indicator > 0) flags |= CarStatusFlags.IndicateRight;
        status.StatusFlag = flags;
    }

    private static byte EncodeTyreSpeed(float speed, float wheelDiameter)
    {
        // same encoding as AssettoServer's traffic AI
        float angular = speed / (MathF.PI * MathF.Max(0.3f, wheelDiameter)) * 6;
        return (byte)(Math.Clamp(MathF.Round(MathF.Log10(angular + 1.0f) * 20.0f) * Math.Sign(angular), -100.0f, 154.0f) + 100.0f);
    }

    public static Vector3 RotationFromForward(Vector3 dir)
        => new(MathF.Atan2(dir.Z, dir.X) - MathF.PI / 2,
            (MathF.Atan2(new Vector2(dir.Z, dir.X).Length(), dir.Y) - MathF.PI / 2) * -1f,
            0);
}
