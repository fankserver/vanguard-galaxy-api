using System;

namespace VGModAPI;

/// <summary>
/// Keeps authored locations visually quiet by suppressing vanilla's decorative passerby traffic
/// there. Docking, station services, faction relations and story- or mission-placed ships are
/// unaffected, and nothing is written to saves.
/// </summary>
public interface IAmbientTrafficService : IServiceStatus
{
    /// <summary>
    /// Quiets decorative visitor traffic at one station. Everything else stays vanilla,
    /// including jump gates in the same system.
    /// </summary>
    IDisposable SuppressAtStation(string stationId);
    /// <summary>
    /// Quiets decorative station and gate traffic throughout the single system that contains the
    /// anchor location. Neighbouring systems stay vanilla, including a suppressed gate's peer gate.
    /// </summary>
    IDisposable SuppressInSystemContaining(string poiId);
}
