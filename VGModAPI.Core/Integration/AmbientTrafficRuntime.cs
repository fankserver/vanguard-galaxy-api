using System;
using System.Collections;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>
/// Reads the decorative spawner's current location and re-resolves declared anchors in the current
/// player's map on every call. Any failure fails open: vanilla traffic proceeds untouched.
/// </summary>
internal sealed class AmbientTrafficRuntime
{
    private readonly AmbientTrafficService _service;
    private readonly Action<Exception> _report;
    private readonly Type _manager;
    private readonly PropertyInfo _poi, _guid, _current, _allPois;
    private readonly FieldInfo _system;
    private bool _reported;
    internal AmbientTrafficRuntime(Assembly assembly, AmbientTrafficService service, Action<Exception> report)
    {
        _service = service; _report = report;
        _manager = assembly.GetType("Behaviour.Managers.BasePoiManager", true)!;
        _poi = Property(_manager, "poi");
        var element = assembly.GetType("Source.Galaxy.MapElement", true)!;
        _guid = Property(element, "guid");
        _system = element.GetField("system", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException(element.FullName, "system");
        var map = assembly.GetType("Source.Galaxy.GalaxyMapData", true)!;
        _current = Property(map, "current");
        _allPois = Property(map, "allPointsOfInterest");
    }
    private static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        ?? throw new MissingMemberException(type.FullName, name);

    internal bool SuppressStationVisitor(object? manager) => Suppress(AmbientSpawnSite.Station, manager);
    internal bool SuppressGateTraffic(object? manager) => Suppress(AmbientSpawnSite.JumpGate, manager);
    internal bool SuppressWormholeTraffic(object? manager) => Suppress(AmbientSpawnSite.Wormhole, manager);
    /// <summary>A quiet wormhole spawns no security patrol either; other POIs keep theirs.</summary>
    internal bool SuppressQuietWormholePatrol(object? manager)
    {
        try
        {
            if (manager == null || !_manager.IsInstanceOfType(manager)) return false;
            var poi = _poi.GetValue(manager);
            if (poi == null) return false;
            return _service.ShouldSuppressPatrol(_guid.GetValue(poi) as string);
        }
        catch (Exception error)
        {
            if (!_reported) { _reported = true; try { _report(error); } catch { } }
            return false;
        }
    }

    private bool Suppress(AmbientSpawnSite site, object? manager)
    {
        try
        {
            if (manager == null || !_manager.IsInstanceOfType(manager)) return false;
            var poi = _poi.GetValue(manager);
            if (poi == null) return false;
            var system = _system.GetValue(poi);
            return _service.ShouldSuppress(site, _guid.GetValue(poi) as string,
                system == null ? null : _guid.GetValue(system) as string, UniqueSystemOf);
        }
        catch (Exception error)
        {
            if (!_reported) { _reported = true; try { _report(error); } catch { } }
            return false;
        }
    }

    private string? UniqueSystemOf(string anchor)
    {
        // The current player's map is the session boundary; no map means nothing to resolve against.
        if (_current.GetValue(null) is not { } map || _allPois.GetValue(map) is not IEnumerable points) return null;
        object? found = null;
        foreach (var poi in points)
        {
            if (poi == null || _guid.GetValue(poi) as string != anchor) continue;
            if (found != null) return null; // Ambiguous identities never suppress anything.
            found = poi;
        }
        var system = found == null ? null : _system.GetValue(found);
        return system == null ? null : _guid.GetValue(system) as string;
    }
}
