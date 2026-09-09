using System;
using System.IO;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Core.Integration;

/// <summary>Rebinds verified saved identities to already-loaded native objects without reapplying defaults or creating duplicates.</summary>
internal sealed class WorldNativeReconstruction
{
    private readonly GameAdapter _game;
    private readonly WorldMapIndex _index;
    private readonly FieldInfo _map, _parent;
    private readonly Type _combat;
    internal WorldNativeReconstruction(GameAdapter game)
    {
        _game = game; var assembly = game.Bindings.Assembly;
        _index = new WorldMapIndex(assembly);
        _map = assembly.GetType("Source.Player.GamePlayer", true)!.GetField("map", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("GamePlayer.map");
        _parent = assembly.GetType("Source.Galaxy.MapElement", true)!.GetField("system", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("MapElement.system");
        _combat = assembly.GetType("Source.Galaxy.POI.Combat", true)!;
    }
    internal Func<bool> CaptureContext(Guid session)
    {
        if (!_game.TryGetObservedPlayer(session, out var player)) throw new InvalidDataException("No observed world player.");
        var map = _map.GetValue(player) ?? throw new InvalidDataException("No observed world map.");
        return () => _game.TryGetObservedPlayer(session, out var current) && ReferenceEquals(current, player) && ReferenceEquals(_map.GetValue(current), map);
    }
    internal WorldSnapshotInstance[] Read(WorldPreparedLoad prepared, Func<bool> stillAdmitted, Func<WorldSnapshotInstance, bool> constructed)
    {
        if (prepared == null || stillAdmitted == null || constructed == null) throw new ArgumentNullException("Verified load and admission fence required.");
        if (!_game.TryGetObservedPlayer(prepared.Session, out var player)) throw new InvalidDataException("World reconstruction has no observed player.");
        var map = _map.GetValue(player) ?? throw new InvalidDataException("Loaded player has no map.");
        var before = _index.Read(map);
        var rows = prepared.Generation?.Rows ?? Array.Empty<WorldSavedObject>();
        if (before.OwnedPointCount != rows.Length) throw new InvalidDataException("Loaded owned-world inventory differs from verified metadata.");
        var result = new WorldSnapshotInstance[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; var poi = before.FindPoint(row.Identity.NativeId);
            var system = before.FindSystem(row.SystemId);
            if (poi == null || poi.GetType() != _combat || system == null || !ReferenceEquals(_parent.GetValue(poi), system))
                throw new InvalidDataException("Loaded world identity, type or parent differs from verified metadata.");
            result[i] = new WorldSnapshotInstance(poi, row.Identity, row.SystemId, prepared.Generation!.DefinitionFor(row));
            if (!constructed(result[i])) throw new InvalidDataException("World instance lacks admitted native construction provenance.");
        }
        if (!stillAdmitted() || !_game.TryGetObservedPlayer(prepared.Session, out var current) || !ReferenceEquals(player, current) ||
            !ReferenceEquals(_map.GetValue(current), map) || !before.SameMembership(_index.Read(map)))
            throw new InvalidDataException("World reconstruction scope changed.");
        return result;
    }
}
