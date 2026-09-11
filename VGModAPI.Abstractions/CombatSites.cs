using System;

namespace VGModAPI;

/// <summary>Typed per-occurrence combat-site state, never a lifecycle marker or an admission token.</summary>
public sealed class CombatSiteState
{
    public AuthoredSystemReconstructionStatus Status { get; }
    public AuthoredSystemFailureReason? Reason { get; }
    /// <summary>Native POI identity accepted by story travel objectives; populated only while reconstructed.</summary>
    public string? PoiId { get; }
    public bool Reconstructed => Status == AuthoredSystemReconstructionStatus.Reconstructed;
    public CombatSiteState(AuthoredSystemReconstructionStatus status, AuthoredSystemFailureReason? reason = null, string? poiId = null)
    { Status = status; Reason = reason; PoiId = poiId; }
}

/// <summary>
/// One owned persistent combat-site occurrence for a single captured game, following the uniform
/// occurrence contract: the consumer names the occurrence with an author-local key, the API allocates
/// and owns the native identity, and re-creating or re-obtaining the same key returns the SAME object
/// instance for the life of the owning session. An occurrence from a session that has ended or been
/// replaced keeps its last state and never resolves against the replacement save — re-obtain the
/// object for the live game explicitly.
/// </summary>
public sealed class CombatSiteFailure
{
    public ICombatSite Occurrence { get; }
    public AuthoredSystemFailureReason Reason { get; }
    public CombatSiteFailure(ICombatSite occurrence, AuthoredSystemFailureReason reason)
    { Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence)); Reason = reason; }
}

/// <summary>Once-per-session aggregate reconciliation report for keyed combat sites at the post-reconstruction safe boundary.</summary>
public sealed class CombatSitesSettledEvent
{
    public Guid SessionId { get; }
    public System.Collections.Generic.IReadOnlyList<ICombatSite> Reconstructed { get; }
    public System.Collections.Generic.IReadOnlyList<CombatSiteFailure> Failures { get; }
    public bool HasFailures => Failures.Count > 0;
    public CombatSitesSettledEvent(Guid sessionId, System.Collections.Generic.IEnumerable<ICombatSite> reconstructed, System.Collections.Generic.IEnumerable<CombatSiteFailure> failures)
    {
        SessionId = sessionId;
        Reconstructed = new System.Collections.ObjectModel.ReadOnlyCollection<ICombatSite>(new System.Collections.Generic.List<ICombatSite>(reconstructed ?? throw new ArgumentNullException(nameof(reconstructed))));
        Failures = new System.Collections.ObjectModel.ReadOnlyCollection<CombatSiteFailure>(new System.Collections.Generic.List<CombatSiteFailure>(failures ?? throw new ArgumentNullException(nameof(failures))));
    }
}

public interface ICombatSite
{
    /// <summary>The author-local occurrence key this occurrence is owned under.</summary>
    string OccurrenceKey { get; }
    /// <summary>The declaration this occurrence belongs to (live revision after migration).</summary>
    WorldCombatSiteDefinition Definition { get; }
    /// <summary>Current typed per-occurrence state (Reconstructed / Pending / Failed(reason)).</summary>
    CombatSiteState State { get; }
    /// <summary>Native POI identity; populated only while <see cref="State"/> is reconstructed.</summary>
    string? PoiId { get; }
    /// <summary>The retained result of the most recent action on this occurrence (creation included).</summary>
    AuthoredActionResult LastAction { get; }
    /// <summary>Fired when this occurrence's observed state changes within its own session.</summary>
    event Action<ICombatSite>? Changed;
}
