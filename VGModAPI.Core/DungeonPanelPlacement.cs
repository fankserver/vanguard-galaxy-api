using System;
namespace VGModAPI.Core;

internal static class DungeonPanelPlacement
{
    internal static (float X, float Top, float Width, float Height)? Around(float left, float bottom, float right, float top, float panelLeft, float panelBottom, float panelRight, float panelTop)
    {
        var beside = Beside(left, bottom, right, top, panelLeft, panelRight, panelTop);
        if (beside.HasValue) return beside;
        foreach (var value in new[] { left, bottom, right, top, panelLeft, panelBottom, panelRight, panelTop })
            if (float.IsNaN(value) || float.IsInfinity(value)) return null;
        var width = Math.Min(280, right - left); if (width < 160 || top <= bottom || panelTop < panelBottom || panelRight < panelLeft) return null;
        var above = top - Math.Max(bottom, panelTop + 8); var below = Math.Min(top, panelBottom - 8) - bottom;
        var height = Math.Min(360, Math.Max(above, below)); if (height < 120) return null;
        var x = Math.Max(left, Math.Min(right - width, panelLeft));
        return (x, above >= below ? Math.Max(bottom, panelTop + 8) + height : Math.Min(top, panelBottom - 8), width, height);
    }
    /// <summary>Place contributions beside the native panel without obscuring its controls.</summary>
    internal static (float X, float Top, float Width, float Height)? Beside(float left, float bottom, float right, float top, float panelLeft, float panelRight, float panelTop)
    {
        var values = new[] { left, bottom, right, top, panelLeft, panelRight, panelTop };
        foreach (var value in values) if (float.IsNaN(value) || float.IsInfinity(value)) return null;
        if (right <= left || top - bottom < 120 || panelRight < panelLeft) return null;
        var rightSpace = right - Math.Max(left, panelRight + 8);
        var leftSpace = Math.Min(right, panelLeft - 8) - left;
        var useRight = rightSpace >= leftSpace;
        var width = Math.Min(280, useRight ? rightSpace : leftSpace); if (width < 160) return null;
        var height = Math.Min(360, top - bottom);
        var y = Math.Max(bottom + height, Math.Min(top, panelTop));
        return (useRight ? Math.Max(left, panelRight + 8) : Math.Min(right, panelLeft - 8) - width, y, width, height);
    }
}
