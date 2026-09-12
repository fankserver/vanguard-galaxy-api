using System;
using System.Linq;
using UiSurfaces;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ForgeInspectorTests
{
    [Fact]
    public void MultipleFractionalAndGeneratedOutputsRemainSeparateAndConditional()
    {
        var station = new RecipeStationHandle(Guid.NewGuid(), Guid.NewGuid(), "Station");
        var quote = new RecipeQuote(RecipeQuoteStatus.Available, "", station, new("vanilla", "recipe"), 2, 1,
            Array.Empty<RecipeIngredientRequirement>(), new[] {
                new RecipeOutputPreview(new("vanilla", "material", RecipeResourceKind.RefinedMaterial), .004, 1, "", Array.Empty<RecipeInventoryKind>()),
                new RecipeOutputPreview(null, 2, .25, "", Array.Empty<RecipeInventoryKind>()) }, Array.Empty<RecipeBlocker>(), creditsRequired: 0, creditsAvailable: 0);
        var rows = Inspector.Describe(quote, new(RecipeCatalogStatus.Available, station.SessionId, "", Array.Empty<RecipeSnapshot>()));
        Assert.Equal(2, rows.Count(row => row.Id.StartsWith("output")));
        Assert.Contains(rows, row => row.Detail.Contains("0.004"));
        Assert.Contains(rows, row => row.Label == "Generated output identity" && row.Tooltip.Contains("0.25"));
    }
    [Fact]
    public void UnavailableQuotesCannotRenderEconomicsAsSuccessful()
    {
        var quote = new RecipeQuote(RecipeQuoteStatus.Unsupported, "", null, new("vanilla", "recipe"), 1, 0,
            Array.Empty<RecipeIngredientRequirement>(), Array.Empty<RecipeOutputPreview>(), Array.Empty<RecipeBlocker>());
        var rows = Inspector.Describe(quote, new(RecipeCatalogStatus.IntegrationUnavailable, null, "", Array.Empty<RecipeSnapshot>()));
        Assert.Single(rows); Assert.Equal("Unsupported", rows[0].Detail);
    }
}
