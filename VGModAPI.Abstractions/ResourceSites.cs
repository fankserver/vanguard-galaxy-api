using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

public enum ResourceSiteKind { SalvageSite, MiningField }

/// <summary>The one supported authored hazard: a constant-damage radiation cloud around the wreck.</summary>
public enum ResourceSiteHazard { DamageInRadius }

/// <summary>
/// Immutable persistent authored-site declaration. Register before starting a session. A salvage
/// site contains a defeated wreck of an exact ship class, optionally a guaranteed derelict boardable
/// station, an optional hazard cloud and an optional scattered asteroid field. A mining field
/// contains an exact asteroid count drawn from the host system's ore data.
/// </summary>
public sealed class ResourceSiteDefinition
{
    public string LocalId { get; }
    public int Revision { get; }
    public string Name { get; }
    public ResourceSiteKind Kind { get; }
    public int Level { get; }
    /// <summary>Existing faction identity for salvage-site presentation and station crewing.</summary>
    public string? FactionId { get; }
    /// <summary>Exact wreck ship class identity; an unknown class refuses creation rather than substituting a template.</summary>
    public string? WreckShipId { get; }
    /// <summary>Guarantees a boardable derelict station at the site (no probability roll).</summary>
    public bool WithStation { get; }
    public ResourceSiteHazard? Hazard { get; }
    /// <summary>Scatters a decorative asteroid field around the wreck, keeping the hub clear.</summary>
    public bool ScatterAsteroids { get; }
    /// <summary>Exact asteroid count for a mining field; the count is gameplay-relevant, not decorative.</summary>
    public int AsteroidCount { get; }

    private ResourceSiteDefinition(string localId, int revision, string name, ResourceSiteKind kind, int level,
        string? factionId, string? wreckShipId, bool withStation, ResourceSiteHazard? hazard, bool scatterAsteroids, int asteroidCount)
    {
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (level < 1 || level > 100000) throw new ArgumentOutOfRangeException(nameof(level));
        if (hazard.HasValue && !Enum.IsDefined(typeof(ResourceSiteHazard), hazard.Value)) throw new ArgumentOutOfRangeException(nameof(hazard));
        Revision = revision; Kind = kind; Level = level; FactionId = factionId; WreckShipId = wreckShipId;
        WithStation = withStation; Hazard = hazard; ScatterAsteroids = scatterAsteroids; AsteroidCount = asteroidCount;
    }

    public static ResourceSiteDefinition Salvage(string localId, int revision, string name, int level, string wreckShipId,
        string factionId, bool withStation = false, ResourceSiteHazard? hazard = null, bool scatterAsteroids = false)
    {
        if (string.IsNullOrWhiteSpace(wreckShipId)) throw new ArgumentException("An exact wreck ship class is required.", nameof(wreckShipId));
        if (string.IsNullOrWhiteSpace(factionId)) throw new ArgumentException("An existing faction identity is required.", nameof(factionId));
        if (withStation && level < 5) throw new ArgumentOutOfRangeException(nameof(level), "Native derelict stations require site level 5 or higher.");
        return new ResourceSiteDefinition(localId, revision, name, ResourceSiteKind.SalvageSite, level,
            factionId, wreckShipId, withStation, hazard, scatterAsteroids, 0);
    }

    public static ResourceSiteDefinition MiningField(string localId, int revision, string name, int level, int asteroidCount)
    {
        if (asteroidCount < 1 || asteroidCount > 64) throw new ArgumentOutOfRangeException(nameof(asteroidCount));
        return new ResourceSiteDefinition(localId, revision, name, ResourceSiteKind.MiningField, level,
            null, null, withStation: false, null, scatterAsteroids: false, asteroidCount);
    }
}

/// <summary>Typed per-occurrence authored-site state, never a lifecycle marker or an admission token.</summary>
public sealed class ResourceSiteState
{
    public ReconstructionStatus Status { get; }
    public ReconstructionFailureReason? Reason { get; }
    /// <summary>Native POI identity accepted by travel targets; populated only while reconstructed.</summary>
    public string? PoiId { get; }
    public bool Reconstructed => Status == ReconstructionStatus.Reconstructed;
    public ResourceSiteState(ReconstructionStatus status, ReconstructionFailureReason? reason = null, string? poiId = null)
    { Status = status; Reason = reason; PoiId = poiId; }
}

/// <summary>
/// One owned authored-site occurrence for a single captured game, following the uniform occurrence
/// contract: author-local key, API-allocated native identity, same-key=same object occurrence per
/// session, keyed reconciliation instead of duplicates, and stale-session freeze.
/// </summary>
public interface IResourceSite
{
    string OccurrenceKey { get; }
    ResourceSiteDefinition Definition { get; }
    ResourceSiteState State { get; }
    string? PoiId { get; }
    WorldContentResult LastAction { get; }
    event Action<IResourceSite>? Changed;
    /// <summary>
    /// Dissolves the owned site: removes its native POI from the host system (a directly authored
    /// salvage site or mining field, including a pocket system) and drops its save row so it is
    /// recorded as intentionally absent rather than reconstructed as a failure. Refused while the
    /// player is at or routed to the site, while a live boarding operation or a persisted interior
    /// simulation holds a salvage site's derelict station, and while the site's installation is held
    /// enterable by this API. On success this object is terminal
    /// (<see cref="ReconstructionStatus.Dissolved"/>); creating the same occurrence key again authors
    /// a fresh site with fresh native identity.
    /// </summary>
    WorldContentResult Dissolve();
}

public sealed class ResourceSiteFailure
{
    public IResourceSite Occurrence { get; }
    public ReconstructionFailureReason Reason { get; }
    public ResourceSiteFailure(IResourceSite occurrence, ReconstructionFailureReason reason)
    { Occurrence = occurrence ?? throw new ArgumentNullException(nameof(occurrence)); Reason = reason; }
}

/// <summary>Once-per-session aggregate reconciliation report for authored sites at the post-reconstruction safe boundary.</summary>
public sealed class ResourceSitesSettledEvent
{
    public Guid SessionId { get; }
    public IReadOnlyList<IResourceSite> Reconstructed { get; }
    public IReadOnlyList<ResourceSiteFailure> Failures { get; }
    public bool HasFailures => Failures.Count > 0;
    public ResourceSitesSettledEvent(Guid sessionId, IEnumerable<IResourceSite> reconstructed, IEnumerable<ResourceSiteFailure> failures)
    {
        SessionId = sessionId;
        Reconstructed = new ReadOnlyCollection<IResourceSite>(new List<IResourceSite>(reconstructed ?? throw new ArgumentNullException(nameof(reconstructed))));
        Failures = new ReadOnlyCollection<ResourceSiteFailure>(new List<ResourceSiteFailure>(failures ?? throw new ArgumentNullException(nameof(failures))));
    }
}
