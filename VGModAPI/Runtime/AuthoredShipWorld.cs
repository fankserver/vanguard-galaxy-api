using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using VGModAPI.Core;
using VGModAPI.Core.Integration;

namespace VGModAPI.Runtime;

/// <summary>
/// Installed native seam for moored authored ships. Spawning uses the game's own fixed-payload path
/// (CreateFixedPayload + AddUnit) so the datum persists in the station's unit list like any payload
/// ship; the API owns the persistent unit identity and converges to exactly one instance by that
/// identity. Mooring maintenance is idempotent per frame while the station is the current POI:
/// never boardable, no docking state, no auto-AI, snapped to the authored offset.
/// </summary>
internal sealed class AuthoredShipWorld : IAuthoredShipNative
{
    private readonly GameAdapter _game;
    private readonly WorldMapIndex _index;
    private readonly PropertyInfo _map, _dataGuid, _shipData, _unitData, _rigidbody, _current;
    private readonly PropertyInfo _callsign;
    private readonly FieldInfo _units, _positionData, _position, _customName, _dockingState, _commander,
        _noBoardable, _allFactions, _vectorX, _vectorY;
    private readonly MethodInfo _createPayload, _addUnit, _worldPosition, _shipExists, _factionGet, _setName, _setActions;
    private readonly Type _vector, _shipType, _unitDataType;
    private readonly object _gameplayCargo, _rankStandard;
    private readonly Action<Exception> _report;

    internal AuthoredShipWorld(GameAdapter game, Assembly assembly, Action<Exception>? report = null)
    {
        _game = game; _report = report ?? (_ => { });
        _index = new WorldMapIndex(assembly);
        Type Get(string name) => assembly.GetType(name, true)!;
        var player = Get("Source.Player.GamePlayer");
        _map = player.GetProperty("map", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("GamePlayer.map");
        var poi = Get("Source.Galaxy.MapPointOfInterest");
        _current = poi.GetProperty("current", BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMemberException("MapPointOfInterest.current");
        _units = Field(poi, "units");
        _createPayload = Method(poi, "CreateFixedPayload");
        _addUnit = Method(poi, "AddUnit");
        _worldPosition = poi.GetMethod("GetWorldPosition", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException("GetWorldPosition");
        _unitDataType = Get("Source.Data.AbstractUnitData");
        _dataGuid = _unitDataType.GetProperty("guid", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("AbstractUnitData.guid");
        _positionData = Field(_unitDataType, "positionData");
        _position = Field(Get("Source.Data.UnitPositionData"), "position");
        var shipData = Get("Source.SpaceShip.SpaceShipData");
        _customName = Field(shipData, "customShipName");
        _dockingState = Field(shipData, "dockingState");
        _commander = Field(shipData, "commanderData");
        // Derive the commander type from the field itself; its namespace is not part of the contract.
        var captain = _commander.FieldType;
        _callsign = captain.GetProperty("callsign", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("callsign");
        _setName = captain.GetMethod("SetName", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(string), typeof(string) }, null)
            ?? throw new MissingMethodException("CaptainData.SetName");
        _shipType = Get("Behaviour.Unit.SpaceShip");
        _noBoardable = Field(_shipType, "noBoardable");
        _shipData = _shipType.GetProperty("spaceShipData", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("spaceShipData");
        _setActions = Method(_shipType, "SetTemporaryActions");
        _shipExists = _shipType.GetMethod("SpaceShipExists", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException("SpaceShipExists");
        var unit = Get("Behaviour.Unit.AbstractUnit");
        _unitData = unit.GetProperty("unitData", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("unitData");
        _rigidbody = unit.GetProperty("rigidbody", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMemberException("rigidbody");
        var faction = Get("Source.Galaxy.Faction");
        _allFactions = faction.GetField("allFactions", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingFieldException("allFactions");
        _factionGet = faction.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException("Faction.Get");
        _vector = Get("UnityEngine.Vector2");
        _vectorX = Field(_vector, "x"); _vectorY = Field(_vector, "y");
        // Enum types are derived from CreateFixedPayload's own parameters; namespaces are not part of the contract.
        var parameters = _createPayload.GetParameters();
        var gameplayType = Nullable.GetUnderlyingType(parameters[3].ParameterType) ?? parameters[3].ParameterType;
        _gameplayCargo = Enum.Parse(gameplayType, "Cargo");
        _rankStandard = Enum.Parse(parameters[4].ParameterType, "Standard");
    }
    private static FieldInfo Field(Type type, string name)
        => type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(type.FullName, name);
    private static MethodInfo Method(Type type, string name)
        => type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMethodException(type.FullName, name);

    private object? Station(Guid session, string stationPoiId)
    {
        if (!_game.TryGetObservedPlayer(session, out var player) || player == null) return null;
        var map = _map.GetValue(player);
        if (map == null) return null;
        return _index.Read(map).FindPoint(stationPoiId);
    }

    public string? CreateShip(Guid session, string stationPoiId, AuthoredShipDeclaration declaration)
    {
        try
        {
            var station = Station(session, stationPoiId);
            if (station == null) return null;
            if (_shipExists.Invoke(null, new object[] { declaration.ShipClassId }) is not true) return null;
            // Existing factions only: Faction.Get would silently create an unknown identity.
            if (_allFactions.GetValue(null) is not IDictionary factions || !factions.Contains(declaration.FactionId)) return null;
            var faction = _factionGet.Invoke(null, new object[] { declaration.FactionId });
            if (faction == null) return null;
            var list = _createPayload.Invoke(station, new object?[] { declaration.ShipClassId, 1, faction, _gameplayCargo, _rankStandard, null, null, false }) as IList;
            if (list == null || list.Count == 0 || list[0] == null) return null;
            var data = list[0]!;
            var positionData = _positionData.GetValue(data);
            if (positionData == null) return null;
            _position.SetValue(positionData, Offset(station, declaration));
            ApplyIdentity(data, declaration);
            _addUnit.Invoke(station, new object?[] { data, null, false });
            var unitId = _dataGuid.GetValue(data) as string;
            return string.IsNullOrEmpty(unitId) ? null : unitId;
        }
        catch (Exception error) { Report(error); return null; }
    }

    public bool ResolveShip(Guid session, string stationPoiId, string unitId)
    {
        var station = Station(session, stationPoiId);
        if (station == null) return false;
        if (_units.GetValue(station) is not IList units) return false;
        foreach (var data in units)
            if (data != null && _dataGuid.GetValue(data) as string == unitId) return true;
        return false;
    }

    public void Maintain(Guid session, string stationPoiId, string unitId, AuthoredShipDeclaration declaration)
    {
        try
        {
            var station = Station(session, stationPoiId);
            if (station == null || !ReferenceEquals(_current.GetValue(null), station)) return;
            // Persisted side: identity, name and moored position converge on the datum itself.
            object? datum = null;
            if (_units.GetValue(station) is IList units)
                foreach (var candidate in units)
                    if (candidate != null && _dataGuid.GetValue(candidate) as string == unitId) { datum = candidate; break; }
            if (datum == null) return;
            var target = Offset(station, declaration);
            ApplyIdentity(datum, declaration);
            _dockingState.SetValue(datum, null);
            // Live side: the materialised instance, matched by exact persistent identity.
            foreach (var found in UnityEngine.Object.FindObjectsByType(_shipType))
            {
                var unitData = _unitData.GetValue(found);
                if (unitData == null || _dataGuid.GetValue(unitData) as string != unitId) continue;
                _noBoardable.SetValue(found, true);
                _setActions.Invoke(found, new object?[] { null });
                var rigidbody = _rigidbody.GetValue(found);
                if (rigidbody != null)
                {
                    // Rigidbody2D lives in the Physics2D module; keep the seam reflection-only.
                    var positionProperty = rigidbody.GetType().GetProperty("position", BindingFlags.Public | BindingFlags.Instance);
                    var velocityProperty = rigidbody.GetType().GetProperty("linearVelocity", BindingFlags.Public | BindingFlags.Instance);
                    if (positionProperty != null && velocityProperty != null)
                    {
                        var vector = new Vector2((float)_vectorX.GetValue(target)!, (float)_vectorY.GetValue(target)!);
                        var currentPosition = (Vector2)positionProperty.GetValue(rigidbody)!;
                        if (Vector2.Distance(currentPosition, vector) > 2f) positionProperty.SetValue(rigidbody, vector);
                        velocityProperty.SetValue(rigidbody, Vector2.zero);
                        var positionData = _positionData.GetValue(unitData);
                        if (positionData != null) _position.SetValue(positionData, target);
                    }
                }
                break;
            }
        }
        catch (Exception error) { Report(error); }
    }

    private void ApplyIdentity(object shipData, AuthoredShipDeclaration declaration)
    {
        // Display name + commander callsign only; the ship class identity is never renamed.
        if (_customName.GetValue(shipData) as string != declaration.Name) _customName.SetValue(shipData, declaration.Name);
        var commander = _commander.GetValue(shipData);
        if (commander != null && _callsign.GetValue(commander) as string != declaration.Name)
            _setName.Invoke(commander, new object[] { "", declaration.Name, "" });
    }
    private object Offset(object station, AuthoredShipDeclaration declaration)
    {
        var world = _worldPosition.Invoke(station, null)!;
        var result = Activator.CreateInstance(_vector)!;
        _vectorX.SetValue(result, (float)_vectorX.GetValue(world)! + declaration.OffsetX);
        _vectorY.SetValue(result, (float)_vectorY.GetValue(world)! + declaration.OffsetY);
        return result;
    }
    private void Report(Exception error)
    {
        if (error is TargetInvocationException tie && tie.InnerException != null) _report(tie.InnerException);
        else _report(error);
    }
}
