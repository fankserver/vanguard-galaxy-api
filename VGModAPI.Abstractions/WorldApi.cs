using System;
using System.Collections.Generic;

namespace VGModAPI;

public enum WorldStatus { Succeeded, Unavailable, NotReady, UnknownProvider, DuplicateDefinition, InvalidDefinition, NotRegistered, Rejected }

/// <summary>Immutable persistent Combat-site declaration. Register before starting a session.</summary>
public sealed class WorldCombatSiteDefinition
{
    public string LocalId { get; }
    public int Revision { get; }
    public string Name { get; }
    public string FactionId { get; }
    public int Level { get; }
    public WorldCombatSiteDefinition(string localId, int revision, string name, string factionId, int level)
    {
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        FactionId = factionId ?? throw new ArgumentNullException(nameof(factionId));
        Revision = revision; Level = level;
    }
}

/// <summary>Internal coordinator keying shape; consumers address a site through its <see cref="ICombatSite"/> object.</summary>
public sealed class WorldSiteReference
{
    public string ProviderId { get; }
    public string LocalId { get; }
    public Guid InstanceId { get; }
    public WorldSiteReference(string providerId, string localId, Guid instanceId)
    {
        ProviderId = providerId ?? throw new ArgumentNullException(nameof(providerId));
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId)); InstanceId = instanceId;
    }
}

public sealed class WorldSiteResult
{
    public WorldStatus Status { get; }
    public WorldSiteReference? Reference { get; }
    /// <summary>POI identity accepted by story travel objectives; not an authorization token.</summary>
    public string? PoiId { get; }
    public bool Succeeded => Status == WorldStatus.Succeeded;
    public WorldSiteResult(WorldStatus status, WorldSiteReference? reference = null, string? poiId = null) { Status = status; Reference = reference; PoiId = poiId; }
}

public interface IWorldService : IServiceStatus
{
    /// <summary>Main-thread-only; invoke directly from the loaded plugin's assembly. Null means unavailable or unauthenticated.</summary>
    IWorldProvider? AcquireProvider(object pluginInstance);
    /// <summary>Declarative quiet-location presentation for authored content.</summary>
    IAmbientTrafficService AmbientTraffic { get; }
    /// <summary>Runtime-only survivability for story-critical unit instances.</summary>
    IUnitProtectionService UnitProtection { get; }
    /// <summary>Instance-scoped drone-bay tuning for authored encounters.</summary>
    IDroneBayService DroneBays { get; }
}

public interface IWorldProvider : IDisposable
{
    string ProviderId { get; }
    /// <summary>Optional exact previous declaration permits a revision/name migration; faction, level and local identity must remain unchanged.</summary>
    WorldStatus Register(WorldCombatSiteDefinition definition, WorldCombatSiteDefinition? previous = null);
    /// <summary>
    /// Creates (or reconciles) an owned persistent combat site in an existing system for the current
    /// game, keyed by an author-local occurrence key. The API allocates and owns the native identity;
    /// consumers never supply instance GUIDs or session tokens. Re-declaring the same key returns the
    /// SAME object instance for the life of the session. Returns null while the world cannot author.
    /// Supported state is saved automatically; no provider save hooks are required.
    /// </summary>
    ICombatSite? CreateCombatSite(string localId, string occurrenceKey, string systemId, float x, float y);
    /// <summary>Re-obtains the owned occurrence for a key in the current game, or null if it does not exist.</summary>
    ICombatSite? GetCombatSite(string localId, string occurrenceKey);

    /// <summary>Declares an enclosed authored pocket system. Optional exact previous declaration permits a revision/name migration.</summary>
    WorldStatus RegisterAuthoredSystem(AuthoredSystemDefinition definition, AuthoredSystemDefinition? previous = null);
    /// <summary>
    /// Creates (or reconciles) an owned pocket system for the current game, keyed by an author-local
    /// occurrence key. Returns the owned occurrence object; re-declaring the same key returns the SAME
    /// object instance for the life of the session. Returns null while the world cannot author (no
    /// current gameplay-initialized session, definition not registered, or not authorable).
    /// </summary>
    IAuthoredSystem? CreateAuthoredSystem(string localId, string occurrenceKey, string anchorSystemId);
    /// <summary>All current-game occurrences the provider owns for a registered local definition (including restored rows, no replay).</summary>
    IReadOnlyList<IAuthoredSystem> GetAuthoredSystems(string localId);
    /// <summary>Re-obtains the owned occurrence for a key in the current game, or null if it does not exist yet.</summary>
    IAuthoredSystem? GetAuthoredSystem(string localId, string occurrenceKey);
    /// <summary>
    /// Reports actual reconciliation outcomes once per session at the post-reconstruction safe boundary,
    /// carrying the owned occurrence objects. An empty failure list means every declared occurrence reconstructed.
    /// </summary>
    event Action<AuthoredSystemsSettledEvent>? AuthoredSystemReconstructionSettled;
}
