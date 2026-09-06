using System;
using System.Linq;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class TravelNativeBindings
{
    private const string TravelType = "Behaviour.Managers.TravelManager";
    private const string PoiType = "Source.Galaxy.MapPointOfInterest";
    private const string SystemType = "Source.Galaxy.SystemMapData";
    private const string ManagerType = "Behaviour.Managers.BasePoiManager";
    private const string JumpGateType = "Source.Galaxy.POI.JumpGate";
    private const string DockingOptionType = "Behaviour.Spacestation.Docking.DockingOption";
    private const string SpaceShipType = "Behaviour.Unit.SpaceShip";
    private readonly FieldInfo _player, _currentSystem, _currentPoi, _system, _rawName, _waypoints, _dockingState, _interiorInstance, _travelManagerInstance, _jumpTargetSystemGuid, _jumpTargetPoiGuid;
    private readonly PropertyInfo _guid, _time, _localManager, _managerPoi, _ready, _target, _localTarget, _currentSpaceShip, _usingJumpgate, _interiorStation, _dockingShip, _shipData;
    private readonly MethodInfo _travelActive, _travelNextWaypoint;
    internal TravelNativeBindings(Assembly assembly)
    {
        var player = assembly.GetType(BindingCatalog.Player, true)!;
        var element = assembly.GetType("Source.Galaxy.MapElement", true)!;
        var poiType = assembly.GetType(PoiType, true)!;
        var travel = assembly.GetType(TravelType, true)!;
        // Behaviour.Util.Singleton<TravelManager>.instance is protected static; read the backing
        // field instead of the Instance property so polling never triggers scene FindAnyObjectByType.
        var singleton = assembly.GetType("Behaviour.Util.Singleton`1", true)!.MakeGenericType(travel);
        _travelManagerInstance = singleton.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            ?? throw new MissingFieldException(singleton.FullName, "instance");
        var manager = assembly.GetType(ManagerType, true)!;
        var shipData = assembly.GetType("Source.SpaceShip.SpaceShipData", true)!;
        var interior = assembly.GetType("Behaviour.UI.Spacestation.SpaceStationInterior", true)!;
        _interiorInstance = Field(interior, "instance", interior.FullName, true);
        _interiorStation = Property(interior, "spacestation", "Source.Galaxy.POI.SpaceStation");
        _player = Field(player, "current", BindingCatalog.Player, true);
        _currentSystem = Field(player, "currentSystem", SystemType);
        _currentPoi = Field(player, "currentPointOfInterest", PoiType);
        _waypoints = GenericListField(player, "waypoints", poiType);
        _time = Property(player, "elapsedTime", "System.Double");
        _currentSpaceShip = Property(player, "currentSpaceShip", "Source.SpaceShip.SpaceShipData");
        _dockingState = NullableField(shipData, "dockingState", assembly.GetType("Source.SpaceShip.Auto.DockingState", true)!);
        _system = Field(element, "system", SystemType);
        _guid = Property(element, "guid", "System.String");
        // MapElement.name lazily generates a name. Observation must not mutate it
        // or consume world-generation randomness merely to build a payload.
        _rawName = Field(element, "_name", "System.String");
        _localManager = Property(travel, "localPoiManager", ManagerType);
        _target = Property(travel, "targetPoi", PoiType);
        _localTarget = Property(travel, "localTarget", PoiType);
        _usingJumpgate = Property(travel, "usingJumpgate", "System.Boolean");
        _managerPoi = Property(manager, "poi", PoiType);
        _ready = Property(manager, "initializedAndReady", "System.Boolean");
        var jumpGate = assembly.GetType(JumpGateType, true)!;
        _jumpTargetSystemGuid = Field(jumpGate, "targetSystemGuid", "System.String");
        _jumpTargetPoiGuid = Field(jumpGate, "targetPoiGuid", "System.String");
        _travelActive = Method(travel, "TravelActive", "System.Boolean");
        _travelNextWaypoint = Method(travel, "TravelToNextWaypoint", "System.Void");
        var dockingOption = assembly.GetType(DockingOptionType, true)!;
        _dockingShip = Property(dockingOption, "dockingSpaceship", SpaceShipType);
        var spaceShip = assembly.GetType(SpaceShipType, true)!;
        _shipData = Property(spaceShip, "spaceShipData", "Source.SpaceShip.SpaceShipData");
    }
    internal object? Player => _player.GetValue(null);
    internal object? CurrentPoi(object player) => _currentPoi.GetValue(player);
    internal object? CurrentSystem(object player) => _currentSystem.GetValue(player);
    internal double Time(object player) => (double)_time.GetValue(player)!;
    internal object? Target(object manager) => _target.GetValue(manager);
    internal object? LocalTarget(object manager) => _localTarget.GetValue(manager);
    internal object? LocalManager(object manager) => _localManager.GetValue(manager);
    internal object? Poi(object manager) => _managerPoi.GetValue(manager);
    internal bool UsingJumpgate(object manager) => (bool)_usingJumpgate.GetValue(manager)!;
    internal bool TravelActive(object travelManager) => (bool)_travelActive.Invoke(travelManager, null)!;
    internal void NextWaypoint(object travelManager) => _travelNextWaypoint.Invoke(travelManager, null);
    // The ship object is captured before any dock reset so attribution survives
    // ResetDockingOption() nulling dockingOption.dockingSpaceship.
    internal object? ShipOf(object? dockingOption) => dockingOption == null ? null : _dockingShip.GetValue(dockingOption);
    internal object? ShipData(object ship) => ship == null ? null : _shipData.GetValue(ship);
    internal int? ShipDockingState(object ship)
    {
        var data = ShipData(ship);
        if (data == null) return null;
        var value = _dockingState.GetValue(data);
        return value == null ? (int?)null : Convert.ToInt32(value);
    }
    internal bool IsPlayerShip(object? ship, object player)
    {
        var data = ship == null ? null : _shipData.GetValue(ship);
        var playerData = _currentSpaceShip.GetValue(player);
        return data != null && playerData != null && ReferenceEquals(data, playerData);
    }
    // First waypoint (the next hop target); used for wormhole cross-system requested destination.
    internal object? Waypoint0(object player) => Waypoint(player, 0);
    private object? Waypoint(object player, int index)
    {
        if (_waypoints.GetValue(player) is System.Collections.IList list && list.Count > index) return list[index];
        return null;
    }
    internal bool InCurrentSystem(object poi, object player) => ReferenceEquals(_system.GetValue(poi), CurrentSystem(player));
    // Raw jump-gate target guids (plain public fields; no galaxy lookup, no lazy name/RNG).
    // Valid even when the tutorial/sandbox target is not present in the current world map.
    internal string? JumpSystemGuid(object? gate) => gate == null ? null : (string?)_jumpTargetSystemGuid.GetValue(gate);
    internal string? JumpPoiGuid(object? gate) => gate == null ? null : (string?)_jumpTargetPoiGuid.GetValue(gate);
    internal object? InteriorInstance() => _interiorInstance.GetValue(null);
    internal object? InteriorStation(object interior) => _interiorStation.GetValue(interior);
    // Live TravelManager singleton (null before gameplay; avoids the Instance property's scene query).
    internal object? TravelManager() => _travelManagerInstance.GetValue(null);
    internal int WaypointCount(object player) => ((System.Collections.ICollection)_waypoints.GetValue(player)!).Count;
    // SpaceShipData.dockingState is DockingState?; boxed enum values convert safely to int.
    internal int? DockingState(object player)
    {
        var data = _currentSpaceShip.GetValue(player);
        if (data == null) return null;
        var value = _dockingState.GetValue(data);
        return value == null ? (int?)null : Convert.ToInt32(value);
    }
    internal bool Ready(object localManager, object? actualPoi) => (bool)_ready.GetValue(localManager)! && ReferenceEquals(_managerPoi.GetValue(localManager), actualPoi);
    internal TravelLocation? CurrentLocation(object player) => Location(CurrentSystem(player), CurrentPoi(player));
    internal TravelLocation? Destination(object poi) => Location(_system.GetValue(poi), poi);
    private TravelLocation? Location(object? system, object? poi)
    {
        if (system == null || (poi != null && !ReferenceEquals(_system.GetValue(poi), system))) return null;
        return new TravelLocation((string)_guid.GetValue(system)!, poi == null ? null : (string?)_guid.GetValue(poi),
            (string?)_rawName.GetValue(system), poi == null ? null : (string?)_rawName.GetValue(poi));
    }
    private static FieldInfo NullableField(Type type, string name, Type underlying)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (field == null || field.IsStatic
            || !field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(System.Nullable<>)
            || !ReferenceEquals(field.FieldType.GetGenericArguments()[0], underlying))
            throw new MissingFieldException(type.FullName, name);
        return field;
    }
    private static MethodInfo Method(Type type, string name, string returnType, params string[] parameterTypes)
    {
        var matches = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == name && !m.IsStatic && NativeTypeName.Matches(m.ReturnType, returnType)
                && m.GetParameters().Select(p => NativeTypeName.Canonical(p.ParameterType)).SequenceEqual(parameterTypes, StringComparer.Ordinal));
        var result = matches.SingleOrDefault();
        if (result == null) throw new MissingMethodException(type.FullName, name);
        return result;
    }
    private static FieldInfo GenericListField(Type type, string name, Type elementType)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (field == null || field.IsStatic
            || !field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(System.Collections.Generic.List<>)
            || !ReferenceEquals(field.FieldType.GetGenericArguments()[0], elementType))
            throw new MissingFieldException(type.FullName, name);
        return field;
    }
    private static FieldInfo Field(Type type, string name, string expected, bool isStatic = false)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (field == null || !NativeTypeName.Matches(field.FieldType, expected) || field.IsStatic != isStatic) throw new MissingFieldException(type.FullName, name);
        return field;
    }
    private static PropertyInfo Property(Type type, string name, string expected)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (property == null || !NativeTypeName.Matches(property.PropertyType, expected) || property.GetMethod == null || property.GetMethod.IsStatic || property.GetIndexParameters().Length != 0)
            throw new MissingMemberException(type.FullName, name);
        return property;
    }
}
