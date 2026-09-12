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
    Visible,
    /// <summary>
    /// The pocket allocates a subsector of its own, placed among the ordinary frontier subsectors so it
    /// renders on the galaxy map next to colonised space, and named by the definition's
    /// <see cref="PocketSystemDefinition.SectorName"/>. It is deliberately NOT linked by a sector jump gate:
    /// the wormhole/gate pair remains the only way in, and no sector line is drawn to it. Other systems can
    /// join this subsector by declaring <see cref="Visible"/> with a system already inside it as the anchor.
    /// </summary>
    OwnSector
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
    /// knows, the pocket inherits the anchor system's faction, so the map shows a real "Controlled by" line.
    /// </summary>
    public string? FactionId { get; }
    /// <summary>
    /// Optional name for the subsector this pocket creates when it is placed <see cref="PocketSystemPlacement.OffMap"/>.
    /// An OffMap pocket allocates its own remote subsector; naming it makes the resulting place a properly
    /// named cluster instead of an auto-generated one. Ignored for <see cref="PocketSystemPlacement.Visible"/>,
    /// which reuses its anchor's subsector. A pocket whose anchor is another pocket's system therefore joins
    /// that cluster's subsector, which is how a multi-system cluster is assembled.
    /// </summary>
    public string? SectorName { get; }
    /// <summary>
    /// Makes this authored system silent: no decorative visitor traffic at its stations, no passerby
    /// traffic at its gates, no wormhole traffic, and no security patrols anywhere in it. Use it for an
    /// authored cluster that should feel like your own private place rather than a thoroughfare.
    /// Docking, services, faction relations and story- or mission-placed ships are unaffected.
    /// </summary>
    public bool Quiet { get; }
    public PocketSystemDefinition(string localId, int revision, string name, PocketSystemPlacement placement = PocketSystemPlacement.OffMap, string? factionId = null, string? sectorName = null, bool quiet = false)
    {
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId));        Name = name ?? throw new ArgumentNullException(nameof(name));
        Revision = revision;
        Placement = placement;
        FactionId = factionId;
        SectorName = sectorName;
        Quiet = quiet;
    }

    /// <summary>Binary-compatibility overload for the pre-<c>sectorName</c> shape; consumers built against an
    /// earlier API keep working because that call site binds to this exact five-argument signature.</summary>
    public PocketSystemDefinition(string localId, int revision, string name, PocketSystemPlacement placement, string? factionId)
        : this(localId, revision, name, placement, factionId, null, false) { }
}

/// <summary>
/// Author-local poi key. The API allocates and owns any native identity (system guid and the
/// paired gate guids); a consumer never supplies a native or poi GUID. This type is the internal
/// coordinator keying shape; consumers address an poi through its <see cref="IPocketSystem"/> object.
/// </summary>
public sealed class PocketSystemReference
{
    public string ProviderId { get; }
    public string LocalId { get; }
    public string PoiKey { get; }
    public PocketSystemReference(string providerId, string localId, string poiKey)
    {
        ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
        PoiKey = poiKey ?? throw new ArgumentNullException(nameof(poiKey));
    }
}

/// <summary>Internal coordinator creation outcome; consumers receive an <see cref="IPocketSystem"/> object instead.</summary>
public sealed class PocketSystemResult
{
    public WorldContentStatus Status { get; }
    public PocketSystemReference? Reference { get; }
    /// <summary>Owned pocket-system identity accepted by gameplay travel targets; not an authorization token.</summary>
    public string? SystemId { get; }
    public string? EntranceGatePoiId { get; }
    public string? PocketGatePoiId { get; }
    public bool Succeeded => Status == WorldContentStatus.Succeeded;
    public PocketSystemResult(WorldContentStatus status, PocketSystemReference? reference = null,
        string? systemId = null, string? entranceGatePoiId = null, string? pocketGatePoiId = null)
    {
        Status = status; Reference = reference;
        SystemId = systemId; EntranceGatePoiId = entranceGatePoiId; PocketGatePoiId = pocketGatePoiId;
    }
}

public enum ReconstructionStatus
{
    Reconstructed, Pending, Failed,
    /// <summary>The poi was removed by its owner; terminal for this object. The same poi key may author a fresh pocket later.</summary>
    Removed
}

public enum ReconstructionFailureReason
{
    MissingDefinition, RevisionMismatch, NativeMissing, AmbiguousIdentity, PersistenceUnavailable
}

/// <summary>Typed per-poi reconciliation state, never a lifecycle marker or an admission token.</summary>
public sealed class PocketSystemState
{
    public ReconstructionStatus Status { get; }
    public ReconstructionFailureReason? Reason { get; }
    /// <summary>Four-segment native identity populated only when the owned poi is present.</summary>
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

/// <summary>Outcome of a world-content operation: a declaration (<c>Register*</c>) or an action
/// performed on an owned authored-system poi. One outcome axis for both; the lifecycle/state axis is
/// <see cref="ReconstructionStatus"/> and removal readiness is <see cref="RemovalStatus"/>.</summary>
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
    /// <summary>The owning session ended or was replaced; the poi can no longer act and must be re-obtained for the live game.</summary>
    GameEnded,
    /// <summary>The owning provider is not present or the service is not available.</summary>
    UnknownProvider,
    /// <summary>A declaration with the same author-local key/id already exists.</summary>
    DuplicateDefinition,
    /// <summary>The declaration is malformed (null or rejected by the definition contract).</summary>
    InvalidDefinition,
    /// <summary>No declaration with that identifier is registered by this provider.</summary>
    NotRegistered
}

/// <summary>
/// A typed, read-only report of why (if at all) an owned world-content poi can currently be
/// removed. Returned by <c>CanRemove()</c>; it never mutates native state. A value of
/// <see cref="Ready"/> means a cleanup window may act now. The distinct reasons are a joint report
/// across the four poi kinds; a kind only ever reports the reasons that apply to it.
/// </summary>
public enum RemovalStatus
{
    /// <summary>Removal can proceed now; it is safe for a cleanup window to <c>Remove()</c> or complete a <c>RequestRemoval()</c>.</summary>
    Ready,
    /// <summary>The player's current location or a waypoint is at the poi's POI; synchronous removal would act under the player.</summary>
    PlayerInside,
    /// <summary>A live boarding operation holds the poi's station; it cannot be removed while boarded.</summary>
    BoardingActive,
    /// <summary>The poi's station has a persisted interior simulation; it cannot be removed safely.</summary>
    InteriorPersisted,
    /// <summary>The poi's installation is held enterable by an <c>IDungeonInstallation.KeepEnterable</c> hold.</summary>
    HeldEnterable,
    /// <summary>The poi is not currently present natively (awaiting reconstruction, or already gone); there is nothing to remove.</summary>
    NotPresent,
    /// <summary>The pocket still contains owned combat sites, which cannot be removed with it.</summary>
    CombatSitesPresent,
    /// <summary>The pocket is still the endpoint of an owned wormhole pair; remove the pair first.</summary>
    WormholeEndpoint,
    /// <summary>The owning session ended or was replaced; re-obtain the poi for the live game.</summary>
    SessionEnded,
    /// <summary>A transient lifecycle state (not yet in a safely actionable state) blocked the query/removal.</summary>
    NotReady,
    /// <summary>The world/service layer is unavailable (no authoring capability or the plugin is gone).</summary>
    Unavailable
}

/// <summary>Retained result of an action on an owned poi. Terminal outcomes stay stable until the next action.</summary>
public sealed class WorldContentResult
{
    public WorldContentStatus Status { get; }
    public string Detail { get; }
    public bool Succeeded => Status == WorldContentStatus.Succeeded;
    internal WorldContentResult(WorldContentStatus status, string detail = "")
    { Status = status; Detail = detail ?? ""; }
}

/// <summary>
/// One owned authored-pocket-system poi for a single captured game. The API owns every native
/// identity; the consumer names the poi with an author-local key. Re-creating or re-obtaining the
/// same key returns the SAME object poi for the life of the owning session (keyed reconciliation
/// surfaces as object identity, never a duplicate). An poi from a session that has ended or been
/// replaced refuses its actions with <see cref="WorldContentStatus.GameEnded"/> rather than touching the
/// replacement save — re-obtain the objects for the live game explicitly.
/// </summary>
public interface IPocketSystem
{
    /// <summary>The author-local poi key this poi is owned under.</summary>
    string PoiKey { get; }
    /// <summary>The declaration this poi belongs to (live revision after migration).</summary>
    PocketSystemDefinition Definition { get; }
    /// <summary>Current typed per-poi reconciliation state (Reconstructed / Pending / Failed(reason)).</summary>
    PocketSystemState State { get; }
    /// <summary>Owned pocket-system identity; populated only while <see cref="State"/> is <see cref="ReconstructionStatus.Reconstructed"/>.</summary>
    string? SystemId { get; }
    /// <summary>Owned entrance-gate identity; populated only while reconstructed.</summary>
    string? EntranceGatePoiId { get; }
    /// <summary>Owned pocket-side gate identity; populated only while reconstructed.</summary>
    string? PocketGatePoiId { get; }
    /// <summary>The retained result of the most recent action on this poi.</summary>
    WorldContentResult LastAction { get; }
    /// <summary>
    /// Fired when this poi's reconciliation state changes within its own session (for example
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
    /// Removes the owned pocket: removes the pocket system, both paired gates and this API's
    /// authored sites inside it from the live map and from save data. This is the plain native
    /// removal: it refuses only when removal would be impossible or corrupt save state (the pocket
    /// still contains combat sites, it is still the endpoint of an owned wormhole pair, it is not
    /// present natively, or the world is not in an actionable state). It does not check transient
    /// player-safety conditions. To avoid acting while the player is at or inside the pocket, query
    /// <see cref="CanRemove"/> first, or use <see cref="RequestRemoval"/> to defer to the next safe
    /// cleanup window. On success this object is terminal
    /// (<see cref="ReconstructionStatus.Removed"/>); creating the same poi key again authors
    /// a fresh pocket with fresh native identity.
    /// </summary>
    WorldContentResult Remove();
    /// <summary>
    /// Pure readiness report, no mutation: why (if at all) the pocket can currently be removed
    /// (player at/inside, still contains combat sites, still a wormhole endpoint, not present,
    /// session ended, or not yet actionable). <see cref="RemovalStatus.Ready"/> means a
    /// cleanup window may remove it now.
    /// </summary>
    RemovalStatus CanRemove();
    /// <summary>
    /// Requests deferred removal, mirroring the game's ambient cleanup window: the pocket is marked
    /// for removal and removed at the next safe maintenance pass once <see cref="CanRemove"/> is
    /// <see cref="RemovalStatus.Ready"/> (offsetting occupancy and gate conditions).
    /// Returns a retained result; completion is signalled by <see cref="Changed"/> with the object
    /// becoming terminal (<see cref="ReconstructionStatus.Removed"/>). Refused when the pocket is
    /// already gone or the world is not actionable.
    /// </summary>
    WorldContentResult RequestRemoval();
}

/// <summary>
/// A single reconciled poi that did not settle in a reconstructed state.
/// Carries the owned poi object; the internal coordinator also builds reference-backed pois.
/// </summary>
public sealed class ReconstructionFailure
{
    /// <summary>Internal coordinator keying; null on consumer-built failures.</summary>
    public PocketSystemReference? Reference { get; }
    /// <summary>The owned poi that failed; null on internal coordinator-built failures.</summary>
    public IPocketSystem? Poi { get; }
    public ReconstructionFailureReason Reason { get; }
    /// <summary>Internal coordinator construction.</summary>
    public ReconstructionFailure(PocketSystemReference reference, ReconstructionFailureReason reason)
    {
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        Reason = reason;
    }
    /// <summary>Consumer construction over an owned poi.</summary>
    public ReconstructionFailure(IPocketSystem poi, ReconstructionFailureReason reason)
    {
        Poi = poi ?? throw new ArgumentNullException(nameof(poi));
        Reason = reason;
    }
}

/// <summary>
/// Reports actual reconciliation outcomes once per session at the post-reconstruction safe boundary,
/// carrying the owned poi objects. An empty failure list means every declared poi
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
