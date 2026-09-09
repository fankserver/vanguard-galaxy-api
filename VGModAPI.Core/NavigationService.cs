using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class NavigationMap
{
    internal readonly IReadOnlyDictionary<string, string[]> Edges;
    internal readonly NavigationStation[] Stations;
    internal readonly Func<bool> IsCurrent;
    internal NavigationMap(IReadOnlyDictionary<string, string[]> edges, NavigationStation[] stations, Func<bool> isCurrent)
    { Edges = edges; Stations = stations; IsCurrent = isCurrent; }
}
internal sealed class NavigationService
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly Func<Guid, NavigationMap?> _read;
    private readonly Func<Guid, string, Func<bool>, NavigationStatus> _focus;
    private readonly Func<string, string, bool?> _world;
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public Guid? SessionId { get { _hub.CheckThread(); return _hub.CurrentSession?.Id; } }
    internal NavigationService(LifecycleHub hub, Func<Guid, NavigationMap?> read, Func<Guid, string, Func<bool>, NavigationStatus> focus, Func<string, string, bool?> world)
    { _hub = hub; _status = hub.Services.Get("navigation"); _read = read; _focus = focus; _world = world; }
    internal INavigation ForGame(Guid session) => new Navigation(this, session);
    private sealed class Navigation : INavigation
    {
        private readonly NavigationService _owner;
        private readonly Guid _session;
        internal Navigation(NavigationService owner, Guid session) { _owner = owner; _session = session; }
        public NavigationStationsResult GetStations(bool visitedOnly = true) => _owner.GetStations(_session, visitedOnly);
        public JumpCountsResult GetJumpCounts(string fromSystemId) => _owner.GetJumpCounts(_session, fromSystemId);
        public JumpCountResult GetJumpCount(string fromSystemId, string toSystemId) => _owner.GetJumpCount(_session, fromSystemId, toSystemId);
        public NavigationStatus FocusPoi(string poiId) => _owner.FocusPoi(_session, poiId);
        public NavigationStatus FocusWorldSite(WorldSiteReference reference) => _owner.FocusWorldSite(_session, reference);
    }
    private bool Ready(Guid session) => session != Guid.Empty && SessionId == session &&
        (_hub.CurrentSession!.Phase == SessionPhase.PlayerReady || _hub.CurrentSession.Phase == SessionPhase.GameplayInitialized);
    public NavigationStationsResult GetStations(Guid expectedSessionId, bool visitedOnly = true)
    {
        _hub.CheckThread();
        NavigationStationsResult Empty(NavigationStatus status) => new(status, Array.Empty<NavigationStation>());
        if (!Availability.IsAvailable) return Empty(NavigationStatus.Unavailable);
        if (!Ready(expectedSessionId)) return Empty(NavigationStatus.NotReady);
        try
        {
            var map = _read(expectedSessionId);
            if (map == null || !map.IsCurrent() || !Ready(expectedSessionId)) return Empty(NavigationStatus.NotReady);
            return new NavigationStationsResult(NavigationStatus.Succeeded, map.Stations.Where(x => !visitedOnly || x.Visited).ToArray());
        }
        catch (Exception error) { _hub.ReportSubscriberFailure("navigation", error); return Empty(NavigationStatus.Unavailable); }
    }
    public JumpCountsResult GetJumpCounts(Guid expectedSessionId, string fromSystemId)
    {
        _hub.CheckThread();
        JumpCountsResult Empty(NavigationStatus status) => new(status, new Dictionary<string, int>());
        if (!Availability.IsAvailable) return Empty(NavigationStatus.Unavailable);
        if (!Ready(expectedSessionId)) return Empty(NavigationStatus.NotReady);
        try
        {
            var map = _read(expectedSessionId);
            if (map == null) return Empty(NavigationStatus.NotReady);
            if (string.IsNullOrWhiteSpace(fromSystemId) || !map.Edges.ContainsKey(fromSystemId)) return Empty(NavigationStatus.Missing);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal) { [fromSystemId] = 0 };
            var queue = new Queue<string>(); queue.Enqueue(fromSystemId);
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                foreach (var next in map.Edges[node])
                    if (map.Edges.ContainsKey(next) && !counts.ContainsKey(next)) { counts.Add(next, counts[node] + 1); queue.Enqueue(next); }
            }
            return map.IsCurrent() && Ready(expectedSessionId) ? new JumpCountsResult(NavigationStatus.Succeeded, counts) : Empty(NavigationStatus.NotReady);
        }
        catch (Exception error) { _hub.ReportSubscriberFailure("navigation", error); return Empty(NavigationStatus.Unavailable); }
    }
    public JumpCountResult GetJumpCount(Guid expectedSessionId, string fromSystemId, string toSystemId)
    {
        _hub.CheckThread();
        if (!Availability.IsAvailable) return new(NavigationStatus.Unavailable);
        if (!Ready(expectedSessionId)) return new(NavigationStatus.NotReady);
        if (string.IsNullOrWhiteSpace(fromSystemId) || string.IsNullOrWhiteSpace(toSystemId)) return new(NavigationStatus.Missing);
        try
        {
            var map = _read(expectedSessionId);
            if (map == null) return new(NavigationStatus.NotReady);
            JumpCountResult result;
            if (!map.Edges.ContainsKey(fromSystemId) || !map.Edges.ContainsKey(toSystemId)) result = new(NavigationStatus.Missing);
            else
            {
                var visited = new HashSet<string>(StringComparer.Ordinal) { fromSystemId };
                var queue = new Queue<(string Id, int Distance)>(); queue.Enqueue((fromSystemId, 0));
                result = new(NavigationStatus.Disconnected);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (current.Id == toSystemId) { result = new(NavigationStatus.Succeeded, current.Distance); break; }
                    foreach (var next in map.Edges[current.Id])
                        if (map.Edges.ContainsKey(next) && visited.Add(next)) queue.Enqueue((next, current.Distance + 1));
                }
            }
            return map.IsCurrent() && Ready(expectedSessionId) ? result : new(NavigationStatus.NotReady);
        }
        catch (Exception error) { _hub.ReportSubscriberFailure("navigation", error); return new(NavigationStatus.Unavailable); }
    }
    public NavigationStatus FocusPoi(Guid expectedSessionId, string poiId)
    {
        _hub.CheckThread();
        if (!Availability.IsAvailable) return NavigationStatus.Unavailable;
        if (!Ready(expectedSessionId) || _hub.IsDispatchingCallbacks) return NavigationStatus.NotReady;
        if (string.IsNullOrWhiteSpace(poiId) || WorldObjectIdentity.IsReserved(poiId)) return NavigationStatus.Rejected;
        return Focus(expectedSessionId, poiId, () => Ready(expectedSessionId));
    }
    public NavigationStatus FocusWorldSite(Guid expectedSessionId, WorldSiteReference reference)
    {
        _hub.CheckThread();
        if (!Availability.IsAvailable) return NavigationStatus.Unavailable;
        if (!Ready(expectedSessionId) || _hub.IsDispatchingCallbacks) return NavigationStatus.NotReady;
        if (reference == null) return NavigationStatus.Rejected;
        try
        {
            var id = new WorldObjectIdentity(new ContentDeclaration(reference.ProviderId, reference.LocalId, PersistentContentKind.WorldObject, ContentPersistenceImpact.ProviderRequired), reference.InstanceId);
            if (_world(id.Owner, id.NativeId) != true || !Ready(expectedSessionId)) return NavigationStatus.Missing;
            return Focus(expectedSessionId, id.NativeId, () => Ready(expectedSessionId) && _world(id.Owner, id.NativeId) == true);
        }
        catch (Exception error) { _hub.ReportSubscriberFailure("navigation", error); return NavigationStatus.Rejected; }
    }
    private NavigationStatus Focus(Guid session, string id, Func<bool> current)
    { try { return _focus(session, id, current); } catch (Exception error) { _hub.ReportSubscriberFailure("navigation", error); return NavigationStatus.Unavailable; } }
}
