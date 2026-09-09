using System;

namespace VGModAPI.Core;

internal readonly struct ForgeActionBand
{
    internal const float Height = 28, TooltipHeight = 52, Gap = 4, CellWidth = 180;
    internal float Left { get; }
    internal float Bottom { get; }
    internal float Width { get; }
    internal float TooltipBottom => Bottom + Height + Gap;
    private ForgeActionBand(float left, float bottom, float width) { Left = left; Bottom = bottom; Width = width; }
    internal static bool TryCreate(float canvasWidth, float canvasHeight, float tabsLeft, float tabsRight, float tabsTop, out ForgeActionBand band, float contentWidth = float.MaxValue)
    {
        band = default;
        if (!Finite(canvasWidth) || !Finite(canvasHeight) || !Finite(tabsLeft) || !Finite(tabsRight) || !Finite(tabsTop) || !Finite(contentWidth)) return false;
        var left = Math.Max(8, tabsLeft); var right = Math.Min(canvasWidth - 8, tabsRight);
        var bottom = tabsTop + Gap;
        var width = Math.Min(right - left, contentWidth);
        if (width < CellWidth || bottom < 0 || bottom + Height + Gap + TooltipHeight > canvasHeight) return false;
        band = new ForgeActionBand(left, bottom, width); return true;
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
