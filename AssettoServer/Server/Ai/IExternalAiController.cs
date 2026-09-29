using AssettoServer.Shared.Model;

namespace AssettoServer.Server.Ai;

/// <summary>
/// Lets a plugin drive an AI slot with its own logic instead of the built-in traffic AI.
/// Set <see cref="EntryCar.ExternalAiController"/> and <see cref="EntryCar.AiControlled"/> on the slot;
/// the built-in traffic AI will then leave that slot alone and position updates are taken from this controller.
/// The plugin is responsible for updating the car state itself (e.g. via <see cref="ACServer.Update"/>).
/// </summary>
public interface IExternalAiController
{
    /// <summary>
    /// Returns the status that should be sent to <paramref name="toCar"/>, or null if the car should not be sent (e.g. not spawned).
    /// </summary>
    CarStatus? GetStatusForCar(EntryCar toCar);

    /// <summary>
    /// Steam ID of the player this car stands in for while he's away (e.g. his AI clone drives on until he's back), else null.
    /// He may join a session that is closed for others, and a race keeps running even when no player is connected.
    /// </summary>
    ulong? StandsInFor => null;

    /// <summary>A race keeps running for this car even when no player is connected.</summary>
    bool KeepsSessionAlive => StandsInFor != null;
}
