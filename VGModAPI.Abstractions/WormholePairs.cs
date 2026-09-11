using System;

namespace VGModAPI;

/// <summary>Immutable declaration for an owned pair of exactly connected native wormholes.</summary>
public sealed class WormholePairDefinition
{
    public string LocalId { get; }
    public int Revision { get; }
    public string Name { get; }
    public WormholePairDefinition(string localId, int revision, string name)
    { LocalId = localId ?? throw new ArgumentNullException(nameof(localId)); Revision = revision; Name = name ?? throw new ArgumentNullException(nameof(name)); }
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
    /// Dissolves the owned pair: removes both native wormhole POIs from their systems and clears the
    /// owned row so the pair no longer reconstructs and its occurrence key becomes creatable again.
    /// Refused while the player's current location or a waypoint is at one of the wormholes — relocating
    /// the player first is the consumer's responsibility. On success this object is terminal
    /// (<see cref="ReconstructionStatus.Dissolved"/>); creating the same occurrence key again authors a
    /// fresh pair with fresh native identity.
    /// </summary>
    WorldContentResult Dissolve();
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
