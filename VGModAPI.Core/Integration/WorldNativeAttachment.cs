using System;
using System.Collections;
using System.IO;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Core.Integration;

/// <summary>Native append with player/map/placement fences. The caller owns persistence and registry admission.</summary>
internal sealed class WorldNativeAttachment
{
    private readonly GameAdapter _game;
    private readonly WorldMapIndex _index;
    private readonly WorldDetachedCombatFactory _factory;
    private readonly FieldInfo _map, _points, _parent;
    internal WorldNativeAttachment(GameAdapter game)
    {
        _game = game;
        var assembly = game.Bindings.Assembly;
        _index = new WorldMapIndex(assembly); _factory = new WorldDetachedCombatFactory(assembly);
        _map = assembly.GetType("Source.Player.GamePlayer", true)!.GetField("map", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("GamePlayer.map");
        _parent = assembly.GetType("Source.Galaxy.MapElement", true)!.GetField("system", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("MapElement.system");
        _points = assembly.GetType("Source.Galaxy.SystemMapData", true)!.GetField("pointsOfInterest", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("SystemMapData.pointsOfInterest");
    }
    internal bool Contains(Guid session, WorldSnapshotInstance record, bool observed = false)
    {
        bool Player(out object? value) => observed ? _game.TryGetObservedPlayer(session, out value) : _game.TryGetCurrentReadyPlayer(session, out value);
        if (!Player(out var player)) return false;
        var map = _map.GetValue(player) ?? throw new InvalidDataException("Current player has no map.");
        var membership = _index.Read(map);
        var system = membership.FindSystem(record.SystemId);
        return system != null && ReferenceEquals(membership.FindPoint(record.Identity.NativeId), record.Native) &&
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
            _factory.MatchesCreated(created, definition, identity, system, x, y));
        return appended ? record : null;
    }
}
