using System;
using System.Collections.Generic;

namespace VGModAPI;

public enum WorldStatus { Succeeded, Unavailable, NotReady, UnknownProvider, DuplicateDefinition, InvalidDefinition, NotRegistered, Rejected }

/// <summary>Immutable persistent Combat-site declaration. Register before starting a session.</summary>
public sealed class CombatSiteDefinition
{
    public string LocalId { get; }
    public int Revision { get; }
    public string Name { get; }
    public string FactionId { get; }
    public int Level { get; }
    public CombatSiteDefinition(string localId, int revision, string name, string factionId, int level)
    {
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        FactionId = factionId ?? throw new ArgumentNullException(nameof(factionId));
        Revision = revision; Level = level;
    }
}

/// <summary>Internal coordinator keying shape; consumers address a site through its <see cref="ICombatSite"/> object.</summary>
public sealed class CombatSiteReference
{
    public string ProviderId { get; }
    public string LocalId { get; }
    public Guid InstanceId { get; }
    public CombatSiteReference(string providerId, string localId, Guid instanceId)
    {
        ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId)); InstanceId = instanceId;
    }
}

public sealed class CombatSiteResult
{
    public WorldStatus Status { get; }
    public CombatSiteReference? Reference { get; }
    /// <summary>POI identity accepted by story travel objectives; not an authorization token.</summary>
    public string? PoiId { get; }
    public bool Succeeded => Status == WorldStatus.Succeeded;
    public CombatSiteResult(WorldStatus status, CombatSiteReference? reference = null, string? poiId = null) { Status = status; Reference = reference; PoiId = poiId; }
}

public interface IWorldService : IServiceStatus
{
    /// <summary>Main-thread-only; invoke directly from the loaded plugin's assembly. Null means unavailable or unauthenticated.</summary>
    IWorldProvider? AcquireProvider(object pluginInstance);
    /// <summary>Declarative quiet-location presentation for owned content.</summary>
    IAmbientTrafficService AmbientTraffic { get; }
    /// <summary>Runtime-only survivability for story-critical unit instances.</summary>
    IUnitProtectionService UnitProtection { get; }
    /// <summary>Instance-scoped drone-bay tuning for owned encounters.</summary>
    IDroneBayService DroneBays { get; }
}

public interface IWorldProvider : IDisposable
{
    string ProviderId { get; }
    /// <summary>Optional exact previous declaration permits a revision/name migration; faction, level and local identity must remain unchanged.</summary>
    WorldStatus RegisterCombatSite(CombatSiteDefinition definition, CombatSiteDefinition? previous = null);
    /// <summary>
    /// Creates (or reconciles) an owned persistent combat site in an existing system for the current
    /// game, keyed by an author-local occurrence key. The API allocates and owns the native identity;
    /// consumers never supply instance GUIDs or session tokens. Re-declaring the same key returns the
    /// SAME object instance for the life of the session. Returns null while the world cannot author.
    /// Supported state is saved automatically; no provider save hooks are required.
    /// </summary>
    ICombatSite? CreateCombatSite(string localId, string occurrenceKey, string systemId, float x, float y);
    /// <summary>Re-obtains the owned occurrence for a key in the current game, or null if it does not exist.</summary>
    /// <summary>Re-obtains the owned occurrence for a key in the current game, or null if it does not exist.</summary>
    ICombatSite? GetCombatSite(string localId, string occurrenceKey);
    /// <summary>All current-game occurrences the provider owns for a registered combat-site definition (including restored rows, no replay).</summary>
    IReadOnlyList<ICombatSite> GetCombatSites(string localId);
    /// <summary>Once-per-session aggregate reconciliation report for this provider's keyed combat sites.</summary>
    event Action<CombatSitesSettledEvent>? CombatSiteReconstructionSettled;

    /// <summary>Declares an enclosed owned pocket system. Optional exact previous declaration permits a revision/name migration.</summary>
    WorldStatus RegisterPocketSystem(PocketSystemDefinition definition, PocketSystemDefinition? previous = null);
    /// <summary>
    /// Creates (or reconciles) an owned pocket system for the current game, keyed by an author-local
    /// occurrence key. Returns the owned occurrence object; re-declaring the same key returns the SAME
    /// object instance for the life of the session. Returns null while the world cannot author (no
    /// current gameplay-initialized session, definition not registered, or not authorable).
    /// </summary>
    IPocketSystem? CreatePocketSystem(string localId, string occurrenceKey, string anchorSystemId);
    /// <summary>All current-game occurrences the provider owns for a registered local definition (including restored rows, no replay).</summary>
    IReadOnlyList<IPocketSystem> GetPocketSystems(string localId);
    /// <summary>Re-obtains the owned occurrence for a key in the current game, or null if it does not exist yet.</summary>
    IPocketSystem? GetPocketSystem(string localId, string occurrenceKey);
    /// <summary>
    /// Reports actual reconciliation outcomes once per session at the post-reconstruction safe boundary,
    /// carrying the owned occurrence objects. An empty failure list means every declared occurrence reconstructed.
    /// </summary>
    event Action<PocketSystemsSettledEvent>? PocketSystemReconstructionSettled;

    /// <summary>Declares an owned pair of exactly connected native wormholes.</summary>
    WorldStatus RegisterWormholePair(WormholePairDefinition definition, WormholePairDefinition? previous = null);
    /// <summary>Creates or reconciles one owned pair between two existing systems for the current game.</summary>
    IWormholePair? CreateWormholePair(string localId, string occurrenceKey, string firstSystemId, string secondSystemId);
    /// <summary>Re-obtains the owned pair for a key in the current game, or null if it does not exist.</summary>
    IWormholePair? GetWormholePair(string localId, string occurrenceKey);
    /// <summary>All current-game occurrences owned for a registered wormhole-pair definition.</summary>
    IReadOnlyList<IWormholePair> GetWormholePairs(string localId);
    /// <summary>Once-per-session reconstruction outcomes for this provider's owned pairs.</summary>
    event Action<WormholePairsSettledEvent>? WormholePairReconstructionSettled;

    /// <summary>Declares an owned site (salvage site or exact-count mining field). Optional exact previous declaration permits a revision migration.</summary>
    WorldStatus RegisterResourceSite(ResourceSiteDefinition definition, ResourceSiteDefinition? previous = null);
    /// <summary>
    /// Creates (or reconciles) an owned site in an existing system — including an owned pocket
    /// system — keyed by an author-local occurrence key. The API allocates and owns the native identity.
    /// Re-declaring the same key returns the SAME object instance for the life of the session. Returns
    /// null while the world cannot author.
    /// </summary>
    IResourceSite? CreateResourceSite(string localId, string occurrenceKey, string systemId, float x, float y);
    /// <summary>Re-obtains the owned occurrence for a key in the current game, or null if it does not exist yet.</summary>
    IResourceSite? GetResourceSite(string localId, string occurrenceKey);
    /// <summary>All current-game occurrences the provider owns for a registered site definition (including restored rows, no replay).</summary>
    IReadOnlyList<IResourceSite> GetResourceSites(string localId);
    /// <summary>Once-per-session aggregate reconciliation report for this provider's owned sites.</summary>
    event Action<ResourceSitesSettledEvent>? ResourceSiteReconstructionSettled;

    /// <summary>Declares a moored owned ship. Optional exact previous declaration permits a revision migration.</summary>
    WorldStatus RegisterMooredShip(MooredShipDefinition definition, MooredShipDefinition? previous = null);
    /// <summary>
    /// Creates (or reconciles) the one owned moored ship beside a station POI, keyed by an author-local
    /// occurrence key. The API owns its persistent unit identity, converges to exactly one instance,
    /// maintains the mooring (no docking, no auto-AI, never boardable) and reconstructs after load.
    /// Returns null while the world cannot author.
    /// </summary>
    IMooredShip? CreateMooredShip(string localId, string occurrenceKey, string stationPoiId);
    /// <summary>Re-obtains the owned occurrence for a key in the current game, or null if it does not exist yet.</summary>
    IMooredShip? GetMooredShip(string localId, string occurrenceKey);
    /// <summary>All current-game occurrences the provider owns for a registered moored-ship definition.</summary>
    IReadOnlyList<IMooredShip> GetMooredShips(string localId);
    /// <summary>Once-per-session aggregate reconciliation report for this provider's owned ships.</summary>
    event Action<MooredShipsSettledEvent>? MooredShipReconstructionSettled;

    /// <summary>
    /// Schedules a deterministic owned encounter at an existing POI in the current game through the
    /// native timed-reinforcement trigger: exact counts per wave, never a point-budget request. All
    /// inputs are validated up front (POI, every ship class, faction, rank) before anything is
    /// scheduled. Spawned units are transient session content; nothing is persisted or replayed.
    /// </summary>
    EncounterSpawnResult SpawnEncounter(string poiId, EncounterComposition composition);
}
