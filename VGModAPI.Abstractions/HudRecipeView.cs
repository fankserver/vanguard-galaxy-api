using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VGModAPI;

/// <summary>Recipe-row quantities, not an inventory reservation. Null availability means unknown or inaccessible.</summary>
public sealed class HudIngredientAmounts
{
    public double Required { get; }
    public double? Available { get; }
    public bool? Sufficient => Available.HasValue ? Available.Value >= Required : null;
    public HudIngredientAmounts(double required, double? available)
    {
        if (double.IsNaN(required) || double.IsInfinity(required) || required < 0) throw new ArgumentOutOfRangeException(nameof(required));
        if (available.HasValue && (double.IsNaN(available.Value) || double.IsInfinity(available.Value) || available.Value < 0)) throw new ArgumentOutOfRangeException(nameof(available));
        Required = required; Available = available;
    }
    public string RequiredText => Required.ToString("0.###", CultureInfo.InvariantCulture);
    public string AvailableText => Available.HasValue ? Available.Value.ToString("0.###", CultureInfo.InvariantCulture) : "?";
}

/// <summary>A compact Forge-style presentation; consumers supply their own quantities and refresh policy.</summary>
public sealed class HudRecipeView
{
    public string Title { get; }
    public HudPresentation? Presentation { get; }
    public IReadOnlyList<HudRow> Ingredients { get; }
    public HudRow? Result { get; }
    public bool Closable { get; }
    public HudRecipeView(string title, IEnumerable<HudRow> ingredients, HudPresentation? presentation = null,
        HudRow? result = null, bool closable = true)
    {
        Title = HudText.Check(title, 256);
        Ingredients = RecipeValues.Copy(ingredients, 30);
        if (Ingredients.Any(row => row.IngredientAmounts == null)) throw new ArgumentException("Recipe ingredients require structured quantities.", nameof(ingredients));
        Presentation = presentation; Result = result; Closable = closable;
        // Validate identities before a caller installs the view.
        _ = ToPanel();
    }
    /// <summary>Use with an ordinary HUD registration; its button becomes the panel's integrated action.</summary>
    public HudPanel ToPanel()
    {
        var rows = new List<HudRow>(Ingredients);
        if (Result != null)
        {
            rows.Add(new HudRow("recipe-result-heading", "Result:"));
            rows.Add(Result);
        }
        return new HudPanel(Title, rows, Presentation, Closable);
    }
}
