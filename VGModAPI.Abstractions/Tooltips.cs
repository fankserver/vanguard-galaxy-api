using System;
using System.Collections.Generic;

namespace VGModAPI;

public enum TooltipTextStyle { Normal, Bonus, Details }

/// <summary>The game's ship equipment module families, named after the native
/// Behaviour.Equipment.Module types. Other covers native modules without a dedicated kind.</summary>
public enum ShipModuleKind
{
    Tractor, Mining, Salvage, ShieldGenerator, Reactor, ReactorLight, DroneBay, TorpedoBay,
    Armor, CargoScoop, EngineThrusters, HangarBay, Hull, Painter, Repair, Scanner, Other
}

/// <summary>A stat line the native module just showed, captured before extensions append.</summary>
public sealed class ModuleStatLine
{
    public string Label { get; }
    public string Value { get; }
    internal ModuleStatLine(string label, string value)
    { Label = label; Value = value; }
}

/// <summary>Read-only ship equipment module values captured for one tooltip render.</summary>
public sealed class ShipModule
{
    public ShipModuleKind Kind { get; }
    public string DisplayName { get; }
    public int QualityLevel { get; }
    /// <summary>The vanilla stat lines currently shown, in native order.</summary>
    public IReadOnlyList<ModuleStatLine> StatLines { get; }
    /// <summary>Beam counts, non-null only when Kind is Tractor.</summary>
    public TractorModule? Tractor { get; }
    internal ShipModule(ShipModuleKind kind, string displayName, int qualityLevel,
        IReadOnlyList<ModuleStatLine> statLines, TractorModule? tractor)
    { Kind = kind; DisplayName = displayName; QualityLevel = qualityLevel; StatLines = statLines; Tractor = tractor; }
}

/// <summary>Read-only inventory item values captured for one item tooltip render.</summary>
public sealed class ItemInfo
{
    public string Identifier { get; }
    public string DisplayName { get; }
    public string Description { get; }
    /// <summary>Stack size shown by the source, when a concrete stack was provided.</summary>
    public int Count { get; }
    public int ItemLevel { get; }
    internal ItemInfo(string identifier, string displayName, string description, int count, int itemLevel)
    { Identifier = identifier; DisplayName = displayName; Description = description; Count = count; ItemLevel = itemLevel; }
}

/// <summary>Append-only tooltip content. Valid only during the tooltip callback; no Unity objects are exposed.</summary>
public sealed class Tooltip
{
    private readonly List<TooltipLine> _lines = new();
    private readonly Action _checkThread;
    private bool _closed;
    internal IReadOnlyList<TooltipLine> Lines => _lines;
    internal Tooltip(Action checkThread) { _checkThread = checkThread; }
    internal void Close() { _closed = true; }
    internal void Check()
    { _checkThread(); if (_closed) throw new InvalidOperationException("Tooltip callback has ended."); }
    public TooltipLine AddLine(string text, TooltipTextStyle style = TooltipTextStyle.Normal)
    {
        Check(); var line = new TooltipLine(this); line.Append(text, style); _lines.Add(line); return line;
    }
}

/// <summary>A line of styled text. Append adds inline text, without introducing another row.</summary>
public sealed class TooltipLine
{
    private readonly Tooltip _owner;
    private readonly List<(string Text, TooltipTextStyle Style)> _spans = new();
    internal IReadOnlyList<(string Text, TooltipTextStyle Style)> Spans => _spans;
    internal TooltipLine(Tooltip owner) { _owner = owner; }
    public TooltipLine Append(string text, TooltipTextStyle style = TooltipTextStyle.Normal)
    {
        _owner.Check();
        if (text == null) throw new ArgumentNullException(nameof(text));
        if (!Enum.IsDefined(typeof(TooltipTextStyle), style)) throw new ArgumentOutOfRangeException(nameof(style));
        _spans.Add((text, style)); return this;
    }
}

public interface ITooltipService : IServiceStatus
{
    /// <summary>Append lines to any ship equipment module's stat list, for every native module family.
    /// Called while the module builds its stats; filter by Kind for your family. Changes do not
    /// retroactively rebuild existing stats; native caching is preserved.</summary>
    IDisposable RegisterShipModule(string pluginId, Action<ShipModule, Tooltip> describe);
    /// <summary>Append lines after any inventory item's native tooltip body, using the item the
    /// source is showing (inventory, shop, loot and compare sources all route through it).</summary>
    IDisposable RegisterItem(string pluginId, Action<ItemInfo, Tooltip> describe);
    /// <summary>Append after any skill tree's native mastery bonus lines, using live tree values.</summary>
    IDisposable RegisterSkillTree(string pluginId, Action<SkillTree, Tooltip> describe);
}
