using System;
using System.Collections;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Core.Integration;

/// <summary>Core-facing seam so the encounter path is unit-testable without native types.</summary>
internal interface IEncounterNative
{
    /// <summary>Validates every input, then schedules all waves. Returns the scheduled unit count, or null as a typed refusal with a reason.</summary>
    (int Scheduled, string Detail)? Spawn(Guid session, string poiId, EncounterComposition composition);
}

/// <summary>
/// Installed reflection-backed encounter seam. Every wave routes through the game's own
/// AddTriggeredSpawnFromFixedPayload (the exact-count reinforcement path); hostility is applied
/// per spawned unit (playerHostile/noReputationLoss) and never to a faction's diplomacy.
/// </summary>
internal sealed class WorldNativeEncounters : IEncounterNative
{
    private readonly GameAdapter _game;
    private readonly WorldMapIndex _index;
    private readonly PropertyInfo _map;
    private readonly MethodInfo _addTriggered, _shipExists, _factionGet;
    private readonly FieldInfo _allFactions, _playerHostile, _noReputationLoss;
    private readonly Type _combatType, _rankType, _shipDataType;
    private readonly object _gameplayCombat;
    private readonly Action<Exception> _report;

    internal WorldNativeEncounters(GameAdapter game, Assembly assembly, Action<Exception>? report = null)
    {
        _game = game; _report = report ?? (_ => { });
        _index = new WorldMapIndex(assembly);
        Type Get(string name) => assembly.GetType(name, true)!;
        _map = Get("Source.Player.GamePlayer").GetProperty("map", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("GamePlayer.map");
        var poi = Get("Source.Galaxy.MapPointOfInterest");
        _addTriggered = poi.GetMethod("AddTriggeredSpawnFromFixedPayload", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException("AddTriggeredSpawnFromFixedPayload");
        // The loadout/rank enum types come from the method's own parameters; namespaces are not part of the contract.
        var parameters = _addTriggered.GetParameters();
        if (parameters.Length < 7 || parameters[0].ParameterType != typeof(float) || parameters[1].ParameterType != typeof(string)
            || parameters[2].ParameterType != typeof(int) || parameters[6].ParameterType != typeof(int?))
            throw new MissingMethodException("AddTriggeredSpawnFromFixedPayload shape changed.");
        _combatType = Nullable.GetUnderlyingType(parameters[4].ParameterType) ?? parameters[4].ParameterType;
        _rankType = parameters[5].ParameterType;
        _gameplayCombat = Enum.Parse(_combatType, "Combat");
        var ship = Get("Behaviour.Unit.SpaceShip");
        _shipExists = ship.GetMethod("SpaceShipExists", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException("SpaceShipExists");
        var faction = Get("Source.Galaxy.Faction");
        _allFactions = faction.GetField("allFactions", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingFieldException("allFactions");
        _factionGet = faction.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)
            ?? throw new MissingMethodException("Faction.Get");
        // Hostility is per spawned SHIP datum only (the consumer contract scopes it to ships), but the
        // flags themselves are declared on the unit-data base type.
        _shipDataType = Get("Source.SpaceShip.SpaceShipData");
        var unitData = Get("Source.Data.AbstractUnitData");
        _playerHostile = unitData.GetField("playerHostile", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingFieldException("playerHostile");
        _noReputationLoss = unitData.GetField("noReputationLoss", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingFieldException("noReputationLoss");
    }

    public (int Scheduled, string Detail)? Spawn(Guid session, string poiId, EncounterComposition composition)
    {
        try
        {
            if (!_game.TryGetObservedPlayer(session, out var player) || player == null) return null;
            var map = _map.GetValue(player);
            if (map == null) return null;
            var poi = _index.Read(map).FindPoint(poiId);
            if (poi == null) return (0, "The POI does not exist in the current galaxy.");
            // Validate everything before scheduling anything: a partial encounter is not the authored one.
            foreach (var wave in composition.Waves)
                if (_shipExists.Invoke(null, new object[] { wave.ShipClassId }) is not true)
                    return (0, "Unknown ship class: " + wave.ShipClassId);
            if (_allFactions.GetValue(null) is not IDictionary factions || !factions.Contains(composition.FactionId))
                return (0, "Unknown faction: " + composition.FactionId);
            var faction = _factionGet.Invoke(null, new object[] { composition.FactionId });
            object rank;
            try { rank = Enum.Parse(_rankType, composition.Rank.ToString()); }
            catch (ArgumentException) { return (0, "The installed game does not support rank " + composition.Rank + "."); }
            int scheduled = 0;
            foreach (var wave in composition.Waves)
            {
                var arguments = new object?[_addTriggered.GetParameters().Length];
                for (int i = 0; i < arguments.Length; i++) arguments[i] = _addTriggered.GetParameters()[i].HasDefaultValue ? _addTriggered.GetParameters()[i].DefaultValue : null;
                arguments[0] = wave.DelaySeconds; arguments[1] = wave.ShipClassId; arguments[2] = wave.Count;
                arguments[3] = faction; arguments[4] = _gameplayCombat; arguments[5] = rank; arguments[6] = (int?)composition.Level;
                var units = _addTriggered.Invoke(poi, arguments) as IList;
                if (units == null) return (scheduled, "The native reinforcement trigger refused a wave.");
                foreach (var unit in units)
                {
                    if (composition.HostileToPlayer && _shipDataType.IsInstanceOfType(unit))
                    {
                        _playerHostile.SetValue(unit, true);
                        if (composition.NoReputationLoss) _noReputationLoss.SetValue(unit, true);
                    }
                    scheduled++;
                }
            }
            return (scheduled, "");
        }
        catch (Exception error)
        {
            if (error is TargetInvocationException tie && tie.InnerException != null) _report(tie.InnerException); else _report(error);
            return null;
        }
    }
}
