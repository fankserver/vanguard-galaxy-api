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
    private readonly TooltipService _tooltips;
    private readonly SkillTreeRuntime _skills;
    private readonly Action<Exception> _report;
    private readonly Type _item;
    private readonly FieldInfo _beams, _automatic, _manual, _stats, _filtered;
    private readonly PropertyInfo _parent, _tractored, _badgeTree;
    private readonly MethodInfo _isPlayer, _busy, _eligible, _blocked, _addStat, _addText;
    private readonly FieldInfo _green, _details;
    internal MethodInfo AvailableBeam { get; }
    internal MethodInfo UpdateTargets { get; }
    internal MethodInfo ModuleStats { get; }
    internal MethodInfo MasteryTooltip { get; }

    internal TractorBeamRuntime(Assembly a, EquipmentService equipmentService, TooltipService tooltips, SkillTreeRuntime skills, Action<Exception> report)
    {
        _equipment = equipmentService; _tooltips = tooltips; _skills = skills; _report = report;
        var module = a.GetType("Behaviour.Equipment.Module.TractorModule", true)!;
        _item = a.GetType("Behaviour.Tractoring.TractorableItem", true)!;
        var equipment = a.GetType("Behaviour.Equipment.AbstractEquipment", true)!;
        var beam = a.GetType("Behaviour.Tractoring.TractorBeam", true)!;
        var badge = a.GetType("Behaviour.UI.MasteryBadge", true)!;
        var tooltip = a.GetType("Behaviour.UI.UITooltip", true)!;
        _beams = Field(module, "tractorBeams"); _automatic = Field(module, "amountOfBeams"); _manual = Field(module, "amountOfBonusBeams");
        _stats = Field(equipment, "mainSubStats"); _parent = Property(equipment, "parent"); _filtered = Field(module, "filteredTargets");
        _tractored = Property(_item, "isTractored"); _badgeTree = Property(badge, "skillTree");
        _isPlayer = Method(equipment, "IsPlayer", typeof(bool)); _busy = Method(beam, "HasTarget");
        _eligible = Method(_item, "CanBeAutoTractoredBy", _parent.PropertyType);
        _blocked = Method(module, "IsCrewPodTargetingBlocked", _item);
        _addStat = Method(_stats.FieldType, "AddMainSubStat", typeof(string), typeof(string));
        _addText = Method(tooltip, "AddTextLine", typeof(string), typeof(int), typeof(float));
        var colors = a.GetType("Source.Util.ColorHelper", true)!;
        _green = Field(colors, "greenish"); _details = Field(colors, "detailsColor");
        AvailableBeam = Method(module, "GetAvailableTractorBeam", typeof(bool));
        var targets = a.GetType("Behaviour.Weapons.TargetableUnit", true)!;
        UpdateTargets = Method(module, "UpdateAvailableTargets", typeof(System.Collections.Generic.IEnumerable<>).MakeGenericType(targets));
        ModuleStats = Method(module, "SetMainSubStats"); MasteryTooltip = Method(badge, "AddTooltipCustomContent", tooltip);
        if (AvailableBeam.ReturnType != beam || UpdateTargets.ReturnType != typeof(void) || ModuleStats.ReturnType != typeof(void) || MasteryTooltip.ReturnType != typeof(void)
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
    internal void AddModuleDescription(object module)
    {
        if (!MainThread || !_tooltips.Availability.IsAvailable) return;
        try
        {
            foreach (var line in _tooltips.Describe(ReadModule(module)))
                _addStat.Invoke(_stats.GetValue(module), new object[] { string.Concat(line.Spans.Select(span => span.Text)), "" });
        }
        catch (Exception error) { Report(error); }
    }
    internal void AddMasteryDescription(object badge, object tooltip)
    {
        if (!MainThread || !_tooltips.Availability.IsAvailable) return;
        try
        {
            var nativeTree = _badgeTree.GetValue(badge);
            var tree = nativeTree == null ? null : _skills.Read(nativeTree);
            if (tree == null) return;
            foreach (var line in _tooltips.Describe(tree))
                _addText.Invoke(tooltip, new object[] { string.Concat(line.Spans.Select(span => Format(span.Text, span.Style))), 12, 8f });
        }
        catch (Exception error) { Report(error); }
    }
    private string Format(string text, TooltipTextStyle style)
    {
        if (style == TooltipTextStyle.Normal) return text;
        var color = style == TooltipTextStyle.Bonus ? _green : _details;
        return "<color=#" + ColorUtility.ToHtmlStringRGBA((Color)color.GetValue(null)!) + ">" + text + "</color>";
    }
    private void Report(Exception error) { try { _report(error); } catch { } }
}
