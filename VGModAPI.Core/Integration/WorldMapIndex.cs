using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>Bounded identity lookup in one supplied native map. The caller must establish its current-player provenance.</summary>
internal sealed class WorldMapIndex
{
    private readonly FieldInfo _sectors, _systems, _points, _guid, _parent;
    internal WorldMapIndex(Assembly assembly)
    {
        _sectors = Field(assembly.GetType("Source.Galaxy.GalaxyMapData", true)!, "sectors");
        _systems = Field(assembly.GetType("Source.Galaxy.SectorMapData", true)!, "systems");
        _points = Field(assembly.GetType("Source.Galaxy.SystemMapData", true)!, "pointsOfInterest");
        var element = assembly.GetType("Source.Galaxy.MapElement", true)!;
        _guid = Field(element, "<guid>k__BackingField"); _parent = Field(element, "system");
    }
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.FullName, name);
    private string Id(object value)
    {
        var id = _guid.GetValue(value) as string;
        if (string.IsNullOrEmpty(id) || id!.Length > 4096) throw new InvalidDataException("Invalid native world identity.");
        return id;
    }
    private static object[] Members(FieldInfo field, object value) => WorldMembershipTransaction.Capture(
        field.GetValue(value) as IList ?? throw new InvalidDataException("Missing native world membership."));

    internal Snapshot Read(object map)
    {
        var systems = new Dictionary<string, object>(StringComparer.Ordinal);
        var points = new Dictionary<string, object>(StringComparer.Ordinal);
        var references = new List<object>(); var identities = new List<string>();
        var seen = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();
        int visited = 0;
        void Visit(object value)
        {
            if (++visited > 100000 || seen.TryGetValue(value, out _)) throw new InvalidDataException("Duplicate or excessive native world membership.");
            seen.Add(value, new object()); references.Add(value); identities.Add(Id(value));
        }
        foreach (var sector in Members(_sectors, map))
        {
            Visit(sector);
            foreach (var system in Members(_systems, sector))
            {
                Visit(system);
                if (systems.ContainsKey(Id(system))) throw new InvalidDataException("Ambiguous native system identity.");
                systems.Add(Id(system), system);
                foreach (var poi in Members(_points, system))
                {
                    Visit(poi);
                    if (!ReferenceEquals(_parent.GetValue(poi), system) || points.ContainsKey(Id(poi)))
                        throw new InvalidDataException("Ambiguous native POI identity or parent.");
                    points.Add(Id(poi), poi);
                }
            }
        }
        return new Snapshot(map, systems, points, references.ToArray(), identities.ToArray());
    }

    internal sealed class Snapshot
    {
        private readonly object _map;
        private readonly Dictionary<string, object> _systems, _points;
        private readonly object[] _references;
        private readonly string[] _identities;
        internal Snapshot(object map, Dictionary<string, object> systems, Dictionary<string, object> points, object[] references, string[] identities)
        { _map = map; _systems = systems; _points = points; _references = references; _identities = identities; }
        internal object? FindSystem(string id) => _systems.TryGetValue(id, out var system) ? system : null;
        internal object? FindPoint(string id) => _points.TryGetValue(id, out var poi) ? poi : null;
        internal bool SameMembership(Snapshot other)
        {
            if (!ReferenceEquals(_map, other._map) || _references.Length != other._references.Length) return false;
            for (int i = 0; i < _references.Length; i++)
                if (!ReferenceEquals(_references[i], other._references[i]) || _identities[i] != other._identities[i]) return false;
            // Parent partitions matter even when flattened traversal order remains unchanged.
            foreach (var pair in _points)
                if (!other._points.TryGetValue(pair.Key, out var point) || !ReferenceEquals(pair.Value, point)) return false;
            return true;
        }
    }
}
