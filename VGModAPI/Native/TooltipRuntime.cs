using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Binds the native tooltip populate paths (module stat builders, the mastery badge and
/// the shared tooltip content fill) and dispatches them into the consumer-facing TooltipService.</summary>
internal sealed class TooltipRuntime
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly string[] RequiredModuleTypes =
        { "TractorModule", "MiningModule", "SalvageModule", "ShieldGeneratorModule", "DroneBayModule" };
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly TooltipService _tooltips;
    private readonly SkillTreeRuntime _skills;
    private readonly Action<Exception> _report;
    private readonly FieldInfo _stats;
    private readonly PropertyInfo _quality;
    private readonly PropertyInfo _badgeTree, _subStats, _subName, _subAmount, _source, _sourceItem, _sourceCount;
    private readonly PropertyInfo _itemId, _itemName, _itemDescription, _itemLevel;
    private readonly MethodInfo _getName, _addStat, _addText;
    private readonly Type _itemSourceType;
    private readonly Dictionary<Type, ShipModuleKind> _kinds = new();
    private readonly FieldInfo? _tractorAutomatic, _tractorManual;
    private readonly Type _tractorModuleType;
    private readonly Color _green, _details;
    internal MethodInfo MasteryTooltip { get; }
    internal IReadOnlyList<MethodInfo> ModuleStatBuilders { get; }
    internal IReadOnlyList<MethodInfo> ContentFills { get; }

    internal TooltipRuntime(Assembly a, TooltipService tooltips, SkillTreeRuntime skills, Action<Exception> report)
    {
        _tooltips = tooltips; _skills = skills; _report = report;
        var equipment = a.GetType("Behaviour.Equipment.AbstractEquipment", true)!;
        var mainSubStats = a.GetType("Behaviour.Equipment.MainSubStats", true)!;
        var subStat = a.GetType("Behaviour.Equipment.SubStat", true)!;
        var badge = a.GetType("Behaviour.UI.MasteryBadge", true)!;
        var tooltip = a.GetType("Behaviour.UI.UITooltip", true)!;
        var tooltipSource = a.GetType("Behaviour.UI.Tooltip.TooltipSource", true)!;
        _itemSourceType = a.GetType("Behaviour.UI.Tooltip.ItemTooltipSource", true)!;
        var itemType = a.GetType("Behaviour.Item.InventoryItemType", true)!;
        if (!_itemSourceType.IsSubclassOf(tooltipSource)) throw new InvalidOperationException("Item tooltip source is no longer a tooltip source.");
        _stats = Field(equipment, "mainSubStats", mainSubStats); _quality = Property(equipment, "qualityLevel", typeof(int));
        _subStats = Property(mainSubStats, "subStatsList", typeof(IList), assignable: true);
        _subName = Property(subStat, "mainSubStatName", typeof(string)); _subAmount = Property(subStat, "mainSubStatAmount", typeof(string));
        _badgeTree = Property(badge, "skillTree");
        _source = Property(tooltip, "Source", tooltipSource);
        _sourceItem = Property(_itemSourceType, "item", itemType); _sourceCount = Property(_itemSourceType, "count", typeof(int));
        _itemId = Property(itemType, "identifier", typeof(string)); _itemName = Property(itemType, "displayName", typeof(string));
        _itemDescription = Property(itemType, "description", typeof(string)); _itemLevel = Property(itemType, "itemLevel", typeof(int));
        _getName = Method(equipment, "GetName", Type.EmptyTypes);
        _addStat = Method(mainSubStats, "AddMainSubStat", typeof(string), typeof(string));
        _addText = Method(tooltip, "AddTextLine", typeof(string), typeof(int), typeof(float));
        var colors = a.GetType("Source.Util.ColorHelper", true)!;
        _green = (Color)Field(colors, "greenish", typeof(Color)).GetValue(null)!;
        _details = (Color)Field(colors, "detailsColor", typeof(Color)).GetValue(null)!;
        MasteryTooltip = Method(badge, "AddTooltipCustomContent", tooltip);
        if (MasteryTooltip.ReturnType != typeof(void) || _addStat.ReturnType != typeof(void) || _getName.ReturnType != typeof(string))
            throw new InvalidOperationException("Tooltip binding shape changed.");

        // One postfix per concrete stat builder; the native dispatch resolves to the nearest
        // declaring type, so patching every declaring override covers the whole equipment family.
        var builders = new List<MethodInfo>();
        var seen = new HashSet<Type>();
        // Abstract intermediate classes may hold the real implementation (AbstractTurret), so
        // patch every declaring override with a body; virtual dispatch reaches it from the leaves.
        foreach (var type in Subclasses(a, equipment))
        {
            var overrideMethod = type.GetMethod("SetMainSubStats", Any | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (overrideMethod == null || overrideMethod.DeclaringType != type || overrideMethod.IsAbstract
                || overrideMethod.ReturnType != typeof(void) || !seen.Add(type)) continue;
            builders.Add(overrideMethod);
            _kinds[type] = MapKind(type.Name);
        }
        foreach (var required in RequiredModuleTypes)
            if (!_kinds.Keys.Any(t => t.Name == required)) throw new MissingMethodException(equipment.FullName, required + ".SetMainSubStats");
        _tractorModuleType = a.GetType("Behaviour.Equipment.Module.TractorModule", true)!;
        if (_kinds.TryGetValue(_tractorModuleType, out var tractorKind) && tractorKind != ShipModuleKind.Tractor)
            throw new InvalidOperationException("Tractor module kind mapping changed.");
        _tractorAutomatic = Field(_tractorModuleType, "amountOfBeams", typeof(int));
        _tractorManual = Field(_tractorModuleType, "amountOfBonusBeams", typeof(int));
        ModuleStatBuilders = builders;

        // The fill is virtual; subclass prefabs bypass a base-method patch, so bind each override.
        var fills = new List<MethodInfo>();
        var baseFill = Method(tooltip, "SetContent", tooltipSource);
        if (baseFill.ReturnType != typeof(void)) throw new InvalidOperationException("Tooltip content fill shape changed.");
        fills.Add(baseFill);
        foreach (var type in Subclasses(a, tooltip))
        {
            var fill = type.GetMethod("SetContent", Any | BindingFlags.DeclaredOnly, null, new[] { tooltipSource }, null);
            if (fill != null && fill.DeclaringType == type && !fill.IsAbstract && fill.ReturnType == typeof(void)) fills.Add(fill);
        }
        ContentFills = fills;
    }

    private static ShipModuleKind MapKind(string typeName)
    {
        var stem = typeName.EndsWith("Module", StringComparison.Ordinal) ? typeName[..^"Module".Length] : typeName;
        if (Enum.TryParse<ShipModuleKind>(stem, out var kind)) return kind;
        if (typeName.EndsWith("Turret", StringComparison.Ordinal)) return ShipModuleKind.Turret;
        return ShipModuleKind.Other;
    }

    private static IEnumerable<Type> Subclasses(Assembly a, Type baseType)
    {
        foreach (var type in a.GetTypes())
            if (type != baseType && baseType.IsAssignableFrom(type)) yield return type;
    }

    private static MethodInfo Method(Type type, string name, params Type[] parameters)
        => type.GetMethod(name, Any, null, parameters, null) ?? throw new MissingMethodException(type.FullName, name);
    private static FieldInfo Field(Type type, string name, Type type_)
    {
        for (Type? current = type; current != null; current = current.BaseType)
            if (current.GetField(name, Any | BindingFlags.DeclaredOnly) is FieldInfo field && field.FieldType == type_) return field;
        throw new MissingFieldException(type.FullName, name);
    }
    private static PropertyInfo Property(Type type, string name, Type? type_ = null, bool assignable = false)
    {
        var property = type.GetProperty(name, Any) ?? throw new MissingMemberException(type.FullName, name);
        if (type_ != null && !(assignable ? type_.IsAssignableFrom(property.PropertyType) : type_ == property.PropertyType))
            throw new InvalidOperationException(type.FullName + "." + name + " shape changed.");
        return property;
    }

    private bool MainThread => Thread.CurrentThread.ManagedThreadId == _thread;

    internal void AddModuleDescription(object module)
    {
        if (!MainThread || !_tooltips.Availability.IsAvailable) return;
        try
        {
            var type = module.GetType();
            if (!_kinds.TryGetValue(type, out var kind)) kind = MapKind(type.Name);
            var stats = _stats.GetValue(module); if (stats == null) return;
            var lines = new List<ModuleStatLine>();
            if (_subStats.GetValue(stats) is IList current)
                foreach (var stat in current)
                { if (stat != null) lines.Add(new ModuleStatLine((string?)_subName.GetValue(stat) ?? "", (string?)_subAmount.GetValue(stat) ?? "")); }
            var displayName = _getName.Invoke(module, null) as string ?? "";
            var tractor = kind == ShipModuleKind.Tractor && _tractorModuleType.IsInstanceOfType(module) && _tractorAutomatic != null && _tractorManual != null
                ? new TractorModule((int)_tractorAutomatic.GetValue(module)!, (int)_tractorManual.GetValue(module)!) : null;
            var values = new ShipModule(kind, displayName, (int)_quality.GetValue(module)!, lines, tractor);
            foreach (var line in _tooltips.Describe(values))
                _addStat.Invoke(stats, new object[] { string.Concat(line.Spans.Select(span => span.Text)), "" });
        }
        catch (Exception error) { Report(error); }
    }

    internal void AddItemDescription(object tooltipObject)
    {
        if (!MainThread || !_tooltips.Availability.IsAvailable) return;
        try
        {
            if (_source.GetValue(tooltipObject) is not Component source || !_itemSourceType.IsInstanceOfType(source)) return;
            if (_sourceItem.GetValue(source) is not object item) return;
            var values = new ItemInfo(
                (string?)_itemId.GetValue(item) ?? "", (string?)_itemName.GetValue(item) ?? "",
                (string?)_itemDescription.GetValue(item) ?? "", (int)_sourceCount.GetValue(source)!,
                (int)_itemLevel.GetValue(item)!);
            foreach (var line in _tooltips.Describe(values))
                _addText.Invoke(tooltipObject, new object[] { string.Concat(line.Spans.Select(span => Format(span.Text, span.Style))), 12, 8f });
        }
        catch (Exception error) { Report(error); }
    }

    internal void AddMasteryDescription(object badge, object tooltipObject)
    {
        if (!MainThread || !_tooltips.Availability.IsAvailable) return;
        try
        {
            var nativeTree = _badgeTree.GetValue(badge);
            var tree = nativeTree == null ? null : _skills.Read(nativeTree);
            if (tree == null) return;
            foreach (var line in _tooltips.Describe(tree))
                _addText.Invoke(tooltipObject, new object[] { string.Concat(line.Spans.Select(span => Format(span.Text, span.Style))), 12, 8f });
        }
        catch (Exception error) { Report(error); }
    }

    private string Format(string text, TooltipTextStyle style)
    {
        if (style == TooltipTextStyle.Normal) return text;
        var color = style == TooltipTextStyle.Bonus ? _green : _details;
        return "<color=#" + ColorUtility.ToHtmlStringRGBA(color) + ">" + text + "</color>";
    }
    private void Report(Exception error) { try { _report(error); } catch { } }
}
