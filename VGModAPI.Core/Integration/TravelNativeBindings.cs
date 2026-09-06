using System;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class TravelNativeBindings
{
    private const string TravelType = "Behaviour.Managers.TravelManager";
    private const string PoiType = "Source.Galaxy.MapPointOfInterest";
    private const string SystemType = "Source.Galaxy.SystemMapData";
    private const string ManagerType = "Behaviour.Managers.BasePoiManager";
    private readonly FieldInfo _player, _currentSystem, _currentPoi, _system, _rawName;
    private readonly PropertyInfo _guid, _time, _localManager, _managerPoi, _ready, _target, _localTarget;
    internal TravelNativeBindings(Assembly assembly)
    {
        var player = assembly.GetType(BindingCatalog.Player, true)!;
        var element = assembly.GetType("Source.Galaxy.MapElement", true)!;
        var travel = assembly.GetType(TravelType, true)!;
        var manager = assembly.GetType(ManagerType, true)!;
        _player = Field(player, "current", BindingCatalog.Player, true);
        _currentSystem = Field(player, "currentSystem", SystemType);
        _currentPoi = Field(player, "currentPointOfInterest", PoiType);
        _time = Property(player, "elapsedTime", "System.Double");
        _system = Field(element, "system", SystemType);
        _guid = Property(element, "guid", "System.String");
        // MapElement.name lazily generates a name. Observation must not mutate it
        // or consume world-generation randomness merely to build a payload.
        _rawName = Field(element, "_name", "System.String");
        _localManager = Property(travel, "localPoiManager", ManagerType);
        _target = Property(travel, "targetPoi", PoiType);
        _localTarget = Property(travel, "localTarget", PoiType);
        _managerPoi = Property(manager, "poi", PoiType);
        _ready = Property(manager, "initializedAndReady", "System.Boolean");
    }
    internal object? Player => _player.GetValue(null);
    internal object? CurrentPoi(object player) => _currentPoi.GetValue(player);
    internal object? CurrentSystem(object player) => _currentSystem.GetValue(player);
    internal double Time(object player) => (double)_time.GetValue(player)!;
    internal object? Target(object manager) => _target.GetValue(manager);
    internal object? LocalTarget(object manager) => _localTarget.GetValue(manager);
    internal object? LocalManager(object manager) => _localManager.GetValue(manager);
    internal bool Ready(object localManager, object? actualPoi) => (bool)_ready.GetValue(localManager)! && ReferenceEquals(_managerPoi.GetValue(localManager), actualPoi);
    internal TravelLocation? CurrentLocation(object player) => Location(CurrentSystem(player), CurrentPoi(player));
    internal TravelLocation? Destination(object poi) => Location(_system.GetValue(poi), poi);
    private TravelLocation? Location(object? system, object? poi)
    {
        if (system == null || (poi != null && !ReferenceEquals(_system.GetValue(poi), system))) return null;
        return new TravelLocation((string)_guid.GetValue(system)!, poi == null ? null : (string?)_guid.GetValue(poi),
            (string?)_rawName.GetValue(system) ?? "", poi == null ? null : (string?)_rawName.GetValue(poi));
    }
    private static FieldInfo Field(Type type, string name, string expected, bool isStatic = false)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (field == null || field.FieldType.FullName != expected || field.IsStatic != isStatic) throw new MissingFieldException(type.FullName, name);
        return field;
    }
    private static PropertyInfo Property(Type type, string name, string expected)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (property == null || property.PropertyType.FullName != expected || property.GetMethod == null || property.GetMethod.IsStatic || property.GetIndexParameters().Length != 0)
            throw new MissingMemberException(type.FullName, name);
        return property;
    }
}
