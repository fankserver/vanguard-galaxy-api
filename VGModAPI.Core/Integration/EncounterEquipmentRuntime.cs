using System;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Core.Integration;

/// <summary>
/// Reflection seam that applies an authored <see cref="EncounterEquipmentOverride"/> (forced rarity +
/// outgoing-damage overclock) to the exact spawned unit-data instances the native reinforcement
/// trigger materialises. Reads the live player level and the game's own scaling helpers
/// (<c>GameMath.ApplyItemLevelCap</c>/<c>DamageMultiplier</c>/<c>maxLevel</c>) at runtime. Only the
/// spawned unit's transient data is mutated at materialisation: nothing is written to saves. Every
/// fault fails open to vanilla and is reported once.
/// <para>
/// Every native member bound here is declared in <see cref="EncounterEquipmentBindings"/> and shape-
/// checked by the InstalledGame Cecil tests (run by <c>make check-bindings</c>); a game reshape fails
/// those tests before this seam can silently drift.
/// </para>
/// </summary>
internal sealed class EncounterEquipmentRuntime
{
    private const string OverclockSourceId = "vgmodapi.encounter.damageoverclock";

    private readonly Action<Exception> _report;
    private readonly PropertyInfo _playerLevel, _maxLevel;
    private readonly FieldInfo _shipRarity;
    private readonly MethodInfo _setStatBoost, _loadDefaultEquipment, _applyItemLevelCap, _damageMultiplier;
    private readonly Type _rarityType, _equipStatType, _gameplayType;
    private readonly object _combatLoadout, _damageStat;
    private bool _reported;

    internal EncounterEquipmentRuntime(Assembly assembly, Action<Exception> report)
    {
        _report = report;
        Type Get(string name) => assembly.GetType(name, true)!;

        // The catalog validates member shapes and resolves the (full-arity) methods; any shape change
        // throws here, which the caller isolates so it only degrades this optional feature.
        var resolved = EncounterEquipmentBindings.Validate(assembly);
        _setStatBoost = resolved["setStatBoost"];
        _loadDefaultEquipment = resolved["loadDefaultEquipment"];
        _applyItemLevelCap = resolved["applyItemLevelCap"];
        _damageMultiplier = resolved["damageMultiplier"];

        var playerType = Get(EncounterEquipmentBindings.Player);
        var gameMathType = Get(EncounterEquipmentBindings.GameMath);
        var shipDataType = Get(EncounterEquipmentBindings.ShipData);
        _playerLevel = Prop(playerType, "level");
        _maxLevel = Prop(gameMathType, "maxLevel");
        _shipRarity = Field(shipDataType, "shipRarity");

        _rarityType = Get("Source.Item.Rarity");
        _equipStatType = Get("Source.Item.EquipStat");
        _gameplayType = Get("Source.Util.GameplayType");
        _combatLoadout = Enum.Parse(_gameplayType, "Combat");
        _damageStat = Enum.Parse(_equipStatType, "Damage");
    }

    private static PropertyInfo Prop(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
        ?? throw new MissingMemberException(type.FullName, name);
    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.FullName, name);

    /// <summary>Observed player level, or null when undecidable (caller treats null as no-scaling).</summary>
    internal int? ReadPlayerLevel(object? player)
    {
        try { return player != null && _playerLevel.DeclaringType!.IsInstanceOfType(player) ? (int)_playerLevel.GetValue(player)! : null; }
        catch (Exception error) { ReportOnce(error); return null; }
    }

    /// <summary>The game's level ceiling for this session, or null when undecidable.</summary>
    internal int? ReadMaxLevel()
    {
        try { return (int)_maxLevel.GetValue(null)!; }
        catch (Exception error) { ReportOnce(error); return null; }
    }

    /// <summary>
    /// Applies the authored equipment override to one spawned unit-data instance. Fails open to
    /// vanilla on any fault. Idempotent for the overclock: SetStatBoost removes an existing boost
    /// under the same source id before adding it.
    /// </summary>
    internal void Apply(object shipData, int spawnLevel, EncounterEquipmentOverride? equipment)
    {
        if (equipment == null || !_shipRarity.DeclaringType!.IsInstanceOfType(shipData)) return;
        try
        {
            if (equipment.OverrideRarity is { } requested)
            {
                var rarity = Enum.Parse(_rarityType, requested.ToString());
                _shipRarity.SetValue(shipData, rarity);
                int targetLevel = spawnLevel;
                if (equipment.OverrideDamageLevel is { } raised && ReadMaxLevel() is { } max)
                    targetLevel = Math.Min(raised, max);
                // All eight declared parameters are passed: Invoke never fills optional ones.
                _loadDefaultEquipment.Invoke(shipData, new object?[]
                {
                    targetLevel, -1f, null, _combatLoadout, rarity, null, false, null
                });
            }
            if (equipment.OverrideDamageLevel is { } requestedLevel && ReadMaxLevel() is { } maxLevel)
            {
                int clamped = Math.Min(requestedLevel, maxLevel);
                int achieved = (int)_applyItemLevelCap.Invoke(null, new object[] { spawnLevel, false })!;
                float ratio = (float)_damageMultiplier.Invoke(null, new object[] { (float)clamped })!
                            / (float)_damageMultiplier.Invoke(null, new object[] { (float)achieved })!;
                float boost = Math.Max(0f, ratio - 1f);
                if (boost > 0f)
                    _setStatBoost.Invoke(shipData, new object?[]
                    {
                        OverclockSourceId, "API Combat Overclock", _damageStat, boost, 1f,
                        1, int.MaxValue, null, false
                    });
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
