using System;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Core.Integration;

/// <summary>
/// Installed reflection-backed native seam for authored pocket systems. Pocket creation uses the game's
/// own SandboxWorld.AddSideContentSystemToSystem (no storyteller => nothing is generated inside), the
/// entrance gate is resolved structurally (GetEntranceJumpgate + GetTargetPOI), and both paired gates are
/// unhidden and opened/closed together. All faults fail open and report once per distinct cause.
/// </summary>
internal sealed class WorldNativePocketSystems : IPocketSystemNative
{
    private readonly GameAdapter _game;
    private readonly WorldMapIndex _index;
    private readonly Type _jumpGateType;
    private readonly PropertyInfo _map;
    private readonly FieldInfo _sector, _parent, _hidden, _jumpgateOpen;
    private readonly FieldInfo _sectorSystems, _playerCurrentSystem, _playerCurrentPoi, _playerWaypoints;
    private readonly PropertyInfo _guid;
    private readonly MethodInfo _create, _entrance, _target, _unlock, _lock, _removePoi;
    private readonly int _level;
    private readonly Action<Exception> _report;
    private bool _inPass;
    private Guid _passSession;
    private WorldMapIndex.Snapshot? _passSnapshot;

    internal WorldNativePocketSystems(GameAdapter game, Assembly assembly, int level = 10, Action<Exception>? report = null)
    {
        _game = game; _level = level; _report = report ?? (_ => { });
        _index = new WorldMapIndex(assembly);
        _jumpGateType = assembly.GetType(PocketSystemBindings.JumpGate, true)!;
        _map = assembly.GetType("Source.Player.GamePlayer", true)!.GetProperty("map", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("GamePlayer.map");
        _sector = Field(assembly.GetType(PocketSystemBindings.System, true)!, "sector");
        _parent = Field(assembly.GetType(PocketSystemBindings.Element, true)!, "system");
        _sectorSystems = Field(assembly.GetType(PocketSystemBindings.Sector, true)!, "systems");
        var playerType = assembly.GetType(PocketSystemBindings.Player, true)!;
        _playerCurrentSystem = Field(playerType, "currentSystem");
        _playerCurrentPoi = Field(playerType, "currentPointOfInterest");
        _playerWaypoints = Field(playerType, "waypoints");
        _hidden = Field(assembly.GetType(PocketSystemBindings.Poi, true)!, "hidden");
        _jumpgateOpen = Field(_jumpGateType, "jumpgateOpen");
        _guid = assembly.GetType(PocketSystemBindings.Element, true)!.GetProperty("guid", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("MapElement.guid");
        var resolved = PocketSystemBindings.Validate(assembly);
        _create = resolved["authoredCreate"]; _entrance = resolved["authoredEntrance"];
        _target = resolved["gateTarget"]; _unlock = resolved["gateUnlock"]; _lock = resolved["gateLock"];
        _removePoi = resolved["systemRemovePoi"];
    }
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.FullName, name);
    private object? Map(bool observed, Guid session, out object? player)
    {
        player = null;
        object? observedPlayer = null, readyPlayer = null;
        bool ok;
        if (observed) ok = _game.TryGetObservedPlayer(session, out observedPlayer);
        else ok = _game.TryGetCurrentReadyPlayer(session, out readyPlayer);
        if (!ok) return null;
        player = observed ? observedPlayer : readyPlayer;
        if (player == null) return null;
        return _map.GetValue(player);
    }
    private string Id(object element) => (string)_guid.GetValue(element)!;
    private PocketSystemInfo? ResolveFromSystem(object system)
    {
        object? entrance;
        try { entrance = _entrance.Invoke(system, null); } catch (Exception e) { ReportInvoke(e); return null; }
        if (entrance == null || !_jumpGateType.IsInstanceOfType(entrance)) return null;
        object? peer;
        try { peer = _target.Invoke(entrance, null); } catch (Exception e) { ReportInvoke(e); return null; }
        if (peer == null || !_jumpGateType.IsInstanceOfType(peer)) return null;
        return new PocketSystemInfo(Id(system), Id(entrance), Id(peer));
    }
    private void ReportInvoke(Exception error)
    {
        if (error is TargetInvocationException tie && tie.InnerException != null) _report(tie.InnerException);
        else _report(error);
    }

    /// <summary>Bounds a reconciliation pass to a single observed snapshot for its read paths (ResolvePocket/AmbiguousCount/IsOpen).</summary>
    public void BeginPass(Guid session) { _inPass = true; _passSession = session; _passSnapshot = null; }
    public void EndPass() { _inPass = false; _passSnapshot = null; }
    private WorldMapIndex.Snapshot? Snapshot(bool observed, Guid session)
    {
        bool cache = observed && _inPass && session == _passSession;
        if (cache && _passSnapshot != null) return _passSnapshot;
        var map = Map(observed, session, out _);
        if (map == null) return null;
        var snapshot = _index.Read(map);
        if (cache) _passSnapshot = snapshot;
        return snapshot;
    }

    /// <summary>
    /// Verifies a genuinely-successful pocket creation changed membership by EXACTLY one new system
    /// (the created pocket), its pocket-side gate POI, and the one anchor-side entrance gate POI — and
    /// removed nothing and adopted nothing foreign. Never accepts a no-op or any extra membership growth.
    /// </summary>
    internal static bool VerifyPocketDelta(WorldMapIndex.Snapshot before, WorldMapIndex.Snapshot after,
        object created, object anchor, Func<object, bool> isGate, Func<object, object?> parentOf)
    {
        var beforeSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Systems) beforeSystems.Add(pair.Value);
        var afterSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Systems) afterSystems.Add(pair.Value);
        if (afterSystems.Count != beforeSystems.Count + 1 || Has(beforeSystems, created) || !Has(afterSystems, created)) return false;
        foreach (var value in beforeSystems) if (!Has(afterSystems, value)) return false; // a prior system was removed
        var beforePoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Points) beforePoints.Add(pair.Value);
        var afterPoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Points) afterPoints.Add(pair.Value);
        if (afterPoints.Count != beforePoints.Count + 2) return false;
        foreach (var value in beforePoints) if (!Has(afterPoints, value)) return false; // a prior POI was removed
        int anchorGates = 0, pocketGates = 0;
        foreach (var value in afterPoints)
        {
            if (Has(beforePoints, value)) continue;
            if (!isGate(value)) return false; // the only new POIs must be jump gates
            var parent = parentOf(value);
            if (ReferenceEquals(parent, anchor)) anchorGates++;
            else if (ReferenceEquals(parent, created)) pocketGates++;
            else return false; // a new POI parented somewhere foreign
        }
        return anchorGates == 1 && pocketGates == 1;
    }
    private static bool Has(System.Collections.Generic.HashSet<object> set, object value)
    {
        foreach (var item in set) if (ReferenceEquals(item, value)) return true;
        return false;
    }

    public PocketSystemInfo? CreatePocket(Guid session, string anchorSystemId)
    {
        var map = Map(false, session, out var player);
        if (map == null || player == null) return null;
        WorldMapIndex.Snapshot before;
        try { before = _index.Read(map); }
        catch (Exception e) { _report(e); return null; }
        var anchor = before.FindSystem(anchorSystemId);
        if (anchor == null) return null;
        var sector = _sector.GetValue(anchor);
        if (sector == null) return null;
        try
        {
            var created = _create.Invoke(null, new[] { sector, anchor, _level });
            if (created == null || !_system_IsInstance(created)) return null;
            if (Map(false, session, out var current) == null || !ReferenceEquals(current, player)) return null;
            WorldMapIndex.Snapshot after;
            try { after = _index.Read(map); } catch (Exception e) { _report(e); return null; }
            if (!VerifyPocketDelta(before, after, created, anchor, o => _jumpGateType.IsInstanceOfType(o), o => _parent.GetValue(o)))
                return null;
            var info = ResolveFromSystem(created);
            if (info == null) return null;
            return info;
        }
        catch (Exception e) { ReportInvoke(e); return null; }
    }
    private bool _system_IsInstance(object value) => value.GetType().FullName == PocketSystemBindings.System;

    /// <summary>
    /// Verifies a dissolution changed membership by EXACTLY the removed pocket system, the anchor-side
    /// entrance gate and the POIs parented to the pocket — and removed nothing else and added nothing.
    /// </summary>
    internal static bool VerifyDissolveDelta(WorldMapIndex.Snapshot before, WorldMapIndex.Snapshot after,
        object removedSystem, object removedEntrance, Func<object, object?> parentOf)
    {
        var beforeSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Systems) beforeSystems.Add(pair.Value);
        var afterSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Systems) afterSystems.Add(pair.Value);
        if (afterSystems.Count != beforeSystems.Count - 1 || Has(afterSystems, removedSystem)) return false;
        foreach (var value in afterSystems) if (!Has(beforeSystems, value)) return false; // a system was added
        var afterPoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Points) afterPoints.Add(pair.Value);
        var beforePoints = new System.Collections.Generic.HashSet<object>();
        int expectedRemoved = 0;
        foreach (var pair in before.Points)
        {
            beforePoints.Add(pair.Value);
            bool shouldGo = ReferenceEquals(pair.Value, removedEntrance) || ReferenceEquals(parentOf(pair.Value), removedSystem);
            if (shouldGo) expectedRemoved++;
            if (shouldGo == Has(afterPoints, pair.Value)) return false; // survived a removal or vanished unexpectedly
        }
        if (afterPoints.Count != beforePoints.Count - expectedRemoved) return false;
        foreach (var value in afterPoints) if (!Has(beforePoints, value)) return false; // a POI was added
        return true;
    }

    public PocketDissolveOutcome DissolvePocket(Guid session, string systemId, string entranceGateId, string pocketGateId)
    {
        var map = Map(false, session, out var player);
        if (map == null || player == null) return PocketDissolveOutcome.Failed;
        WorldMapIndex.Snapshot before;
        try { before = _index.Read(map); }
        catch (Exception e) { _report(e); return PocketDissolveOutcome.Failed; }
        var system = before.FindSystem(systemId);
        var entrance = before.FindPoint(entranceGateId);
        var peer = before.FindPoint(pocketGateId);
        if (system == null || entrance == null || peer == null
            || !_jumpGateType.IsInstanceOfType(entrance) || !_jumpGateType.IsInstanceOfType(peer)) return PocketDissolveOutcome.Missing;
        try
        {
            var anchor = _parent.GetValue(entrance);
            if (anchor == null || ReferenceEquals(anchor, system) || !ReferenceEquals(_parent.GetValue(peer), system))
                return PocketDissolveOutcome.Missing;
            // Refuse while the player is inside the pocket or routed into it; relocation is the consumer's move.
            if (ReferenceEquals(_playerCurrentSystem.GetValue(player), system)) return PocketDissolveOutcome.PlayerInside;
            var currentPoi = _playerCurrentPoi.GetValue(player);
            if (currentPoi != null && ReferenceEquals(_parent.GetValue(currentPoi), system)) return PocketDissolveOutcome.PlayerInside;
            if (_playerWaypoints.GetValue(player) is System.Collections.IEnumerable waypoints)
                foreach (var waypoint in waypoints)
                    if (waypoint != null && (ReferenceEquals(waypoint, entrance) || ReferenceEquals(_parent.GetValue(waypoint), system)))
                        return PocketDissolveOutcome.PlayerInside;
            var sector = _sector.GetValue(system);
            if (sector == null || _sectorSystems.GetValue(sector) is not System.Collections.IList systems)
                return PocketDissolveOutcome.Missing;
            int index = -1;
            for (int i = 0; i < systems.Count; i++) if (ReferenceEquals(systems[i], system)) { index = i; break; }
            if (index < 0) return PocketDissolveOutcome.Missing;
            _removePoi.Invoke(anchor, new[] { entrance });
            systems.RemoveAt(index);
            if (Map(false, session, out var current) == null || !ReferenceEquals(current, player)) return PocketDissolveOutcome.Failed;
            WorldMapIndex.Snapshot after;
            try { after = _index.Read(map); }
            catch (Exception e) { _report(e); return PocketDissolveOutcome.Failed; }
            return VerifyDissolveDelta(before, after, system, entrance, o => _parent.GetValue(o))
                ? PocketDissolveOutcome.Dissolved : PocketDissolveOutcome.Failed;
        }
        catch (Exception e) { ReportInvoke(e); return PocketDissolveOutcome.Failed; }
    }

    public PocketSystemInfo? ResolvePocket(Guid session, string systemId)
    {
        var snapshot = Snapshot(true, session);
        if (snapshot == null) return null;
        try
        {
            var system = snapshot.FindSystem(systemId);
            if (system == null) return null;
            return ResolveFromSystem(system);
        }
        catch (Exception e) { _report(e); return null; }
    }

    public int AmbiguousCount(Guid session, string systemId)
    {
        var snapshot = Snapshot(true, session);
        if (snapshot == null) return 0;
        int count = 0;
        try
        {
            foreach (var pair in snapshot.Systems) if (pair.Key == systemId) count++;
        }
        catch (Exception e) { _report(e); }
        return count;
    }

    public bool ApplyOpen(Guid session, string entranceGateId, string pocketGateId, bool open)
    {
        var map = Map(false, session, out var player);
        if (map == null || player == null) return false;
        WorldMapIndex.Snapshot before;
        try { before = _index.Read(map); }
        catch (Exception e) { _report(e); return false; }
        var entrance = before.FindPoint(entranceGateId);
        var peer = before.FindPoint(pocketGateId);
        if (entrance == null || peer == null || !_jumpGateType.IsInstanceOfType(entrance) || !_jumpGateType.IsInstanceOfType(peer)) return false;
        try
        {
            // Unhide + open/close both paired gates together.
            _hidden.SetValue(entrance, !open);
            _hidden.SetValue(peer, !open);
            _jumpgateOpen.SetValue(entrance, open);
            _jumpgateOpen.SetValue(peer, open);
            if (open) _unlock.Invoke(entrance, null);
            else { _lock.Invoke(entrance, null); _lock.Invoke(peer, null); }
            Map(false, session, out var currentOk);
            return currentOk != null && ReferenceEquals(currentOk, player) && before.SameMembership(_index.Read(map));
        }
        catch (Exception e) { ReportInvoke(e); return false; }
    }

    public bool IsOpen(Guid session, string entranceGateId, string pocketGateId)
    {
        var snapshot = Snapshot(true, session);
        if (snapshot == null) return false;
        try
        {
            var entrance = snapshot.FindPoint(entranceGateId);
            var peer = snapshot.FindPoint(pocketGateId);
            if (entrance == null || peer == null) return false;
            return (bool)_jumpgateOpen.GetValue(entrance)! && !(bool)_hidden.GetValue(entrance)! &&
                (bool)_jumpgateOpen.GetValue(peer)! && !(bool)_hidden.GetValue(peer)!;
        }
        catch (Exception e) { _report(e); return false; }
    }
}
