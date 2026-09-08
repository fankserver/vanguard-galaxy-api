using System;
using System.Collections.Generic;
using Behaviour.Crafting;
using Behaviour.UI.Forge;
using Behaviour.UI.Spacestation;
using Source.Galaxy;
using Source.Galaxy.POI;
using Source.Player;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ForgeUiNativeTests : IDisposable
{
    private readonly Guid _session = Guid.NewGuid();
    private readonly CraftingRecipe _root = new() { identifier = "root" }, _variant = new() { identifier = "variant" };
    private readonly RecipeCatalogNativeSource _source;
    private readonly SpaceStationInterior _interior = new();
    private readonly SpaceStation _station = new();
    public ForgeUiNativeTests()
    {
        _root.parentRecipe = _root; _variant.parentRecipe = _root;
        _station.forge = new Source.Mining.Forge { spaceStation = _station, recipes = new[] { _root, _variant } };
        Source.Mining.Forge.current = _station.forge;
        GamePlayer.current = new() { currentPointOfInterest = _station };
        GalaxyMapData.current = new(); GalaxyMapData.current.AddPoi(_station);
        SpaceStationInterior.instance = _interior; _interior.spacestation = _station;
        _interior.tabActions[SpaceStationFacility.Forge] = () =>
        {
            ForgeUI.current = new();
            ForgeUI.current.SelectRecipe(_root, new() { _root, _variant }, ForgeUI.preselectRecipe!);
        };
        ForgeUI.current = null; ForgeUI.preselectRecipe = null;
        _source = new(typeof(CraftingRecipe).Assembly, (_, _) => null, value => value);
        _source.UiSession = () => _session; _source.UiActive = _ => true; _source.UiBelongsTo = (_, _) => true; _source.UiAssetAlive = _ => true;
    }
    [Fact]
    public void ExactVariantNavigationUsesWholeGroupAndRestoresPreselection()
    {
        var previous = new CraftingRecipe { identifier = "previous" }; ForgeUI.preselectRecipe = previous;
        Assert.Equal(ForgeNavigationStatus.Selected, _source.OpenUi(_session, new("vanilla", "forge/variant")));
        Assert.Same(previous, ForgeUI.preselectRecipe);
        Assert.Equal(new[] { _root, _variant }, ForgeUI.current!.tabContents.unlockedRecipes);
        Assert.Same(_variant, ForgeUI.current.tabContents.subRecipe);
        var snapshot = _source.ReadUi(_session)!; Assert.Equal(2, snapshot.Batches); Assert.Equal(2, snapshot.AvailableVariants.Count);
    }
    [Fact]
    public void MissingAndWrongStationRequestsDoNotOpenUi()
    {
        Assert.Equal(ForgeNavigationStatus.RecipeUnavailable, _source.OpenUi(_session, new("vanilla", "forge/missing")));
        Assert.Null(ForgeUI.current);
        _interior.spacestation = new();
        Assert.Equal(ForgeNavigationStatus.NotAtStation, _source.OpenUi(_session, new("vanilla", "forge/variant")));
        Assert.Null(ForgeUI.current);
    }
    [Fact]
    public void ReadsDoNotSelectBuildOrRepairIncompleteGroups()
    {
        _source.OpenUi(_session, new("vanilla", "forge/variant")); var ui = ForgeUI.current!; var calls = ui.SelectCalls;
        _ = _source.ReadUi(_session); _ = _source.ReadUi(_session); Assert.Equal(calls, ui.SelectCalls);
        ui.tabContents.unlockedRecipes = new List<CraftingRecipe> { _variant };
        Assert.Null(_source.ReadUi(_session)); Assert.Equal(calls, ui.SelectCalls);
    }
    [Fact]
    public void ClosingAndReopeningChangesViewIdentityAndSessionLossHidesIt()
    {
        _source.OpenUi(_session, new("vanilla", "forge/variant")); var before = _source.ReadUi(_session)!;
        _interior.currentTab = SpaceStationFacility.Airlock; Assert.Null(_source.ReadUi(_session));
        _source.OpenUi(_session, new("vanilla", "forge/variant")); Assert.NotEqual(before.View, _source.ReadUi(_session)!.View);
        _source.UiSession = () => null; Assert.Null(_source.ReadUi(_session));
    }
    public void Dispose()
    { _source.ClearUi(); ForgeUI.current = null; ForgeUI.preselectRecipe = null; SpaceStationInterior.instance = null; GamePlayer.current = null; }
}
