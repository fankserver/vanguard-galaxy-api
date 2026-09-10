using System;

namespace VGModAPI;

/// <summary>Immutable declaration for an owned pair of exactly connected native wormholes.</summary>
public sealed class AuthoredWormholePairDefinition
{
    public string LocalId { get; }
    public int Revision { get; }
    public string Name { get; }
    public AuthoredWormholePairDefinition(string localId, int revision, string name)
    { LocalId = localId ?? throw new ArgumentNullException(nameof(localId)); Revision = revision; Name = name ?? throw new ArgumentNullException(nameof(name)); }
}

public enum AuthoredWormholePairFailureReason
{
    MissingDefinition, RevisionMismatch, NativeMissing, AmbiguousIdentity, PersistenceUnavailable
}

public sealed class AuthoredWormholePairState
{
    public AuthoredSystemReconstructionStatus Status { get; }
    public AuthoredWormholePairFailureReason? Reason { get; }
    public string? FirstWormholePoiId { get; }
    public string? SecondWormholePoiId { get; }
    public bool Reconstructed => Status == AuthoredSystemReconstructionStatus.Reconstructed;
    public AuthoredWormholePairState(AuthoredSystemReconstructionStatus status,
        AuthoredWormholePairFailureReason? reason = null, string? firstWormholePoiId = null, string? secondWormholePoiId = null)
    { Status = status; Reason = reason; FirstWormholePoiId = firstWormholePoiId; SecondWormholePoiId = secondWormholePoiId; }
}

/// <summary>An owned, exactly connected native wormhole pair for one captured game.</summary>
public interface IAuthoredWormholePair
{
    string OccurrenceKey { get; }
    AuthoredWormholePairDefinition Definition { get; }
    AuthoredWormholePairState State { get; }
    string? FirstWormholePoiId { get; }
    string? SecondWormholePoiId { get; }
    AuthoredActionResult LastAction { get; }
    event Action<IAuthoredWormholePair>? Changed;
    /// <summary>Shows/enables or hides/disables both ends together without changing their exact pairing.</summary>
    AuthoredActionResult SetOpen(bool open);
}

public sealed class AuthoredWormholePairsSettledEvent
{
    public Guid SessionId { get; }
    public System.Collections.Generic.IReadOnlyList<IAuthoredWormholePair> Reconstructed { get; }
    public System.Collections.Generic.IReadOnlyList<IAuthoredWormholePair> Failed { get; }
    public AuthoredWormholePairsSettledEvent(Guid sessionId,
        System.Collections.Generic.IReadOnlyList<IAuthoredWormholePair> reconstructed,
        System.Collections.Generic.IReadOnlyList<IAuthoredWormholePair> failed)
    { SessionId = sessionId; Reconstructed = reconstructed; Failed = failed; }
}
