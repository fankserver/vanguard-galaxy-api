using System;

namespace VGModAPI;

/// <summary>Immutable declaration for an owned pair of exactly connected native wormholes.</summary>
public sealed class WormholePairDefinition
{
    public string LocalId { get; }
    public int Revision { get; }
    public string Name { get; }
    /// <summary>
    /// Keeps both ends of this owned pair free of vanilla decorative traffic: no passerby ships fly
    /// through the rift and no security patrol is created at either end, so a private wormhole stays
    /// private instead of becoming a highway. Docking, services, faction relations and story- or
    /// mission-placed ships are unaffected. Defaults to false (vanilla traffic).
    /// </summary>
    public bool Quiet { get; }
    public WormholePairDefinition(string localId, int revision, string name, bool quiet = false)
    { LocalId = localId ?? throw new ArgumentNullException(nameof(localId)); Revision = revision; Name = name ?? throw new ArgumentNullException(nameof(name)); Quiet = quiet; }

    /// <summary>Binary-compatibility overload for the pre-<c>quiet</c> shape; consumers built against an
    /// earlier API keep working because that call site binds to this exact three-argument signature.</summary>
    public WormholePairDefinition(string localId, int revision, string name)
        : this(localId, revision, name, false) { }
}

public enum WormholePairFailureReason
{
    MissingDefinition, RevisionMismatch, NativeMissing, AmbiguousIdentity, PersistenceUnavailable
}

public sealed class WormholePairState
{
    public ReconstructionStatus Status { get; }
    public WormholePairFailureReason? Reason { get; }
    public string? FirstWormholePoiId { get; }
    public string? SecondWormholePoiId { get; }
    public bool Reconstructed => Status == ReconstructionStatus.Reconstructed;
    public WormholePairState(ReconstructionStatus status,
        WormholePairFailureReason? reason = null, string? firstWormholePoiId = null, string? secondWormholePoiId = null)
    { Status = status; Reason = reason; FirstWormholePoiId = firstWormholePoiId; SecondWormholePoiId = secondWormholePoiId; }
}

/// <summary>An owned, exactly connected native wormhole pair for one captured game.</summary>
public interface IWormholePair
{
    string OccurrenceKey { get; }
    WormholePairDefinition Definition { get; }
    WormholePairState State { get; }
    string? FirstWormholePoiId { get; }
    string? SecondWormholePoiId { get; }
    WorldContentResult LastAction { get; }
    event Action<IWormholePair>? Changed;
    /// <summary>Shows/enables or hides/disables both ends together without changing their exact pairing.</summary>
    WorldContentResult SetOpen(bool open);
    /// <summary>
    /// Removes the owned pair: removes both native wormhole POIs from their systems and clears the
    /// owned row so the pair no longer reconstructs and its occurrence key becomes creatable again.
    /// This is the plain native removal: it refuses only when removal would be impossible or corrupt
    /// save state (the pair is not present natively, or the world is not in an actionable state). It
    /// does not check transient player-safety conditions. To avoid acting while the player's current
    /// location or a waypoint is at a wormhole end, query <see cref="CanRemove"/> first, or use
    /// <see cref="RequestRemoval"/> to defer to the next safe cleanup window. On success this object
    /// is terminal (<see cref="ReconstructionStatus.Removed"/>); creating the same occurrence key
    /// again authors a fresh pair with fresh native identity.
    /// </summary>
    WorldContentResult Remove();
    /// <summary>
    /// Pure readiness report, no mutation: why (if at all) the pair can currently be removed (player
    /// at/routed at an end, not present, session ended, or not yet actionable).
    /// <see cref="WorldContentRemovalStatus.Ready"/> means a cleanup window may remove it now.
    /// </summary>
    WorldContentRemovalStatus CanRemove();
    /// <summary>
    /// Requests deferred removal, mirroring the game's ambient cleanup window: the pair is marked
    /// for removal and removed at the next safe maintenance pass once <see cref="CanRemove"/> is
    /// <see cref="WorldContentRemovalStatus.Ready"/> (offsetting occupancy). Returns a retained
    /// result; completion is signalled by <see cref="Changed"/> with the object becoming terminal
    /// (<see cref="ReconstructionStatus.Removed"/>). Refused when the pair is already gone or the
    /// world is not actionable.
    /// </summary>
    WorldContentResult RequestRemoval();
}

public sealed class WormholePairsSettledEvent
{
    public Guid SessionId { get; }
    public System.Collections.Generic.IReadOnlyList<IWormholePair> Reconstructed { get; }
    public System.Collections.Generic.IReadOnlyList<IWormholePair> Failed { get; }
    public WormholePairsSettledEvent(Guid sessionId,
        System.Collections.Generic.IReadOnlyList<IWormholePair> reconstructed,
        System.Collections.Generic.IReadOnlyList<IWormholePair> failed)
    { SessionId = sessionId; Reconstructed = reconstructed; Failed = failed; }
}
