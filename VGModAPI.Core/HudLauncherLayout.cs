using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>One bounded lane per corner, with disjoint viewports even when opposite corners are populated.</summary>
internal sealed class HudLauncherLayout
{
    internal const float IconWidth = 40, TextWidth = 120, RowHeight = 40, Gap = 8, ScrollbarSpace = 8;
    internal readonly struct Slot
    {
        internal float X { get; }
        internal float Width { get; }
        internal Slot(float x, float width) { X = x; Width = width; }
    }
    internal float X { get; }
    internal float Y { get; }
    internal float Width { get; }
    internal float Height => Visible ? RowHeight + (Overflows ? ScrollbarSpace : 0) : 0;
    internal float ContentWidth { get; }
    internal bool Right { get; }
    internal bool Top { get; }
    internal bool Visible { get; }
    internal bool Overflows => ContentWidth > Width;
    internal IReadOnlyList<Slot> Slots { get; }

    internal HudLauncherLayout(HudCorner corner, IEnumerable<float> widths, float canvasWidth, float canvasHeight)
    {
        if (!Enum.IsDefined(typeof(HudCorner), corner)) throw new ArgumentOutOfRangeException(nameof(corner));
        if (!Finite(canvasWidth) || canvasWidth < 0) throw new ArgumentOutOfRangeException(nameof(canvasWidth));
        if (!Finite(canvasHeight) || canvasHeight < 0) throw new ArgumentOutOfRangeException(nameof(canvasHeight));
        var sizes = widths?.ToArray() ?? throw new ArgumentNullException(nameof(widths));
        if (sizes.Any(width => !Finite(width) || width < IconWidth || width > TextWidth))
            throw new ArgumentOutOfRangeException(nameof(widths));
        Right = corner is HudCorner.TopRight or HudCorner.BottomRight;
        Top = corner is HudCorner.TopLeft or HudCorner.TopRight;
        // Native HUD edge reservations: upper controls and lower flight/status UI.
        var edge = Top ? Math.Min(128, canvasWidth / 8) : 6;
        var laneWidth = Math.Max(0, Math.Min(400, (canvasWidth - edge * 2 - 12) / 2));
        ContentWidth = sizes.Sum() + Math.Max(0, sizes.Length - 1) * Gap;
        Width = Math.Min(ContentWidth, laneWidth);
        Visible = sizes.Length != 0 && Width >= IconWidth && canvasHeight >= (RowHeight + ScrollbarSpace) * 2 + 36;
        X = Right ? canvasWidth - edge - Width : edge;
        Y = Top ? canvasHeight - 12 - Height : Math.Min(340, canvasHeight - (RowHeight + ScrollbarSpace) * 2 - 24);
        var offset = 0f;
        Slots = Array.AsReadOnly(sizes.Select(width =>
        {
            var slot = new Slot(Right ? ContentWidth - offset - width : offset, width);
            offset += width + Gap; return slot;
        }).ToArray());
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
