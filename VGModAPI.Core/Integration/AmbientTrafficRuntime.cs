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
    /// <summary>Resolves (poiGuid, systemGuid) to "this is owned authored content". Null when the world
    /// content service is unavailable, which fails open to vanilla dressing.</summary>
    private readonly Func<string?, string?, bool>? _owned;
    private readonly Type _manager;
    private readonly PropertyInfo _poi, _guid, _current, _allPois, _allSystems;
    private readonly FieldInfo _system;
    private bool _reported;
    internal AmbientTrafficRuntime(Assembly assembly, AmbientTrafficService service, Action<Exception> report,
        Func<string?, string?, bool>? owned = null)
    {
        _service = service; _report = report; _owned = owned;
        _manager = assembly.GetType("Behaviour.Managers.BasePoiManager", true)!;
        _poi = Property(_manager, "poi");
        var element = assembly.GetType("Source.Galaxy.MapElement", true)!;
        _guid = Property(element, "guid");
        _system = element.GetField("system", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingFieldException(element.FullName, "system");
        var map = assembly.GetType("Source.Galaxy.GalaxyMapData", true)!;
        _current = Property(map, "current");
        _allPois = Property(map, "allPointsOfInterest");
        _allSystems = Property(map, "allSystems");
    }
    private static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
        ?? throw new MissingMemberException(type.FullName, name);

    internal bool SuppressStationVisitor(object? manager) => Suppress(AmbientSpawnSite.Station, manager);
    internal bool SuppressGateTraffic(object? manager) => Suppress(AmbientSpawnSite.JumpGate, manager);
    internal bool SuppressWormholeTraffic(object? manager) => Suppress(AmbientSpawnSite.Wormhole, manager);
    /// <summary>A quiet wormhole spawns no security patrol either; so does a whole quieted system.</summary>
    internal bool SuppressQuietWormholePatrol(object? manager)
    {
        try
        {
            if (manager == null || !_manager.IsInstanceOfType(manager)) return false;
            var poi = _poi.GetValue(manager);
            if (poi == null) return false;
            var system = _system.GetValue(poi);
            return _service.ShouldSuppressPatrol(_guid.GetValue(poi) as string,
                system == null ? null : _guid.GetValue(system) as string, UniqueSystemOf);
        }
        catch (Exception error)
        {
            if (!_reported) { _reported = true; try { _report(error); } catch { } }
            return false;
        }
    }

    /// <summary>
    /// True when the point of interest about to receive the game's first-visit window dressing belongs to
    /// owned authored content. An authored rift or gate is created empty on purpose, and "empty and never
    /// visited" is exactly the condition the game uses to add a gun platform, asteroids, cargo and a
    /// derelict ship. Owned doors stay exactly as authored; anything else keeps vanilla dressing.
    /// </summary>
    internal bool SuppressWindowDressing(object? poi)
    {
        try
        {
            if (poi == null || _owned == null) return false;
            var system = _system.GetValue(poi);
            return _owned(_guid.GetValue(poi) as string, system == null ? null : _guid.GetValue(system) as string);
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

    /// <summary>Resolves an anchor to the identity of the single system it belongs to. An anchor may be a
    /// point of interest's identity or a system's own identity (an authored cluster quiets whole systems),
    /// so a system GUID resolves directly. Ambiguous or missing anchors resolve to null and fail open.</summary>
    private string? UniqueSystemOf(string anchor)
    {
        if (_current.GetValue(null) is not { } map) return null;
        if (_allPois.GetValue(map) is IEnumerable points)
        {
            object? found = null;
            foreach (var poi in points)
            {
                if (poi == null || _guid.GetValue(poi) as string != anchor) continue;
                if (found != null) return null; // Ambiguous identities never suppress anything.
                found = poi;
            }
            if (found != null)
            {
                var system = _system.GetValue(found);
                return system == null ? null : _guid.GetValue(system) as string;
            }
        }
        if (_allSystems.GetValue(map) is not IEnumerable systems) return null;
        string? match = null;
        foreach (var system in systems)
        {
            if (system == null || _guid.GetValue(system) as string != anchor) continue;
            if (match != null) return null;
            match = anchor;
        }
        return match;
    }
}
