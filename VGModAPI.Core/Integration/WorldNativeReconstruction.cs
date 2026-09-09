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
    private readonly PropertyInfo _map;
    private readonly FieldInfo _parent, _name;
    private readonly Type _combat;
    internal WorldNativeReconstruction(GameAdapter game)
    {
        _game = game; var assembly = game.Bindings.Assembly;
        _index = new WorldMapIndex(assembly);
        _map = assembly.GetType("Source.Player.GamePlayer", true)!.GetProperty("map", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("GamePlayer.map");
        _parent = assembly.GetType("Source.Galaxy.MapElement", true)!.GetField("system", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException("MapElement.system");
        _name = assembly.GetType("Source.Galaxy.MapElement", true)!.GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("MapElement._name");
        if (_name.FieldType != typeof(string) || _name.IsInitOnly) throw new InvalidDataException("Unsupported mutable world name field.");
        _combat = assembly.GetType("Source.Galaxy.POI.Combat", true)!;
    }
    internal Func<bool> CaptureContext(Guid session)
    {
        if (!_game.TryGetObservedPlayer(session, out var player)) throw new InvalidDataException("No observed world player.");
        var map = _map.GetValue(player) ?? throw new InvalidDataException("No observed world map.");
        return () => _game.TryGetObservedPlayer(session, out var current) && ReferenceEquals(current, player) && ReferenceEquals(_map.GetValue(current), map);
    }
    internal WorldSnapshotInstance[] Read(WorldPreparedLoad prepared, Func<bool> stillAdmitted, Func<WorldSnapshotInstance, bool> constructed)
        => Prepare(prepared, stillAdmitted, constructed).Instances;
    internal WorldRestorationPlan Prepare(WorldPreparedLoad prepared, Func<bool> stillAdmitted, Func<WorldSnapshotInstance, bool> constructed, Func<WorldSavedDefinition, WorldSavedDefinition?>? effective = null)
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
        var names = new string?[result.Length]; var replacements = new string?[result.Length];
        if (effective != null)
        {
            for (int i = 0; i < result.Length; i++)
            {
                var record = result[i]; var previous = record.Definition.Definition;
                var target = effective(record.Definition) ?? throw new InvalidDataException("World definition migration is unavailable.");
                var next = target.Definition;
                if (target.Owner != record.Identity.Owner || next.LocalId != previous.LocalId || next.Revision < previous.Revision ||
                    next.FactionId != previous.FactionId || next.Level != previous.Level || (next.Revision == previous.Revision && next.Name != previous.Name))
                    throw new InvalidDataException("Unsupported world definition migration.");
                names[i] = (string?)_name.GetValue(record.Native);
                replacements[i] = names[i] == previous.Name ? next.Name : names[i];
                result[i] = new WorldSnapshotInstance(record.Native, record.Identity, record.SystemId, target);
            }
        }
        if (!stillAdmitted() || !_game.TryGetObservedPlayer(prepared.Session, out var current) || !ReferenceEquals(player, current) ||
            !ReferenceEquals(_map.GetValue(current), map) || !before.SameMembership(_index.Read(map)))
            throw new InvalidDataException("World reconstruction scope changed.");
        if (effective == null) return new WorldRestorationPlan(result);
        int attempted = 0;
        return new WorldRestorationPlan(result, () =>
        {
            for (int i = 0; i < result.Length; i++)
                if ((string?)_name.GetValue(result[i].Native) != names[i]) throw new InvalidDataException("World name changed during migration approval.");
            for (int i = 0; i < result.Length; i++)
            { attempted = i + 1; _name.SetValue(result[i].Native, replacements[i]); }
        }, () =>
        {
            for (int i = attempted - 1; i >= 0; i--) _name.SetValue(result[i].Native, names[i]);
            attempted = 0;
        });
    }
}
