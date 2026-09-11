using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI;

/// <summary>
/// Instance-scoped tuning of one unit's drone bay for an owned encounter. Only supplied aspects
/// change; everything else stays vanilla. Nothing is written to saves, and disposing the
/// declaration restores stock behavior for later launches and replacements.
/// </summary>
public sealed class DroneBayTuning
{
    /// <summary>Seconds per drone launch transition; the game's stock value is 1.5.</summary>
    public double? LaunchSeconds { get; }
    /// <summary>
    /// Drone catalog names the bay reproduces when replacing losses, cycled deterministically, so an
    /// owned composition holds for the whole fight. An unknown name is skipped with one report.
    /// </summary>
    public IReadOnlyList<string>? ReplacementDrones { get; }
    /// <summary>
    /// Desired docked complement. The bay is rebuilt through the game's own drone initialisation in
    /// staggered batches, then deploys, so owned drones behave exactly like natively created ones.
    /// </summary>
    public int? Complement { get; }
    public DroneBayTuning(double? launchSeconds = null, IReadOnlyList<string>? replacementDrones = null, int? complement = null)
    {
        if (launchSeconds is { } launch && (double.IsNaN(launch) || launch < 0.01 || launch > 600))
            throw new ArgumentOutOfRangeException(nameof(launchSeconds));
        if (replacementDrones != null)
        {
            if (replacementDrones.Count is 0 or > 8) throw new ArgumentException("1-8 replacement drones are required.", nameof(replacementDrones));
            ReplacementDrones = Array.AsReadOnly(replacementDrones
                .Select(name => CharacterText.Check(name, 128, nameof(replacementDrones))).ToArray());
        }
        if (complement is { } count && count is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(complement));
        if (launchSeconds == null && replacementDrones == null && complement == null)
            throw new ArgumentException("At least one tuning aspect is required.");
        LaunchSeconds = launchSeconds; Complement = complement;
    }
}

/// <summary>
/// Tunes drone bays per exactly identified unit instance. Identification is the unit's persistent
/// unit-data identity, so a ship of the same class — including the player's — is never affected.
/// </summary>
public interface IDroneBayService : IServiceStatus
{
    /// <summary>
    /// Applies tuning to the drone bay of the one unit whose persistent identity matches. Later
    /// declarations override earlier ones per aspect for the same unit. Declare once and retain;
    /// dispose when the encounter ends.
    /// </summary>
    /// <param name="key">Optional author-scoped declaration key: re-declaring the same key replaces
    /// only your previous declaration; disposing a superseded handle is inert.</param>
    IDisposable Tune(string unitId, DroneBayTuning tuning, string? key = null);
}
