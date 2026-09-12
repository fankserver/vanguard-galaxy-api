using System;
using System.Collections;
using System.IO;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Core.Integration;

/// <summary>Typed outcome of a native owned-world removal attempt.</summary>
internal enum WorldRemoveOutcome
{
    /// <summary>The owned POI was removed from its host system; it is gone from the live map.</summary>
    Removed,
    /// <summary>The player's current location or a waypoint is at the POI; nothing was removed.</summary>
    PlayerInside,
    /// <summary>The owned POI is not currently present natively; nothing was removed.</summary>
    Missing,
    /// <summary>The native removal could not be performed or verified; the map may be unchanged.</summary>
    Failed
}

/// <summary>Native append with player/map/placement fences. The caller owns persistence and registry admission.</summary>
internal sealed class WorldNativeAttachment
{
    private readonly GameAdapter _game;
    private readonly WorldMapIndex _index;
    private readonly WorldDetachedCombatFactory _factory;
    private readonly PropertyInfo _map;
    private readonly FieldInfo _points, _parent, _playerCurrentPoi, _playerWaypoints;
    // The game's own generic teardown for an authored POI: SystemMapData.RemovePointOfInterest(poi)
    // => pointsOfInterest.Remove(poi). Creation has no generic native op (only typed Add* creators),
    // so TryAppend mutates the field, but removal does, so we delegate rather than re-implement it.
    private readonly MethodInfo _removePointOfInterest;
    // Fixed callback-free inspection only; no callbacks or mutation may follow the final admission fence.
    private readonly Action<object>? _profile;
    internal WorldNativeAttachment(GameAdapter game, Action<object>? profile = null)
    {
        _game = game; _profile = profile;
        var assembly = game.Bindings.Assembly;
        _index = new WorldMapIndex(assembly); _factory = new WorldDetachedCombatFactory(assembly);
        _map = assembly.GetType("Source.Player.GamePlayer", true)!.GetProperty("map", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("GamePlayer.map");
        _parent = assembly.GetType("Source.Galaxy.MapElement", true)!.GetField("system", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("MapElement.system");
        _points = assembly.GetType("Source.Galaxy.SystemMapData", true)!.GetField("pointsOfInterest", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("SystemMapData.pointsOfInterest");
        _removePointOfInterest = assembly.GetType("Source.Galaxy.SystemMapData", true)!.GetMethod("RemovePointOfInterest", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException("SystemMapData.RemovePointOfInterest");
        var player = assembly.GetType("Source.Player.GamePlayer", true)!;
        _playerCurrentPoi = player.GetField("currentPointOfInterest", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("GamePlayer.currentPointOfInterest");
        _playerWaypoints = player.GetField("waypoints", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("GamePlayer.waypoints");
    }
    private bool CheckProfile(object poi) { _profile?.Invoke(poi); return true; }
    internal bool Contains(Guid session, WorldSnapshotInstance record, bool observed = false)
    {
        bool Player(out object? value) => observed ? _game.TryGetObservedPlayer(session, out value) : _game.TryGetCurrentReadyPlayer(session, out value);
        if (!Player(out var player)) return false;
        var map = _map.GetValue(player) ?? throw new InvalidDataException("Current player has no map.");
        var membership = _index.Read(map);
        var system = membership.FindSystem(record.SystemId);
        return system != null && CheckProfile(record.Native) && ReferenceEquals(membership.FindPoint(record.Identity.NativeId), record.Native) &&
            ReferenceEquals(_parent.GetValue(record.Native), system) && Player(out var current) &&
            ReferenceEquals(current, player) && ReferenceEquals(_map.GetValue(current), map);
    }
    internal WorldSnapshotInstance? TryAppend(Guid session, WorldSavedDefinition definition, WorldObjectIdentity identity,
        string systemId, float x, float y, Func<bool> admission, Action<WorldSnapshotInstance>? prepare = null)
    {
        if (admission == null) throw new ArgumentNullException(nameof(admission));
        if (!_game.TryGetCurrentReadyPlayer(session, out var player)) return null;
        var map = _map.GetValue(player) ?? throw new InvalidDataException("Current player has no map.");
        var before = _index.Read(map);
        var system = before.FindSystem(systemId);
        if (system == null || before.FindPoint(identity.NativeId) != null) return null;
        var members = (IList)_points.GetValue(system)!;
        var expected = WorldMembershipTransaction.Capture(members);
        var created = _factory.Create(definition, identity, system, x, y);
        // Validate and allocate the returned descriptor before the only native mutation.
        var record = new WorldSnapshotInstance(created, identity, systemId, definition);
        prepare?.Invoke(record);
        bool appended = WorldMembershipTransaction.TryAppend(members, expected, created, () =>
            admission() && _game.TryGetCurrentReadyPlayer(session, out var current) && ReferenceEquals(current, player) &&
            ReferenceEquals(_map.GetValue(current), map) && before.SameMembership(_index.Read(map)) &&
            CheckProfile(created) && _factory.MatchesCreated(created, definition, identity, system, x, y));
        return appended ? record : null;
    }

    /// <summary>
    /// Removes the owned POI from its host system. This is the plain native removal: ownership is still
    /// structural (host system membership and parent) and the post-removal membership delta must be
    /// exactly this one POI with nothing else changed (otherwise the POI is restored), but it does not
    /// check transient player-safety conditions — the modder inspects <see cref="Readiness"/> first or
    /// uses the deferred removal path.
    /// </summary>
    internal WorldRemoveOutcome RemoveChecked(Guid session, WorldSnapshotInstance record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (!_game.TryGetCurrentReadyPlayer(session, out var player)) return WorldRemoveOutcome.Failed;
        var map = _map.GetValue(player) ?? throw new InvalidDataException("Current player has no map.");
        var before = _index.Read(map);
        var system = before.FindSystem(record.SystemId);
        if (system == null || !ReferenceEquals(before.FindPoint(record.Identity.NativeId), record.Native) ||
            !ReferenceEquals(_parent.GetValue(record.Native), system)) return WorldRemoveOutcome.Missing;
        var members = (IList)_points.GetValue(system)!;
        if (!Contains(members, record.Native)) return WorldRemoveOutcome.Missing;
        // Capture the slot for rollback ordering only; the removal itself is the game's own teardown.
        int removalIndex = IndexOf(members, record.Native);
        if (removalIndex < 0) return WorldRemoveOutcome.Missing;
        try
        {
            // Delegate to the game's removal rather than hand-rolling field surgery. The game removes
            // by equality (pointsOfInterest.Remove(poi)), so a native POI that overrides Equals is
            // handled exactly as the game itself would handle it. Our VerifyRemoveDelta + rollback
            // below remain the atomicity/save-safety net (unchanged by the primitive we delegate to).
            _removePointOfInterest.Invoke(system, new object[] { record.Native });
            if (!_game.TryGetCurrentReadyPlayer(session, out var current) || !ReferenceEquals(current, player) ||
                !ReferenceEquals(_map.GetValue(current), map))
            { Rollback(members, record.Native, removalIndex); return WorldRemoveOutcome.Failed; }
            var after = _index.Read(map);
            if (!VerifyRemoveDelta(before, after, record.Native, system)) { Rollback(members, record.Native, removalIndex); return WorldRemoveOutcome.Failed; }
            return WorldRemoveOutcome.Removed;
        }
        catch
        {
            try { Rollback(members, record.Native, removalIndex); } catch { /* rollback is best-effort only */ }
            throw;
        }
    }

    /// <summary>
    /// Pure readiness report for removing this owned POI: <see cref="WorldContentRemovalStatus.Ready"/>,
    /// <see cref="WorldContentRemovalStatus.PlayerInside"/>,
    /// <see cref="WorldContentRemovalStatus.NotPresent"/>, or
    /// <see cref="WorldContentRemovalStatus.Unavailable"/> when the world cannot be inspected. Never
    /// mutates native state.
    /// </summary>
    internal WorldContentRemovalStatus Readiness(Guid session, WorldSnapshotInstance record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (!_game.TryGetCurrentReadyPlayer(session, out var player)) return WorldContentRemovalStatus.Unavailable;
        var map = _map.GetValue(player);
        if (map == null) return WorldContentRemovalStatus.Unavailable;
        var before = _index.Read(map);
        var system = before.FindSystem(record.SystemId);
        if (system == null || !ReferenceEquals(before.FindPoint(record.Identity.NativeId), record.Native) ||
            !ReferenceEquals(_parent.GetValue(record.Native), system)) return WorldContentRemovalStatus.NotPresent;
        if (PlayerIsAt(player, record.Native)) return WorldContentRemovalStatus.PlayerInside;
        return WorldContentRemovalStatus.Ready;
    }

    private bool PlayerIsAt(object? player, object poi)
    {
        if (player == null) return false;
        var currentPoi = _playerCurrentPoi.GetValue(player);
        if (currentPoi != null && ReferenceEquals(currentPoi, poi)) return true;
        if (_playerWaypoints.GetValue(player) is IEnumerable waypoints)
            foreach (var waypoint in waypoints)
                if (waypoint != null && ReferenceEquals(waypoint, poi)) return true;
        return false;
    }

    private static bool Contains(IList members, object value)
    { foreach (var item in members) if (ReferenceEquals(item, value)) return true; return false; }

    private static int IndexOf(IList members, object value)
    { for (int i = 0; i < members.Count; i++) if (ReferenceEquals(members[i], value)) return i; return -1; }

    private static void Rollback(IList members, object poi, int index)
    { try { if (IndexOf(members, poi) < 0) members.Insert(index < members.Count ? index : members.Count, poi); } catch { /* best-effort only */ } }

    /// <summary>Exactly the given POI was removed from the host and nothing else changed.</summary>
    private static bool VerifyRemoveDelta(WorldMapIndex.Snapshot before, WorldMapIndex.Snapshot after, object removed, object host)
    {
        var beforePoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Points) beforePoints.Add(pair.Value);
        var afterPoints = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Points) afterPoints.Add(pair.Value);
        if (afterPoints.Count != beforePoints.Count - 1) return false;
        bool removedPresent = false;
        foreach (var value in beforePoints)
        {
            if (ReferenceEquals(value, removed)) { removedPresent = true; continue; }
            if (!DeltaContains(afterPoints, value)) return false;
        }
        if (!removedPresent) return false;
        foreach (var value in afterPoints) if (!DeltaContains(beforePoints, value)) return false;
        var beforeSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in before.Systems) beforeSystems.Add(pair.Value);
        var afterSystems = new System.Collections.Generic.HashSet<object>();
        foreach (var pair in after.Systems) afterSystems.Add(pair.Value);
        if (afterSystems.Count != beforeSystems.Count) return false;
        bool hostPresent = false;
        foreach (var value in afterSystems)
        { if (!DeltaContains(beforeSystems, value)) return false; if (ReferenceEquals(value, host)) hostPresent = true; }
        return hostPresent;
        static bool DeltaContains(System.Collections.Generic.HashSet<object> set, object value)
        { foreach (var item in set) if (ReferenceEquals(item, value)) return true; return false; }
    }
}
