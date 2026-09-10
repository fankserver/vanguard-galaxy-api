using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

/// <summary>
/// Immutable persistent enclosed-pocket-system declaration. Register before starting a session.
/// Anchored next to an existing system, created with no storyteller (vanilla generates nothing inside),
/// reachable only through a paired entrance jump gate.
/// </summary>
public sealed class AuthoredSystemDefinition
{
    public string LocalId { get; }
    public int Revision { get; }
    public string Name { get; }
    public AuthoredSystemDefinition(string localId, int revision, string name)
    {
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Revision = revision;
    }
}

/// <summary>
/// Author-local occurrence key. The API allocates and owns any native identity (system guid and the
/// paired gate guids); a consumer never supplies a native or instance GUID.
/// </summary>
public sealed class AuthoredSystemReference
{
    public string ProviderId { get; }
    public string LocalId { get; }
    public string OccurrenceKey { get; }
    public AuthoredSystemReference(string providerId, string localId, string occurrenceKey)
    {
        ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
        OccurrenceKey = occurrenceKey ?? throw new ArgumentNullException(nameof(occurrenceKey));
    }
}

public sealed class AuthoredSystemResult
{
    public WorldStatus Status { get; }
    public AuthoredSystemReference? Reference { get; }
    /// <summary>Owned pocket-system identity accepted by gameplay travel targets; not an authorization token.</summary>
    public string? SystemId { get; }
    public string? EntranceGatePoiId { get; }
    public string? PocketGatePoiId { get; }
    public bool Succeeded => Status == WorldStatus.Succeeded;
    public AuthoredSystemResult(WorldStatus status, AuthoredSystemReference? reference = null,
        string? systemId = null, string? entranceGatePoiId = null, string? pocketGatePoiId = null)
    {
        Status = status; Reference = reference;
        SystemId = systemId; EntranceGatePoiId = entranceGatePoiId; PocketGatePoiId = pocketGatePoiId;
    }
}

public enum AuthoredSystemReconstructionStatus { Reconstructed, Pending, Failed }

public enum AuthoredSystemFailureReason
{
    MissingDefinition, RevisionMismatch, NativeMissing, AmbiguousIdentity, PersistenceUnavailable
}

/// <summary>Typed per-occurrence reconciliation state, never a lifecycle marker or an admission token.</summary>
public sealed class AuthoredSystemReconstructionState
{
    public AuthoredSystemReconstructionStatus Status { get; }
    public AuthoredSystemFailureReason? Reason { get; }
    /// <summary>Four-segment native identity populated only when the owned occurrence is present.</summary>
    public string? SystemId { get; }
    public string? EntranceGatePoiId { get; }
    public string? PocketGatePoiId { get; }
    public bool Reconstructed => Status == AuthoredSystemReconstructionStatus.Reconstructed;
    public AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus status, AuthoredSystemFailureReason? reason = null,
        string? systemId = null, string? entranceGatePoiId = null, string? pocketGatePoiId = null)
    {
        Status = status; Reason = reason;
        SystemId = systemId; EntranceGatePoiId = entranceGatePoiId; PocketGatePoiId = pocketGatePoiId;
    }
}

/// <summary>A single reconstructed occurrence that did not settle in a reconstructed state.</summary>
public sealed class AuthoredSystemFailure
{
    public AuthoredSystemReference Reference { get; }
    public AuthoredSystemFailureReason Reason { get; }
    public AuthoredSystemFailure(AuthoredSystemReference reference, AuthoredSystemFailureReason reason)
    {
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        Reason = reason;
    }
}

/// <summary>
/// Reports actual reconciliation outcomes once per session at the post-reconstruction safe boundary.
/// An empty failure list means every declared occurrence reconstructed.
/// </summary>
public sealed class ReconstructionSettledEvent
{
    public Guid SessionId { get; }
    public IReadOnlyList<AuthoredSystemFailure> Failures { get; }
    public bool HasFailures => Failures.Count > 0;
    public ReconstructionSettledEvent(Guid sessionId, IReadOnlyList<AuthoredSystemFailure> failures)
    {
        SessionId = sessionId;
        if (failures == null) throw new ArgumentNullException(nameof(failures));
        var copy = new List<AuthoredSystemFailure>(failures);
        Failures = new ReadOnlyCollection<AuthoredSystemFailure>(copy);
    }
}
