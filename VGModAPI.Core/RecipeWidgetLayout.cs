using System;

namespace VGModAPI.Core;

/// <summary>Content-driven recipe geometry; bounded content scrolls instead of escaping the HUD.</summary>
internal readonly struct RecipeWidgetLayout
{
    internal const float Width = 300;
    internal const float QuantityWidth = 72, QuantitiesWidth = QuantityWidth * 2;
    internal const float IngredientNameWidth = Width - 40 - QuantitiesWidth;
    internal const float HeaderHeight = 32;
    internal const float RowHeight = 24;
    internal const float FooterHeight = 30;
    internal float Height { get; }
    internal float RowsHeight { get; }
    internal bool Scrolls { get; }

    internal RecipeWidgetLayout(int rows, bool footer, float availableHeight)
    {
        if (rows < 0 || rows > 32) throw new ArgumentOutOfRangeException(nameof(rows));
        if (float.IsNaN(availableHeight) || float.IsInfinity(availableHeight) || availableHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(availableHeight));
        var chrome = HeaderHeight + 8 + (footer ? FooterHeight : 0);
        Height = Math.Min(availableHeight, chrome + rows * RowHeight);
        RowsHeight = Math.Max(0, Height - chrome);
        Scrolls = rows * RowHeight > RowsHeight;
    }
}
