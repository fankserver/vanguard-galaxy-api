using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

internal sealed class NavigationNativeReader
{
    private readonly WorldMapIndex _index;
    private readonly FieldInfo _name, _hidden, _visited, _system;
    private readonly PropertyInfo _guid;
    private readonly MethodInfo _adjacent;
    private readonly Type _station;
    internal NavigationNativeReader(Assembly assembly)
    {
        _index = new WorldMapIndex(assembly);
        var element = assembly.GetType("Source.Galaxy.MapElement", true)!;
        var poi = assembly.GetType("Source.Galaxy.MapPointOfInterest", true)!;
        _name = element.GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic)!;
        _system = element.GetField("system")!; _guid = element.GetProperty("guid")!;
        _hidden = poi.GetField("hidden")!; _visited = poi.GetField("lastVisitedTime")!;
        _adjacent = assembly.GetType("Source.Galaxy.SystemMapData", true)!.GetMethod("GetAdjacentSystems", Type.EmptyTypes)!;
        _station = assembly.GetType("Source.Galaxy.POI.SpaceStation", true)!;
        if (_name == null || _system == null || _guid == null || _hidden == null || _visited == null || _adjacent == null)
            throw new MissingMemberException("Navigation bindings unavailable.");
    }
    internal NavigationMap Read(object map, Func<bool> current)
    {
        var snapshot = _index.Read(map);
        var edges = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Systems)
        {
            var neighbors = new List<string>();
            foreach (var next in (IEnumerable)_adjacent.Invoke(entry.Value, null)!)
            {
                if (next == null) continue;
                var id = (string)_guid.GetValue(next)!;
                if (!ReferenceEquals(snapshot.FindSystem(id), next)) throw new InvalidDataException("Jump gate targets an absent system.");
                neighbors.Add(id);
            }
            edges.Add(entry.Key, neighbors.ToArray());
        }
        var stations = new List<NavigationStation>();
        foreach (var entry in snapshot.Points)
        {
            if (!_station.IsInstanceOfType(entry.Value) || (bool)_hidden.GetValue(entry.Value)!) continue;
            var parent = _system.GetValue(entry.Value)!;
            stations.Add(new NavigationStation(entry.Key, (string)_guid.GetValue(parent)!, _name.GetValue(entry.Value) as string,
                (float)_visited.GetValue(entry.Value)! > 0));
        }
        return new NavigationMap(edges, stations.ToArray(), () => current() && snapshot.SameMembership(_index.Read(map)));
    }
    internal object? FindPoint(object map, string id) => _index.Read(map).FindPoint(id);
    internal bool Hidden(object poi) => (bool)_hidden.GetValue(poi)!;
}
