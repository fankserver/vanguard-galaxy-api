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
    /// Removes the owned POI from its host system. Ownership is structural (host system membership and
    /// parent), the player must not be at or routed to it, and the post-removal membership delta must be
    /// exactly this one POI with nothing else changed; otherwise the POI is restored.
    /// </summary>
    internal WorldRemoveOutcome TryRemove(Guid session, WorldSnapshotInstance record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (!_game.TryGetCurrentReadyPlayer(session, out var player)) return WorldRemoveOutcome.Failed;
        var map = _map.GetValue(player) ?? throw new InvalidDataException("Current player has no map.");
        var before = _index.Read(map);
        var system = before.FindSystem(record.SystemId);
        if (system == null || !ReferenceEquals(before.FindPoint(record.Identity.NativeId), record.Native) ||
            !ReferenceEquals(_parent.GetValue(record.Native), system)) return WorldRemoveOutcome.Missing;
        // Refuse while the player is at or routed into the POI; relocation is the consumer's move.
        var currentPoi = _playerCurrentPoi.GetValue(player);
        if (currentPoi != null && ReferenceEquals(currentPoi, record.Native)) return WorldRemoveOutcome.PlayerInside;
        if (_playerWaypoints.GetValue(player) is IEnumerable waypoints)
            foreach (var waypoint in waypoints)
                if (waypoint != null && ReferenceEquals(waypoint, record.Native)) return WorldRemoveOutcome.PlayerInside;
        var members = (IList)_points.GetValue(system)!;
        if (!Contains(members, record.Native)) return WorldRemoveOutcome.Missing;
        // Remove by reference, never by equality, so a native POI that overrode Equals cannot cause a
        // different-but-equal member to be removed while the owned POI survives.
        int removalIndex = IndexOf(members, record.Native);
        if (removalIndex < 0) return WorldRemoveOutcome.Missing;
        try
        {
            members.RemoveAt(removalIndex);
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
