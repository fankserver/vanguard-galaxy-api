using System;
using System.Collections;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Core.Integration;

/// <summary>Reflection-backed exact native wormhole-pair ownership; never joins the global untargeted mesh.</summary>
internal sealed class WorldNativeWormholes : IWormholePairNative
{
    private readonly GameAdapter _game;
    private readonly WorldMapIndex _index;
    private readonly Type _wormholeType;
    private readonly PropertyInfo _map, _guid, _name;
    private readonly FieldInfo _points, _parent, _hidden, _discovered, _targets;
    private readonly FieldInfo _playerCurrentPoi, _playerWaypoints;
    private readonly MethodInfo _setup, _position;
    private readonly Action<Exception> _report;
    private bool _inPass; private Guid _passSession; private WorldMapIndex.Snapshot? _snapshot;

    internal WorldNativeWormholes(GameAdapter game, Assembly assembly, Action<Exception>? report = null)
    {
        _game = game; _report = report ?? (_ => { }); _index = new WorldMapIndex(assembly);
        _wormholeType = assembly.GetType(WormholePairBindings.Wormhole, true)!;
        var player = assembly.GetType("Source.Player.GamePlayer", true)!;
        _map = player.GetProperty("map", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("GamePlayer.map");
        var system = assembly.GetType(WormholePairBindings.System, true)!;
        var element = assembly.GetType(WormholePairBindings.Element, true)!;
        var poi = assembly.GetType(WormholePairBindings.Poi, true)!;
        _points = Field(system, "pointsOfInterest"); _parent = Field(element, "system");
        _name = element.GetProperty("name", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("MapElement.name");
        _hidden = Field(poi, "hidden"); _discovered = Field(_wormholeType, "discovered"); _targets = Field(_wormholeType, "targetWormholeGuids");
        _guid = element.GetProperty("guid", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("MapElement.guid");
        _playerCurrentPoi = Field(player, "currentPointOfInterest"); _playerWaypoints = Field(player, "waypoints");
        var methods = WormholePairBindings.Validate(assembly); _setup = methods["wormholeSetup"]; _position = methods["wormholePosition"];
    }
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.FullName, name);
    private object? Map(bool observed, Guid session)
    {
        object? player;
        bool ok = observed ? _game.TryGetObservedPlayer(session, out player) : _game.TryGetCurrentReadyPlayer(session, out player);
        return ok && player != null ? _map.GetValue(player) : null;
    }
    private WorldMapIndex.Snapshot? Snapshot(bool observed, Guid session)
    {
        bool cache = observed && _inPass && session == _passSession;
        if (cache && _snapshot != null) return _snapshot;
        var map = Map(observed, session); if (map == null) return null;
        var snapshot = _index.Read(map); if (cache) _snapshot = snapshot; return snapshot;
    }
    private string Id(object value) => (string)_guid.GetValue(value)!;
    private IList Points(object system) => (IList)_points.GetValue(system)!;
    private IList Targets(object wormhole) => (IList)_targets.GetValue(wormhole)!;
    private void SetPair(object first, object second, bool open)
    {
        var a = Targets(first); var b = Targets(second); a.Clear(); b.Clear(); a.Add(Id(second)); b.Add(Id(first));
        _discovered.SetValue(first, true); _discovered.SetValue(second, true);
        _hidden.SetValue(first, !open); _hidden.SetValue(second, !open);
    }
    private object CreateAt(object system, string name)
    {
        var wormhole = Activator.CreateInstance(_wormholeType)!;
        _name.SetValue(wormhole, name);
        var position = _position.Invoke(system, new object[] { -20f, 20f, -5f, 5f });
        _setup.Invoke(system, new[] { wormhole, position, null, (object)0 });
        Points(system).Add(wormhole);
        return wormhole;
    }
    public WormholePairInfo? CreatePair(Guid session, string name, string firstSystemId, string secondSystemId, bool open)
    {
        if (firstSystemId == secondSystemId) return null;
        var map = Map(false, session); if (map == null) return null;
        var before = _index.Read(map); var firstSystem = before.FindSystem(firstSystemId); var secondSystem = before.FindSystem(secondSystemId);
        if (firstSystem == null || secondSystem == null) return null;
        object? first = null, second = null;
        try
        {
            first = CreateAt(firstSystem, name); second = CreateAt(secondSystem, name); SetPair(first, second, open);
            var after = _index.Read(map);
            if (after.FindPoint(Id(first)) == null || after.FindPoint(Id(second)) == null) throw new InvalidOperationException("Wormhole pair did not enter map membership.");
            return new WormholePairInfo(Id(first), Id(second), open);
        }
        catch (Exception error)
        {
            try { if (first != null) Points(firstSystem).Remove(first); if (second != null) Points(secondSystem).Remove(second); } catch { }
            _report(error is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : error); return null;
        }
    }
    public WormholePairInfo? ResolvePair(Guid session, string firstSystemId, string secondSystemId, string firstPoiId, string secondPoiId)
    {
        var snapshot = Snapshot(true, session); if (snapshot == null) return null;
        try
        {
            var first = snapshot.FindPoint(firstPoiId); var second = snapshot.FindPoint(secondPoiId);
            if (first == null || second == null || !_wormholeType.IsInstanceOfType(first) || !_wormholeType.IsInstanceOfType(second)) return null;
            if (Id(_parent.GetValue(first)!) != firstSystemId || Id(_parent.GetValue(second)!) != secondSystemId) return null;
            var a = Targets(first); var b = Targets(second);
            if (a.Count != 1 || b.Count != 1 || (string)a[0]! != secondPoiId || (string)b[0]! != firstPoiId) return null;
            bool open = !(bool)_hidden.GetValue(first)! && !(bool)_hidden.GetValue(second)!;
            return new WormholePairInfo(firstPoiId, secondPoiId, open);
        }
        catch (Exception error) { _report(error); return null; }
    }
    public int AmbiguousCount(Guid session, string poiId)
    {
        var snapshot = Snapshot(true, session); if (snapshot == null) return 0;
        int count = 0; foreach (var pair in snapshot.Points) if (pair.Key == poiId) count++; return count;
    }
    public bool ApplyOpen(Guid session, string firstPoiId, string secondPoiId, bool open)
    {
        var map = Map(false, session); if (map == null) return false;
        var snapshot = _index.Read(map); var first = snapshot.FindPoint(firstPoiId); var second = snapshot.FindPoint(secondPoiId);
        if (first == null || second == null || !_wormholeType.IsInstanceOfType(first) || !_wormholeType.IsInstanceOfType(second)) return false;
        try { SetPair(first, second, open); return snapshot.SameMembership(_index.Read(map)); }
        catch (Exception error) { _report(error); return false; }
    }

    /// <summary>Removes both owned wormhole POIs from their systems. Refuses while the player's current
    /// POI or any waypoint is at either wormhole, and refuses when either GUID is ambiguous (a duplicated
    /// native id means the occurrence's ownership cannot be decided safely). On a post-removal
    /// verification failure the removed POIs are rolled back so the map is left unchanged.</summary>
    public WormholeDissolveOutcome DissolveWormhole(Guid session, string firstPoiId, string secondPoiId)
    {
        var map = Map(false, session); if (map == null) return WormholeDissolveOutcome.Failed;
        // A duplicated native GUID cannot be attributed safely: refuse rather than remove an arbitrary copy.
        if (AmbiguousCount(session, firstPoiId) > 1 || AmbiguousCount(session, secondPoiId) > 1)
            return WormholeDissolveOutcome.Missing;
        var before = _index.Read(map);
        var first = before.FindPoint(firstPoiId); var second = before.FindPoint(secondPoiId);
        if (first == null || second == null || !_wormholeType.IsInstanceOfType(first) || !_wormholeType.IsInstanceOfType(second))
            return WormholeDissolveOutcome.Missing;
        // Verify the pair is exactly as owned (each targets the other) before removing.
        var a = Targets(first); var b = Targets(second);
        if (a.Count != 1 || b.Count != 1 || (string)a[0]! != secondPoiId || (string)b[0]! != firstPoiId)
            return WormholeDissolveOutcome.Missing;
        // Refuse while the player is at or routed into either wormhole; relocation is the consumer's move.
        try
        {
            if (!_game.TryGetCurrentReadyPlayer(session, out var player) || player == null)
                return WormholeDissolveOutcome.Failed;
            var currentPoi = _playerCurrentPoi.GetValue(player);
            if (currentPoi != null && (ReferenceEquals(currentPoi, first) || ReferenceEquals(currentPoi, second)))
                return WormholeDissolveOutcome.PlayerInside;
            if (_playerWaypoints.GetValue(player) is System.Collections.IEnumerable waypoints)
                foreach (var waypoint in waypoints)
                    if (waypoint != null && (ReferenceEquals(waypoint, first) || ReferenceEquals(waypoint, second)))
                        return WormholeDissolveOutcome.PlayerInside;
            var firstSystem = _parent.GetValue(first);
            var secondSystem = _parent.GetValue(second);
            if (firstSystem == null || secondSystem == null) return WormholeDissolveOutcome.Missing;
            // Remove both wormholes from their systems' point lists.
            Points(firstSystem).Remove(first);
            Points(secondSystem).Remove(second);
            if (Map(false, session) == null)
            {
                Rollback(firstSystem, secondSystem, first, second);
                return WormholeDissolveOutcome.Failed;
            }
            var after = _index.Read(map);
            // Verify membership shrank by exactly these two wormhole POIs and nothing else changed.
            if (after.FindPoint(firstPoiId) != null || after.FindPoint(secondPoiId) != null
                || !VerifyDissolveDelta(before, after, first, second))
            {
                Rollback(firstSystem, secondSystem, first, second);
                return WormholeDissolveOutcome.Failed;
            }
            return WormholeDissolveOutcome.Dissolved;
        }
        catch (Exception error)
        {
            try
            {
                var fs = _parent.GetValue(first); var ss = _parent.GetValue(second);
                if (fs != null && ss != null) Rollback(fs, ss, first, second);
            }
            catch { /* rollback is best-effort only */ }
            _report(error); return WormholeDissolveOutcome.Failed;
        }
    }
    /// <summary>Best-effort restoration of both wormhole POIs into their systems after a failed removal.</summary>
    private void Rollback(object firstSystem, object secondSystem, object first, object second)
    {
        try
        {
            var fs = Points(firstSystem); var ss = Points(secondSystem);
            if (!fs.Contains(first)) fs.Add(first);
            if (!ss.Contains(second)) ss.Add(second);
        }
        catch { /* best-effort only */ }
    }
    /// <summary>Exactly the two given wormhole POIs were removed; nothing else was removed and nothing added.</summary>
    private static bool VerifyDissolveDelta(WorldMapIndex.Snapshot before, WorldMapIndex.Snapshot after,
        object first, object second)
    {
        var beforeSet = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Points) if (pair.Value != null) beforeSet.Add(pair.Value);
        var afterSet = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Points) if (pair.Value != null) afterSet.Add(pair.Value);
        if (afterSet.Count != beforeSet.Count - 2) return false;
        bool foundFirst = false, foundSecond = false;
        foreach (var value in beforeSet)
        {
            if (ReferenceEquals(value, first)) { foundFirst = true; continue; }
            if (ReferenceEquals(value, second)) { foundSecond = true; continue; }
            if (!afterSet.Contains(value)) return false; // an unrelated POI was removed
        }
        if (!foundFirst || !foundSecond) return false; // the owned wormholes were not both present
        foreach (var value in afterSet) if (!beforeSet.Contains(value)) return false; // a POI was added
        return true;
    }
    public void BeginPass(Guid session) { _inPass = true; _passSession = session; _snapshot = null; }
    public void EndPass() { _inPass = false; _snapshot = null; }
}
