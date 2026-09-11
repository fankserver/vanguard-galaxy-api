using System;
using System.Collections;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Core.Integration;

/// <summary>
/// Reflection seam that applies an authored guardian loadout (forced rarity + outgoing-damage
/// overclock) to the exact spawned unit-data instances the native reinforcement trigger materialises
/// from — the same data objects the existing encounter flags reach. Reads the live player level and
/// the game's own scaling helpers (GameMath.ApplyItemLevelCap/DamageMultiplier/maxLevel) at runtime.
/// Runtime-only: nothing written to saves. Every fault fails open to vanilla and is reported once.
/// </summary>
internal sealed class BossGuardianRuntime
{
    private const string OverclockSourceId = "vgmodapi.guardian.damageoverclock";

    private readonly Action<Exception> _report;
    private readonly Type _playerType, _shipDataType, _abstractUnitDataType, _gameMathType,
        _rarityType, _equipStatType, _statBoostType, _gameplayType;
    private readonly PropertyInfo _playerLevel, _maxLevel, _statBoosts;
    private readonly FieldInfo _shipRarity;
    private readonly PropertyInfo _sourceId;
    private readonly MethodInfo _setStatBoost, _loadDefaultEquipment, _applyItemLevelCap, _damageMultiplier;
    private readonly object _rarityAny, _combatLoadout, _damageStat;
    private bool _reported;

    internal BossGuardianRuntime(Assembly assembly, Action<Exception> report)
    {
        _report = report;
        _playerType = Get(assembly, "Source.Player.GamePlayer");
        _shipDataType = Get(assembly, "Source.SpaceShip.SpaceShipData");
        _abstractUnitDataType = Get(assembly, "Source.Data.AbstractUnitData");
        _gameMathType = Get(assembly, "Source.Util.GameMath");
        _rarityType = Get(assembly, "Source.Item.Rarity");
        _equipStatType = Get(assembly, "Source.Item.EquipStat");
        _statBoostType = Get(assembly, "Source.Item.StatBoostEntry");
        _gameplayType = Get(assembly, "Source.Util.GameplayType");

        _playerLevel = Prop(_playerType, "level");
        _maxLevel = Prop(_gameMathType, "maxLevel");
        _statBoosts = Prop(_abstractUnitDataType, "statBoosts");
        _shipRarity = Field(_shipDataType, "shipRarity");
        _sourceId = Prop(_statBoostType, "sourceId");

        _setStatBoost = Method(_abstractUnitDataType, "SetStatBoost",
            typeof(string), typeof(string), _equipStatType, typeof(float), typeof(float));
        _loadDefaultEquipment = Method(_shipDataType, "LoadDefaultEquipment",
            typeof(int), typeof(float), typeof(string), typeof(Nullable<>).MakeGenericType(_gameplayType), typeof(Nullable<>).MakeGenericType(_rarityType));
        _applyItemLevelCap = Method(_gameMathType, "ApplyItemLevelCap", typeof(int), typeof(bool));
        _damageMultiplier = Method(_gameMathType, "DamageMultiplier", typeof(float));

        _rarityAny = Enum.GetValues(_rarityType).GetValue(0)!;           // any valid member; replaced per call
        _combatLoadout = Enum.Parse(_gameplayType, "Combat");
        _damageStat = Enum.Parse(_equipStatType, "Damage");
    }

    private static Type Get(Assembly assembly, string name) => assembly.GetType(name, true)!;
    private static PropertyInfo Prop(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
        ?? throw new MissingMemberException(type.FullName, name);
    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.FullName, name);
    private static MethodInfo Method(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, parameters, null)
        ?? throw new MissingMethodException(type.FullName, name);

    /// <summary>Observed player level, or null when undecidable (caller treats null as no-scaling).</summary>
    internal int? ReadPlayerLevel(object? player)
    {
        try { return player != null && _playerType.IsInstanceOfType(player) ? (int)_playerLevel.GetValue(player)! : null; }
        catch (Exception error) { ReportOnce(error); return null; }
    }

    /// <summary>The game's level ceiling for this session, or null when undecidable.</summary>
    internal int? ReadMaxLevel()
    {
        try { return (int)_maxLevel.GetValue(null)!; }
        catch (Exception error) { ReportOnce(error); return null; }
    }

    /// <summary>True when this unit already carries the API's overclock (idempotent by source id).</summary>
    internal bool HasOverclock(object shipData)
    {
        try
        {
            if (_statBoosts.GetValue(shipData) is not IEnumerable boosts) return false;
            foreach (var entry in boosts)
                if (entry != null && _sourceId.GetValue(entry) as string == OverclockSourceId) return true;
            return false;
        }
        catch (Exception error) { ReportOnce(error); return false; }
    }

    /// <summary>
    /// Applies the authored loadout to one spawned unit-data instance. Fails open to vanilla on any fault.
    /// </summary>
    internal void ApplyLoadout(object shipData, int spawnLevel, int? playerLevel, AuthoredLoadout? loadout)
    {
        if (loadout == null || !_shipDataType.IsInstanceOfType(shipData)) return;
        try
        {
            if (loadout.Rarity is { } requested)
            {
                var rarity = Enum.Parse(_rarityType, requested.ToString());
                _shipRarity.SetValue(shipData, rarity);
                _loadDefaultEquipment.Invoke(shipData, new object?[] { spawnLevel, -1f, null, _combatLoadout, rarity });
            }
            if (loadout.RequestedDamageLevel is { } requestedLevel && !HasOverclock(shipData) && ReadMaxLevel() is { } max)
            {
                int clamped = Math.Min(requestedLevel, max);
                int achieved = (int)_applyItemLevelCap.Invoke(null, new object[] { spawnLevel, false })!;
                float ratio = (float)_damageMultiplier.Invoke(null, new object[] { (float)clamped })!
                            / (float)_damageMultiplier.Invoke(null, new object[] { (float)achieved })!;
                float boost = Math.Max(0f, ratio - 1f);
                if (boost > 0f)
                    _setStatBoost.Invoke(shipData, new object[] { OverclockSourceId, "API Combat Overclock", _damageStat, boost, 1f });
            }
        }
        catch (Exception error) { ReportOnce(error); }
    }

    private void ReportOnce(Exception error)
    {
        if (_reported) return;
        _reported = true;
        try { _report(error); } catch { }
    }
}
