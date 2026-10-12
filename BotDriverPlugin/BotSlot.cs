using AssettoServer.Server;
using AssettoServer.Server.Ai;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Outgoing;
using BotDriverPlugin.Core;

namespace BotDriverPlugin;

/// <summary>One entry list slot driven by the racing AI. Writes the bot's pose into the slot's <see cref="EntryCar.Status"/>.</summary>
public sealed class BotSlot : IExternalAiController
{
    public EntryCar EntryCar { get; }
    public RaceBot Bot { get; }
    public string Nation { get; }

    /// <summary>False while a real player occupies the slot (e.g. an admin joined it).</summary>
    public bool Active { get; set; } = true;
    /// <summary>Switched off by an admin (/bots_count): the car stays empty until the bots are switched on again.</summary>
    public bool Benched { get; set; }

    /// <summary>Grid position index (AC_START_x) for the current race.</summary>
    public int GridIndex { get; set; }

    /// <summary>Last <see cref="RaceBot.DamageVersion"/> sent to the clients.</summary>
    public int SentDamageVersion { get; set; } = -1;

    /// <summary>Collisions switched off for the clients (emergency ghost).</summary>
    public bool Ghosted { get; set; }

    public BotSlot(EntryCar entryCar, RaceBot bot, string nation)
    {
        EntryCar = entryCar;
        Bot = bot;
        Nation = nation;
    }

    public CarStatus Status => EntryCar.Status;

    public CarStatus? GetStatusForCar(EntryCar toCar) => Active ? Status : null;

    public void WriteStatus(in BotPose pose, long serverTimeMs, CarStatusFlags lights, CarStatusFlags wipers, bool highBeams = true)
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
        if (high) flags &= ~CarStatusFlags.HighBeamsOff;
        if (pose.Braking) flags |= CarStatusFlags.BrakeLightsOn;
        if (pose.Hazards) flags |= CarStatusFlags.HazardsOn;
        status.StatusFlag = flags;
    }

    private static byte EncodeTyreSpeed(float speed, float wheelDiameter)
    {
        // same encoding as AssettoServer's traffic AI
        float angular = speed / (MathF.PI * MathF.Max(0.3f, wheelDiameter)) * 6;
        return (byte)(Math.Clamp(MathF.Round(MathF.Log10(angular + 1.0f) * 20.0f) * Math.Sign(angular), -100.0f, 154.0f) + 100.0f);
    }
}
