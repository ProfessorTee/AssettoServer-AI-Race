using System.Collections.Generic;
using System.Linq;
using AssettoServer.Shared.Model;

namespace AssettoServer.Server.Extensions;

// Contracts between plugins. Every plugin is loaded on its own (separate assembly load context), so plugins can only share types that
// live in the server itself. A plugin registers an implementation with Autofac; another one asks for IEnumerable<IContract> and gets
// an empty list when that plugin isn't installed. Nothing in here may depend on a plugin.

/// <summary>Settings several plugins need, set once (ServerToolsPlugin). Use <see cref="SharedSettings"/> to read them with defaults.</summary>
public interface ISharedSettings
{
    /// <summary>"en" or "de".</summary>
    string ChatLanguage { get; }
    /// <summary>Assetto Corsa installation for content that isn't in the server's content/ folder, null = none.</summary>
    string? AssettoCorsaPath { get; }
    /// <summary>Address players use to join (host name or IP), empty = detect the public IP.</summary>
    string PublicAddress { get; }
}

/// <summary>Reads <see cref="ISharedSettings"/> when a plugin provides them, with each consumer's own fallback otherwise.</summary>
public static class SharedSettings
{
    public static string ChatLanguage(IEnumerable<ISharedSettings> all, string? own = null)
        => !string.IsNullOrWhiteSpace(own) ? own : all.FirstOrDefault()?.ChatLanguage ?? "en";

    public static string? AssettoCorsaPath(IEnumerable<ISharedSettings> all, string? own = null)
        => !string.IsNullOrWhiteSpace(own) ? own : all.FirstOrDefault()?.AssettoCorsaPath;

    public static string PublicAddress(IEnumerable<ISharedSettings> all, string? own = null)
        => !string.IsNullOrWhiteSpace(own) ? own : all.FirstOrDefault()?.PublicAddress ?? "";
}

/// <summary>Safety rating of players (ServerToolsPlugin).</summary>
public interface IPlayerRating
{
    /// <summary>Licence class and rating, e.g. "A 4.2"; null when unknown or not enough driven yet.</summary>
    string? Licence(ulong guid);
}

/// <summary>What a plugin that drives cars (BotDriverPlugin) knows about one of them, for timing and live pages.</summary>
public sealed class DrivenCarInfo
{
    public required string Name { get; init; }
    /// <summary>"ai" for a bot, "clone" for a bot standing in for a player.</summary>
    public required string Kind { get; init; }
    /// <summary>Steam ID of the player a clone stands in for, else 0.</summary>
    public ulong Guid { get; init; }
    public required CarStatus Status { get; init; }
    public bool InPitLane { get; init; }
    public int PitStops { get; init; }
    /// <summary>Tyre grip left in percent, null when unknown.</summary>
    public float? TyrePercent { get; init; }
}

/// <summary>A plugin that drives cars itself (BotDriverPlugin).</summary>
public interface IDrivenCars
{
    /// <summary>The car in this slot when the plugin drives it, else null.</summary>
    DrivenCarInfo? Get(EntryCar car);
    /// <summary>Whether a player's car is in the pit lane, null when the plugin can't tell.</summary>
    bool? PlayerInPitLane(EntryCar car);
    /// <summary>False while the plugin is still starting (loading tracks, calibrating); a track change waits for it.</summary>
    bool Ready { get; }
}

/// <summary>Who may use the admin pages and APIs of all plugins (WebPortalPlugin). Without it only this computer may.</summary>
public interface IAdminWebAccess
{
    /// <summary>Admin pages also from other computers, with the server's admin password.</summary>
    bool RemoteAccess { get; }
}
