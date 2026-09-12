using System;

namespace VGModAPI;

/// <summary>Typed per-poi combat-site state, never a lifecycle marker or an admission token.</summary>
public sealed class CombatSiteState
{
    public ReconstructionStatus Status { get; }
    public ReconstructionFailureReason? Reason { get; }
    /// <summary>Native POI identity accepted by story travel objectives; populated only while reconstructed.</summary>
    public string? PoiId { get; }
    public bool Reconstructed => Status == ReconstructionStatus.Reconstructed;
    public CombatSiteState(ReconstructionStatus status, ReconstructionFailureReason? reason = null, string? poiId = null)
    { Status = status; Reason = reason; PoiId = poiId; }
}

/// <summary>
/// One owned persistent combat-site poi for a single captured game, following the uniform
/// poi contract: the consumer names the poi with an author-local key, the API allocates
/// and owns the native identity, and re-creating or re-obtaining the same key returns the SAME object
/// instance for the life of the owning session. An poi from a session that has ended or been
/// replaced keeps its last state and never resolves against the replacement save — re-obtain the
/// object for the live game explicitly.
/// </summary>
public sealed class CombatSiteFailure
{
    public ICombatSite Poi { get; }
    public ReconstructionFailureReason Reason { get; }
    public CombatSiteFailure(ICombatSite poi, ReconstructionFailureReason reason)
    { Poi = poi ?? throw new ArgumentNullException(nameof(poi)); Reason = reason; }
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
    /// <summary>The author-local poi key this poi is owned under.</summary>
    string PoiKey { get; }
    /// <summary>The declaration this poi belongs to (live revision after migration).</summary>
    CombatSiteDefinition Definition { get; }
    /// <summary>Current typed per-poi state (Reconstructed / Pending / Failed(reason)).</summary>
    CombatSiteState State { get; }
    /// <summary>Native POI identity; populated only while <see cref="State"/> is reconstructed.</summary>
    string? PoiId { get; }
    /// <summary>The retained result of the most recent action on this poi (creation included).</summary>
    WorldContentResult LastAction { get; }
    /// <summary>Fired when this poi's observed state changes within its own session.</summary>
    event Action<ICombatSite>? Changed;
    /// <summary>
    /// Removes the owned combat site: removes its native POI from the host system and drops its
    /// poi key so save data records it as intentionally absent rather than reconstructing it
    /// as a failure. This is the plain native removal: it refuses only when removal would be
    /// impossible or corrupt save state (the site is not present natively, or the world is not in an
    /// actionable state). It does not check transient player-safety conditions. To avoid acting
    /// while the player is at or routed to the site, query <see cref="CanRemove"/> first, or use
    /// <see cref="RequestRemoval"/> to defer to the next safe cleanup window. On success this
    /// object is terminal (<see cref="ReconstructionStatus.Removed"/>); creating the same
    /// poi key again authors a fresh site with fresh native identity.
    /// </summary>
    WorldContentResult Remove();
    /// <summary>
    /// Pure readiness report, no mutation: why (if at all) the combat site can currently be removed
    /// (player at/routed, not present, session ended, or not yet actionable).
    /// <see cref="RemovalStatus.Ready"/> means a cleanup window may remove it now.
    /// </summary>
    RemovalStatus CanRemove();
    /// <summary>
    /// Requests deferred removal, mirroring the game's ambient cleanup window: the combat site is
    /// marked for removal and removed at the next safe maintenance pass once
    /// <see cref="CanRemove"/> is <see cref="RemovalStatus.Ready"/> (offsetting
    /// occupancy). Returns a retained result; completion is signalled by <see cref="Changed"/> with
    /// the object becoming terminal (<see cref="ReconstructionStatus.Removed"/>). Refused when the
    /// site is already gone or the world is not actionable.
    /// </summary>
    WorldContentResult RequestRemoval();
}
