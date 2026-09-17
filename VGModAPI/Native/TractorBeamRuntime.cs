using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class TractorBeamRuntime
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly EquipmentService _equipment;
    private readonly Action<Exception> _report;
    private readonly Type _item;
    private readonly FieldInfo _beams, _automatic, _manual, _filtered;
    private readonly PropertyInfo _parent, _tractored;
    private readonly MethodInfo _isPlayer, _busy, _eligible, _blocked;
    internal MethodInfo AvailableBeam { get; }
    internal MethodInfo UpdateTargets { get; }

    internal TractorBeamRuntime(Assembly a, EquipmentService equipmentService, Action<Exception> report)
    {
        _equipment = equipmentService; _report = report;
        var module = a.GetType("Behaviour.Equipment.Module.TractorModule", true)!;
        _item = a.GetType("Behaviour.Tractoring.TractorableItem", true)!;
        var equipment = a.GetType("Behaviour.Equipment.AbstractEquipment", true)!;
        var beam = a.GetType("Behaviour.Tractoring.TractorBeam", true)!;
        _beams = Field(module, "tractorBeams"); _automatic = Field(module, "amountOfBeams"); _manual = Field(module, "amountOfBonusBeams");
        _parent = Property(equipment, "parent"); _filtered = Field(module, "filteredTargets");
        _tractored = Property(_item, "isTractored");
        _isPlayer = Method(equipment, "IsPlayer", typeof(bool)); _busy = Method(beam, "HasTarget");
        _eligible = Method(_item, "CanBeAutoTractoredBy", _parent.PropertyType);
        _blocked = Method(module, "IsCrewPodTargetingBlocked", _item);
        AvailableBeam = Method(module, "GetAvailableTractorBeam", typeof(bool));
        var targets = a.GetType("Behaviour.Weapons.TargetableUnit", true)!;
        UpdateTargets = Method(module, "UpdateAvailableTargets", typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(targets));
        if (AvailableBeam.ReturnType != beam || UpdateTargets.ReturnType != typeof(void)
            || _isPlayer.ReturnType != typeof(bool) || _busy.ReturnType != typeof(bool) || _eligible.ReturnType != typeof(bool) || _blocked.ReturnType != typeof(bool)
            || _automatic.FieldType != typeof(int) || _manual.FieldType != typeof(int) || _tractored.PropertyType != typeof(bool))
            throw new InvalidOperationException("Tractor binding shape changed.");
    }
    private static MethodInfo Method(Type type, string name, params Type[] parameters)
        => type.GetMethod(name, Any, null, parameters, null) ?? throw new MissingMethodException(type.FullName, name);
    private static FieldInfo Field(Type type, string name)
    {
        for (Type? current = type; current != null; current = current.BaseType)
            if (current.GetField(name, Any | BindingFlags.DeclaredOnly) is FieldInfo field) return field;
        throw new MissingFieldException(type.FullName, name);
    }
    private static PropertyInfo Property(Type type, string name)
        => type.GetProperty(name, Any) ?? throw new MissingMemberException(type.FullName, name);
    private TractorModule ReadModule(object module) => new((int)_automatic.GetValue(module)!, (int)_manual.GetValue(module)!);
    private int BusyBeams(object module)
    {
        int busy = 0;
        foreach (var beam in (IEnumerable)_beams.GetValue(module)!) if ((bool)_busy.Invoke(beam, null)!) busy++;
        return busy;
    }
    private bool MainThread => Thread.CurrentThread.ManagedThreadId == _thread;
    internal object? Borrow(object module, bool manual, object? original)
    {
        if (original != null || !MainThread || !_equipment.Availability.IsAvailable) return original;
        try
        {
            if (_isPlayer.Invoke(module, new object[] { true }) is not true) return original;
            var values = ReadModule(module); var targeting = _equipment.Resolve(values);
            if (targeting == null || !EquipmentService.MayBorrow(values, targeting, BusyBeams(module), manual)) return original;
            foreach (var beam in (IEnumerable)_beams.GetValue(module)!) if (_busy.Invoke(beam, null) is false) return beam;
        }
        catch (Exception error) { Report(error); }
        return original;
    }
    internal void TopUp(object module, IEnumerable targets)
    {
        if (!MainThread || !_equipment.Availability.IsAvailable) return;
        try
        {
            if (_isPlayer.Invoke(module, new object[] { true }) is not true) return;
            var values = ReadModule(module); var targeting = _equipment.Resolve(values);
            if (targeting == null) return;
            int cap = EquipmentService.AutomaticCapacity(values, targeting);
            var filtered = (IList)_filtered.GetValue(module)!;
            var parent = _parent.GetValue(module);
            foreach (var target in targets)
            {
                if (filtered.Count >= cap) break;
                if (target is not UnityEngine.Object native || native == null || !_item.IsInstanceOfType(target) || filtered.Contains(target)) continue;
                if (_tractored.GetValue(target) is false && _eligible.Invoke(target, new[] { parent }) is true && _blocked.Invoke(module, new[] { target }) is false)
                    filtered.Add(target);
            }
        }
        catch (Exception error) { Report(error); }
    }
    private void Report(Exception error) { try { _report(error); } catch { } }
}
