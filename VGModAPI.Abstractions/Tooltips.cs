using System;
using System.Collections.Generic;

namespace VGModAPI;

public enum TooltipTextStyle { Normal, Bonus, Details }

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
    /// <summary>Append module stat descriptions after vanilla builds them. Native caching is preserved;
    /// changes do not retroactively rebuild existing stats. Style is ignored by the native stat list.</summary>
    IDisposable RegisterTractorModule(string pluginId, Action<TractorModule, Tooltip> describe);
    /// <summary>Append after any skill tree's native mastery bonus lines, using live tree values.</summary>
    IDisposable RegisterSkillTree(string pluginId, Action<SkillTree, Tooltip> describe);
}
