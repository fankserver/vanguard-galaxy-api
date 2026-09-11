using System;

namespace VGModAPI;

/// <summary>
/// Keeps owned locations visually quiet by suppressing vanilla's decorative passerby traffic
/// there. Docking, station services, faction relations and story- or mission-placed ships are
/// unaffected, and nothing is written to saves.
/// </summary>
public interface IAmbientTrafficService : IServiceStatus
{
    /// <summary>
    /// Quiets decorative visitor traffic at one station. Everything else stays vanilla,
    /// including jump gates in the same system.
    /// </summary>
    /// <param name="key">Optional author-scoped declaration key: re-declaring the same key replaces
    /// only your previous declaration; disposing a superseded handle is inert.</param>
    IDisposable SuppressAtStation(string stationId, string? key = null);
    /// <summary>
    /// Fully quiets one wormhole: no decorative passerby traffic flies through it and no security
    /// patrol is created there — an owned wormhole stays a private door rather than a highway.
    /// Docking, services, faction relations and story- or mission-placed ships are unaffected.
    /// </summary>
    /// <param name="key">Optional author-scoped declaration key: re-declaring the same key replaces
    /// only your previous declaration; disposing a superseded handle is inert.</param>
    IDisposable SuppressAtWormhole(string wormholePoiId, string? key = null);
    /// <summary>
    /// Quiets decorative station, gate and wormhole traffic throughout the single system that
    /// contains the anchor location. Neighbouring systems stay vanilla, including a suppressed
    /// gate's peer gate. Security patrols are deliberately left alone here; use
    /// <see cref="SuppressAtWormhole"/> to quiet one wormhole completely.
    /// </summary>
    IDisposable SuppressInSystemContaining(string poiId, string? key = null);
}
