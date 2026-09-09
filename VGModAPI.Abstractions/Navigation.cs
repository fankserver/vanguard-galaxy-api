using System;
using System.Collections.Generic;

namespace VGModAPI;

public enum NavigationStatus { Succeeded, Unavailable, NotReady, Missing, Disconnected, Rejected }

/// <summary>Read-only station metadata. Null names have not been generated; inspection never generates names or stock.</summary>
public sealed class NavigationStation
{
    public string Id { get; }
    public string SystemId { get; }
    public string? Name { get; }
    public bool Visited { get; }
    public NavigationStation(string id, string systemId, string? name, bool visited)
    { Id = id; SystemId = systemId; Name = name; Visited = visited; }
}
public sealed class NavigationStationsResult
{
    public NavigationStatus Status { get; }
    public IReadOnlyList<NavigationStation> Stations { get; }
    public NavigationStationsResult(NavigationStatus status, IReadOnlyList<NavigationStation> stations)
    { Status = status; var copy = new NavigationStation[stations.Count]; for (int i = 0; i < copy.Length; i++) copy[i] = stations[i]; Stations = Array.AsReadOnly(copy); }
}
public sealed class JumpCountResult
{
    public NavigationStatus Status { get; }
    /// <summary>Unweighted directed jump-gate hops, ignoring passes, hostility and travel eligibility. Null unless succeeded.</summary>
    public int? Hops { get; }
    public JumpCountResult(NavigationStatus status, int? hops = null) { Status = status; Hops = hops; }
}

public sealed class JumpCountsResult
{
    public NavigationStatus Status { get; }
    public IReadOnlyDictionary<string, int> Hops { get; }
    public JumpCountsResult(NavigationStatus status, IDictionary<string, int> hops)
    { Status = status; Hops = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(new Dictionary<string, int>(hops, StringComparer.Ordinal)); }
}

public interface INavigationService : IServiceStatus
{
    Guid? SessionId { get; }
    NavigationStationsResult GetStations(Guid expectedSessionId, bool visitedOnly = true);
    /// <summary>One graph read for a distance list; only reachable system IDs are included.</summary>
    JumpCountsResult GetJumpCounts(Guid expectedSessionId, string fromSystemId);
    JumpCountResult GetJumpCount(Guid expectedSessionId, string fromSystemId, string toSystemId);
    /// <summary>Requests the native map focus operation; success means scheduled, not completed travel or a traversable route.</summary>
    NavigationStatus FocusPoi(Guid expectedSessionId, string poiId);
    /// <summary>Resolves restored API-owned content by provider/local/instance identity before scheduling focus.</summary>
    NavigationStatus FocusWorldSite(Guid expectedSessionId, WorldSiteReference reference);
}
