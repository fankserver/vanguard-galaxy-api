using System;

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

/// <summary>Stable owner-local-instance reference, never a native Unity object or session permission.</summary>
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
}

public interface IWorldProvider : IDisposable
{
    string ProviderId { get; }
    /// <summary>Optional exact previous declaration permits a revision/name migration; faction, level and local identity must remain unchanged.</summary>
    WorldStatus Register(WorldCombatSiteDefinition definition, WorldCombatSiteDefinition? previous = null);
    /// <summary>Resolves this provider's reference against the current observed session and native membership.</summary>
    WorldSiteResult FindPersistentCombatSite(Guid expectedSessionId, WorldSiteReference reference);
    /// <summary>Creates a persistent site in an existing system. Supported state is saved automatically; no provider save hooks are required.</summary>
    WorldSiteResult CreatePersistentCombatSite(Guid expectedSessionId, string localId, Guid instanceId, string systemId, float x, float y);
}
