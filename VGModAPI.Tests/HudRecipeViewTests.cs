using System;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class HudRecipeViewTests
{
    [Fact]
    public void IngredientQuantitiesDistinguishShortageAndUnknown()
    {
        Assert.False(new HudIngredientAmounts(143, 48).Sufficient);
        Assert.True(new HudIngredientAmounts(104, 104).Sufficient);
        Assert.Null(new HudIngredientAmounts(104, null).Sufficient);
        Assert.Equal("?", new HudIngredientAmounts(104, null).AvailableText);
        Assert.Throws<ArgumentOutOfRangeException>(() => new HudIngredientAmounts(double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HudIngredientAmounts(1, -1));
    }

    [Fact]
    public void RecipeViewReusesOrdinaryRegistrationRowsAndOptionalResult()
    {
        var ingredient = HudRow.Ingredient("titanium", "Titanium Plate", 143, 48, clickable: true);
        var result = new HudRow("cannon", "Cannon x1");
        var panel = new HudRecipeView("Cannon", new[] { ingredient }, results: new[] { result }).ToPanel();
        Assert.Equal(3, panel.Rows.Count);
        Assert.Same(ingredient, panel.Rows[0]);
        Assert.Equal("Result:", panel.Rows[1].Label);
        Assert.Same(result, panel.Rows[2]);
        Assert.True(panel.Rows[0].Clickable);
        Assert.Single(new HudRecipeView("Cannon", new[] { ingredient }).ToPanel().Rows);
    }

    [Fact]
    public void DuplicateAndUnstructuredIngredientsAreRejected()
    {
        var row = HudRow.Ingredient("same", "Oxide", 1, 2);
        Assert.Throws<ArgumentException>(() => new HudRecipeView("Recipe", new[] { row, row }));
        Assert.Throws<ArgumentException>(() => new HudRecipeView("Recipe", new[] { new HudRow("text", "Text") }));
    }

    [Fact]
    public void IngredientNameCannotConsumeQuantityColumns()
    {
        Assert.Equal(144f, RecipeWidgetLayout.QuantitiesWidth);
        Assert.True(RecipeWidgetLayout.IngredientNameWidth > 0);
        Assert.Equal("143", new HudIngredientAmounts(143, 48).RequiredText);
        Assert.True(new HudIngredientAmounts(double.MaxValue, double.MaxValue).RequiredText.Length < 12);
        Assert.True(new HudIngredientAmounts(double.MaxValue, double.MaxValue).AvailableText.Length < 12);
    }

    [Fact]
    public void ContextualActionReservesNativeResultHeading()
    {
        Assert.True(ForgeActionBand.TryCreateResult(1920, 1080, 680, 1140, 380, out var band));
        Assert.Equal(960f, band.Left);
        Assert.Equal(348f, band.Bottom);
        Assert.False(ForgeActionBand.TryCreateResult(1920, 1080, 680, 800, 380, out _));
        Assert.False(ForgeActionBand.TryCreateResult(1920, 1080, 680, 1140, 1060, out _));
    }
}
