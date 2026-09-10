using System;
using System.Collections;
using System.Reflection;

namespace VGModAPI.Core.Integration;

/// <summary>
/// Brackets one native damage call on a protected unit. The unit's recorded condition is captured
/// before vanilla damage runs and restored afterwards, and the native invincibility clamp is held
/// only for the duration of the call, so no runtime flag or data change outlives the hit.
/// Every fault fails open: vanilla damage proceeds untouched and is reported once.
/// </summary>
internal sealed class UnitProtectionRuntime
{
    private readonly UnitProtectionService _service;
    private readonly Action<Exception> _report;
    private readonly Type _unit;
    private readonly PropertyInfo _unitData, _guid, _destroyed;
    private readonly FieldInfo _invincible, _hull, _armor, _shield, _emp, _battleDamage;
    private bool _reported;

    internal UnitProtectionRuntime(Assembly assembly, UnitProtectionService service, Action<Exception> report)
    {
        _service = service; _report = report;
        _unit = assembly.GetType("Behaviour.Unit.AbstractUnit", true)!;
        _unitData = Property(_unit, "unitData");
        var targetable = assembly.GetType("Behaviour.Weapons.TargetableUnit", true)!;
        _invincible = Field(targetable, "isInvincible");
        _destroyed = Property(targetable, "isDestroyed");
        if (_destroyed.SetMethod == null) throw new MissingMemberException(targetable.FullName, "isDestroyed setter");
        var data = assembly.GetType("Source.Data.AbstractUnitData", true)!;
        _guid = Property(data, "guid");
        _hull = Field(data, "currentHullHP"); _armor = Field(data, "currentArmorHP");
        _shield = Field(data, "currentShieldHP"); _emp = Field(data, "empCharge");
        _battleDamage = Field(data, "battleDamage");
    }
    private static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMemberException(type.FullName, name);
    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new MissingFieldException(type.FullName, name);

    internal sealed class Condition
    {
        internal object Unit = null!, Data = null!;
        internal bool Invincible, Destroyed;
        internal float Hull, Armor, Shield, Emp;
        internal int BattleDamageCount;
    }

    /// <summary>Null means unprotected or undecidable; vanilla damage proceeds either way.</summary>
    internal Condition? Enter(object? unit)
    {
        try
        {
            if (unit == null || !_unit.IsInstanceOfType(unit)) return null;
            if (_unitData.GetValue(unit) is not { } data || !_service.IsProtected(_guid.GetValue(data) as string)) return null;
            var condition = new Condition
            {
                Unit = unit,
                Data = data,
                Invincible = (bool)_invincible.GetValue(unit)!,
                Destroyed = (bool)_destroyed.GetValue(unit)!,
                Hull = (float)_hull.GetValue(data)!,
                Armor = (float)_armor.GetValue(data)!,
                Shield = (float)_shield.GetValue(data)!,
                Emp = (float)_emp.GetValue(data)!,
                BattleDamageCount = (_battleDamage.GetValue(data) as IList)?.Count ?? 0
            };
            // The native clamp keeps the hull from reaching a lethal zero during this call.
            _invincible.SetValue(unit, true);
            return condition;
        }
        catch (Exception error) { ReportOnce(error); return null; }
    }

    internal void Exit(Condition? condition)
    {
        if (condition == null) return;
        try
        {
            _invincible.SetValue(condition.Unit, condition.Invincible);
            if (!condition.Destroyed && (bool)_destroyed.GetValue(condition.Unit)!)
                _destroyed.SetValue(condition.Unit, false);
            _hull.SetValue(condition.Data, condition.Hull);
            _armor.SetValue(condition.Data, condition.Armor);
            _shield.SetValue(condition.Data, condition.Shield);
            _emp.SetValue(condition.Data, condition.Emp);
            if (_battleDamage.GetValue(condition.Data) is IList marks)
                while (marks.Count > condition.BattleDamageCount) marks.RemoveAt(marks.Count - 1);
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
