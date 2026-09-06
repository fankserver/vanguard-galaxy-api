using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI;

public enum MissionTransitionKind { Restored, Accepted, Completed, Failed, Abandoned, Removed, Archived }

public enum MissionIdentityEvidence { SessionOnly, SavedSnapshotMatch, MissingOrAmbiguous, Unavailable }

/// <summary>Immutable observation, not a mission factory or persistent history record.</summary>
public sealed class MissionSnapshot
{
    public Guid SessionId { get; }
    public Guid InstanceId { get; }
    public string? DefinitionId { get; }
    public string Name { get; }
    public IReadOnlyList<string> ObjectiveTags { get; }
    public bool AcceptanceObserved { get; }
    public MissionIdentityEvidence IdentityEvidence { get; }
    public MissionSnapshot(Guid sessionId, Guid instanceId, string? definitionId, string name, IEnumerable<string> objectiveTags, bool acceptanceObserved)
        : this(sessionId, instanceId, definitionId, name, objectiveTags, acceptanceObserved, MissionIdentityEvidence.SessionOnly) { }
    public MissionSnapshot(Guid sessionId, Guid instanceId, string? definitionId, string name, IEnumerable<string> objectiveTags, bool acceptanceObserved, MissionIdentityEvidence identityEvidence)
    {
        if (!Enum.IsDefined(typeof(MissionIdentityEvidence), identityEvidence)) throw new ArgumentOutOfRangeException(nameof(identityEvidence));
        IdentityEvidence = identityEvidence;
        if (sessionId == Guid.Empty || instanceId == Guid.Empty) throw new ArgumentException("Session and live instance identities are required.");
        SessionId = sessionId; InstanceId = instanceId; DefinitionId = definitionId;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ObjectiveTags = Array.AsReadOnly((objectiveTags ?? throw new ArgumentNullException(nameof(objectiveTags))).Select(tag => tag ?? throw new ArgumentException("Null objective tag.")).Distinct(StringComparer.Ordinal).ToArray());
        AcceptanceObserved = acceptanceObserved;
    }
}

public sealed class MissionTransition
{
    public MissionTransitionKind Kind { get; }
    public MissionSnapshot Mission { get; }
    public long Sequence { get; }
    public MissionTransition(MissionTransitionKind kind, MissionSnapshot mission, long sequence)
    {
        if (!Enum.IsDefined(typeof(MissionTransitionKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        Kind = kind; Mission = mission ?? throw new ArgumentNullException(nameof(mission)); Sequence = sequence;
    }
}

/// <summary>Explicitly version-sensitive, read-only escape hatch. Never retain or mutate the returned object.</summary>
public interface IVersionSensitiveMissionAccess
{
    /// <summary>Only resolves the exact snapshot currently being dispatched, on the main thread. No stable native shape is promised.</summary>
    bool TryGetNative(MissionSnapshot snapshot, out object? native);
}

/// <summary>Optional observed transitions; all access is main-thread-only. History belongs to consumers.</summary>
public interface IMissionEvents
{
    IDisposable Subscribe(string owner, Action<MissionTransition> callback);
}
