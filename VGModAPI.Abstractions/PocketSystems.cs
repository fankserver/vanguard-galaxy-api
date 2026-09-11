using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

/// <summary>Where an authored pocket system is placed on the galaxy map.</summary>
public enum PocketSystemPlacement
{
    /// <summary>
    /// The pocket is placed in a remote, sparsely-populated sector away from colonized space — reachable
    /// only through its paired entrance jump gate, off the settled belt/galaxy map. Default placement.
    /// </summary>
    OffMap,
    /// <summary>
    /// The pocket is placed in the anchor's own sector at a position well away from existing systems, so it
    /// renders as a clearly separate system on the settled belt/galaxy map. Still a single sealed gate pair
    /// linking it to the anchor (the consumer still supplies an anchor system id); the pocket has no
    /// storyteller and stays enclosed.
    /// </summary>
    Visible
}

/// <summary>
/// Immutable persistent enclosed-pocket-system declaration. Register before starting a session.
/// Anchored next to an existing system, created with no storyteller (vanilla generates nothing inside),
/// reachable only through a paired entrance jump gate.
/// </summary>
public sealed class PocketSystemDefinition
{
    public string LocalId { get; }
    public int Revision { get; }
    public string Name { get; }
    /// <summary>Whether the pocket is placed off the settled map (default) or as a visible adjacent system.</summary>
    public PocketSystemPlacement Placement { get; }
    /// <summary>
    /// Optional owning faction identifier (for example "Marauders"). When null or not a faction the game
    /// knows, the system is authored with no owner, so the map shows no "Controlled by" line (unknown).
    /// </summary>
    public string? FactionId { get; }
    public PocketSystemDefinition(string localId, int revision, string name, PocketSystemPlacement placement = PocketSystemPlacement.OffMap, string? factionId = null)
    {
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Revision = revision;
        Placement = placement;
        FactionId = factionId;
    }
}

/// <summary>
/// Author-local occurrence key. The API allocates and owns any native identity (system guid and the
/// paired gate guids); a consumer never supplies a native or occurrence GUID. This type is the internal
/// coordinator keying shape; consumers address an occurrence through its <see cref="IPocketSystem"/> object.
/// </summary>
public sealed class PocketSystemReference
{
    public string ProviderId { get; }
    public string LocalId { get; }
    public string OccurrenceKey { get; }
    public PocketSystemReference(string providerId, string localId, string occurrenceKey)
    {
        ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
        OccurrenceKey = occurrenceKey ?? throw new ArgumentNullException(nameof(occurrenceKey));
    }
}

/// <summary>Internal coordinator creation outcome; consumers receive an <see cref="IPocketSystem"/> object instead.</summary>
public sealed class PocketSystemResult
{
    public WorldStatus Status { get; }
    public PocketSystemReference? Reference { get; }
    /// <summary>Owned pocket-system identity accepted by gameplay travel targets; not an authorization token.</summary>
    public string? SystemId { get; }
    public string? EntranceGatePoiId { get; }
    public string? PocketGatePoiId { get; }
    public bool Succeeded => Status == WorldStatus.Succeeded;
    public PocketSystemResult(WorldStatus status, PocketSystemReference? reference = null,
        string? systemId = null, string? entranceGatePoiId = null, string? pocketGatePoiId = null)
    {
        Status = status; Reference = reference;
        SystemId = systemId; EntranceGatePoiId = entranceGatePoiId; PocketGatePoiId = pocketGatePoiId;
    }
}

public enum ReconstructionStatus
{
    Reconstructed, Pending, Failed,
    /// <summary>The occurrence was dissolved by its owner; terminal for this object. The same occurrence key may author a fresh pocket later.</summary>
    Dissolved
}

public enum ReconstructionFailureReason
{
    MissingDefinition, RevisionMismatch, NativeMissing, AmbiguousIdentity, PersistenceUnavailable
}

/// <summary>Typed per-occurrence reconciliation state, never a lifecycle marker or an admission token.</summary>
public sealed class PocketSystemState
{
    public ReconstructionStatus Status { get; }
    public ReconstructionFailureReason? Reason { get; }
    /// <summary>Four-segment native identity populated only when the owned occurrence is present.</summary>
    public string? SystemId { get; }
    public string? EntranceGatePoiId { get; }
    public string? PocketGatePoiId { get; }
    public bool Reconstructed => Status == ReconstructionStatus.Reconstructed;
    public PocketSystemState(ReconstructionStatus status, ReconstructionFailureReason? reason = null,
        string? systemId = null, string? entranceGatePoiId = null, string? pocketGatePoiId = null)
    {
        Status = status; Reason = reason;
        SystemId = systemId; EntranceGatePoiId = entranceGatePoiId; PocketGatePoiId = pocketGatePoiId;
    }
}

/// <summary>Outcome of an action performed on an owned authored-system occurrence.</summary>
public enum WorldContentStatus
{
    /// <summary>The action was applied to the native state and its declared outcome retained.</summary>
    Succeeded,
    /// <summary>The action was refused for a content/state reason (for example a gate that cannot be matched).</summary>
    Rejected,
    /// <summary>A temporary lifecycle state (not gameplay-initialized yet, or a save callback is dispatching) blocked the action.</summary>
    NotReady,
    /// <summary>The world/service layer is unavailable (no authoring capability or the plugin is gone).</summary>
    Unavailable,
    /// <summary>The owning session ended or was replaced; the occurrence can no longer act and must be re-obtained for the live game.</summary>
    GameEnded
}

/// <summary>Retained result of an action on an owned occurrence. Terminal outcomes stay stable until the next action.</summary>
public sealed class WorldContentResult
{
    public WorldContentStatus Status { get; }
    public string Detail { get; }
    public bool Succeeded => Status == WorldContentStatus.Succeeded;
    internal WorldContentResult(WorldContentStatus status, string detail = "")
    { Status = status; Detail = detail ?? ""; }
}

/// <summary>
/// One owned authored-pocket-system occurrence for a single captured game. The API owns every native
/// identity; the consumer names the occurrence with an author-local key. Re-creating or re-obtaining the
/// same key returns the SAME object occurrence for the life of the owning session (keyed reconciliation
/// surfaces as object identity, never a duplicate). An occurrence from a session that has ended or been
/// replaced refuses its actions with <see cref="WorldContentStatus.GameEnded"/> rather than touching the
/// replacement save — re-obtain the objects for the live game explicitly.
/// </summary>
public interface IPocketSystem
{
    /// <summary>The author-local occurrence key this occurrence is owned under.</summary>
    string OccurrenceKey { get; }
    /// <summary>The declaration this occurrence belongs to (live revision after migration).</summary>
    PocketSystemDefinition Definition { get; }
    /// <summary>Current typed per-occurrence reconciliation state (Reconstructed / Pending / Failed(reason)).</summary>
    PocketSystemState State { get; }
    /// <summary>Owned pocket-system identity; populated only while <see cref="State"/> is <see cref="ReconstructionStatus.Reconstructed"/>.</summary>
    string? SystemId { get; }
    /// <summary>Owned entrance-gate identity; populated only while reconstructed.</summary>
    string? EntranceGatePoiId { get; }
    /// <summary>Owned pocket-side gate identity; populated only while reconstructed.</summary>
    string? PocketGatePoiId { get; }
    /// <summary>The retained result of the most recent action on this occurrence.</summary>
    WorldContentResult LastAction { get; }
    /// <summary>
    /// Fired when this occurrence's reconciliation state changes within its own session (for example
    /// Pending → Reconstructed on load, or Pending → Failed at the settle boundary). Not fired for the
    /// object's own actions; gate application is a retained action result, not a state transition.
    /// </summary>
    event Action<IPocketSystem>? Changed;
    /// <summary>
    /// Declarative entrance-gate state, persisted as supported state. Unhides and opens/closes both
    /// paired gates together. Returns a retained result; the phase/dispatch gates are enforced here.
    /// </summary>
    WorldContentResult SetEntranceOpen(bool open);
    /// <summary>
    /// Dissolves the owned pocket: removes the pocket system, both paired gates and this API's
    /// authored sites inside it from the live map and from save data. Refused while the player's
    /// current system, current location or any waypoint is inside the pocket — relocating the player
    /// first is the consumer's responsibility — while the pocket still contains combat sites, and
    /// while the pocket is still an endpoint of a wormhole (dissolve the wormhole first).
    /// On success this object is terminal (<see cref="ReconstructionStatus.Dissolved"/>);
    /// creating the same occurrence key again authors a fresh pocket with fresh native identity.
    /// </summary>
    WorldContentResult Dissolve();
}

/// <summary>
/// A single reconciled occurrence that did not settle in a reconstructed state.
/// Carries the owned occurrence object; the internal coordinator also builds reference-backed occurrences.
/// </summary>
public sealed class ReconstructionFailure
{
    /// <summary>Internal coordinator keying; null on consumer-built failures.</summary>
    public PocketSystemReference? Reference { get; }
    /// <summary>The owned occurrence that failed; null on internal coordinator-built failures.</summary>
    public IPocketSystem? Occurrence { get; }
    public ReconstructionFailureReason Reason { get; }
    /// <summary>Internal coordinator construction.</summary>
    public ReconstructionFailure(PocketSystemReference reference, ReconstructionFailureReason reason)
    {
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        Reason = reason;
    }
    /// <summary>Consumer construction over an owned occurrence.</summary>
    public ReconstructionFailure(IPocketSystem occurrence, ReconstructionFailureReason reason)
    {
        Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence));
        Reason = reason;
    }
}

/// <summary>
/// Reports actual reconciliation outcomes once per session at the post-reconstruction safe boundary,
/// carrying the owned occurrence objects. An empty failure list means every declared occurrence
/// reconstructed. This is the consumer-facing aggregate boundary event.
/// </summary>
public sealed class PocketSystemsSettledEvent
{
    public Guid SessionId { get; }
    public IReadOnlyList<IPocketSystem> Reconstructed { get; }
    public IReadOnlyList<ReconstructionFailure> Failures { get; }
    public bool HasFailures => Failures.Count > 0;
    public PocketSystemsSettledEvent(Guid sessionId, IEnumerable<IPocketSystem> reconstructed, IEnumerable<ReconstructionFailure> failures)
    {
        SessionId = sessionId;
        if (reconstructed == null) throw new ArgumentNullException(nameof(reconstructed));
        if (failures == null) throw new ArgumentNullException(nameof(failures));
        var rec = new List<IPocketSystem>(reconstructed);
        Reconstructed = new ReadOnlyCollection<IPocketSystem>(rec);
        var fail = new List<ReconstructionFailure>(failures);
        Failures = new ReadOnlyCollection<ReconstructionFailure>(fail);
    }
}

/// <summary>
/// Internal coordinator boundary event (session + reference-backed failures). Consumers use the
/// provider-level <see cref="PocketSystemsSettledEvent"/> via <see cref="IWorldProvider.PocketSystemReconstructionSettled"/>.
/// </summary>
public sealed class ReconstructionSettledEvent
{
    public Guid SessionId { get; }
    public IReadOnlyList<ReconstructionFailure> Failures { get; }
    public bool HasFailures => Failures.Count > 0;
    public ReconstructionSettledEvent(Guid sessionId, IReadOnlyList<ReconstructionFailure> failures)
    {
        SessionId = sessionId;
        if (failures == null) throw new ArgumentNullException(nameof(failures));
        var copy = new List<ReconstructionFailure>(failures);
        Failures = new ReadOnlyCollection<ReconstructionFailure>(copy);
    }
}
