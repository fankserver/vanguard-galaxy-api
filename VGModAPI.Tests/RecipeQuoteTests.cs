using System;
using System.Linq;
using Behaviour.Crafting;
using Behaviour.Crew;
using Behaviour.Item;
using Behaviour.Mining;
using Source.Galaxy.POI;
using Source.Item;
using Source.Mining;
using Source.Player;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class RecipeQuoteTests : IDisposable
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly RecipeQuoteService _service;
    private readonly RecipeCatalogNativeSource _source;
    private readonly GamePlayer _player = new() { currentSpaceShip = new(), credits = 1000 };
    private readonly SpaceStation _station = new();
    private readonly InventoryItemType _item = new() { identifier = "component" };
    private readonly CraftingRecipe _recipe = new() { identifier = "recipe" };
    private readonly RecipeStationHandle _handle;
    private static readonly RecipeId Id = new("vanilla", "forge/recipe");
    public RecipeQuoteTests()
    {
        _hub.SetCapability("recipe-quotes", true, "Bound.");
        var session = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(session); _hub.GameplayInitialized(session);
        GamePlayer.current = _player; _player.currentPointOfInterest = _station;
        Source.Galaxy.GalaxyMapData.current = new(); Source.Galaxy.GalaxyMapData.current.AddPoi(_station);
        _station.forge = new Forge { spaceStation = _station, recipes = new[] { _recipe } }; Forge.current = _station.forge;
        _item.gameObject.Components[typeof(InventoryItemType)] = _item;
        InventoryItemType.all = new[] { _item };
        _recipe.itemMaterials.Add(new() { item = _item.gameObject, count = 2 });
        _recipe.materials.Add(new() { material = RefinedMaterial.TestMetal, amount = .25f });
        _recipe.results.Add(new() { item = _item.gameObject, count = 3 });
        _station.materialStorage.items = new[] { Stack(4) };
        _player.currentSpaceShip.cargo.items = new[] { Stack(6) };
        _source = new RecipeCatalogNativeSource(typeof(CraftingRecipe).Assembly,
            (prefab, type) => ((UnityEngine.GameObject)prefab).Components.TryGetValue(type, out var value) ? value : null, value => value);
        _service = new RecipeQuoteService(_hub, _source, _ => { }); _handle = _service.CurrentStation!;
    }
    private Inventory.InventoryItem Stack(int count, bool favourite = false) => new() { item = _item, count = count, favourite = favourite };
    public void Dispose()
    {
        _service.Dispose(); _hub.Dispose(); GamePlayer.current = null; Forge.current = null; Source.Galaxy.GalaxyMapData.current = null; SpaceStation.TestCurrent = null;
        InventoryItemType.all = Array.Empty<InventoryItemType>(); CraftingRecipe.all = Array.Empty<CraftingRecipe>();
        Behaviour.UI.Spacestation.SpaceStationInterior.instance = null;
        SkilltreeNode.industrialForgeBonusCraft = new(); SkilltreeNode.industrialRefBonusCraft1 = new(); SkilltreeNode.industrialCrystalRefineChance = new();
    }
    [Fact]
    public void QuotePreservesBatchesAmountsLevelInventoryAndRevision()
    {
        var first = _service.Quote(_handle, Id, 2);
        Assert.True(first.RequirementsMet); Assert.Equal(20L, first.CreditsRequired); Assert.Equal(10, first.OutputLevel);
        Assert.Equal(.5, first.Inputs[0].Required); Assert.Equal(4, first.Inputs[1].Required); Assert.Equal(10, first.Inputs[1].Available);
        Assert.Equal(6, Assert.Single(first.Outputs).Amount);
        Assert.Contains(first.Inputs[1].Inventories, balance => balance.Kind == RecipeInventoryKind.StationMaterials && balance.Station!.Equals(_handle));
        _recipe.TestLevel = 20; _recipe.TestScale = 2; _recipe.craftingCost = 30;
        var next = _service.Quote(_handle, Id, 2);
        Assert.Equal(20, next.OutputLevel); Assert.Equal(8, next.Inputs[1].Required); Assert.Equal(60L, next.CreditsRequired);
        Assert.True(next.Revision > first.Revision); Assert.Equal(4, first.Inputs[1].Required);
    }
    [Fact]
    public void UndockingDoesNotPretendCargoIsEmptyOrCountItAsUsable()
    {
        _player.currentPointOfInterest = null; Forge.current = null;
        var result = _service.Quote(_handle, Id, 3);
        Assert.Equal(RecipeQuoteStatus.Available, result.Status); Assert.Contains(RecipeBlocker.MissingIngredients, result.Blockers);
        var input = result.Inputs[1]; Assert.Equal(4, input.Available); Assert.Equal(2, input.Missing);
        var cargo = input.Inventories.Single(balance => balance.Kind == RecipeInventoryKind.ShipCargo);
        Assert.False(cargo.Accessible); Assert.Null(cargo.Amount);
    }
    [Fact]
    public void ReportsQueueAndCreditsTogetherAndRejectsStaleAndInvalidRequests()
    {
        _player.credits = 0; _station.forge!.maxJobs = 0;
        var result = _service.Quote(_handle, Id);
        Assert.Contains(RecipeBlocker.QueueFull, result.Blockers); Assert.Contains(RecipeBlocker.InsufficientCredits, result.Blockers);
        Assert.False(result.RequirementsMet);
        Assert.Equal(RecipeQuoteStatus.InvalidRequest, _service.Quote(_handle, Id, 0).Status);
        Assert.Equal(RecipeQuoteStatus.InvalidRequest, _service.Quote(_handle, Id, int.MaxValue).Status);
        Assert.Equal(RecipeQuoteStatus.StaleHandle, _service.Quote(new RecipeStationHandle(_handle.SessionId, Guid.NewGuid(), "forged"), Id).Status);
        var next = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(next); _hub.GameplayInitialized(next);
        Assert.Equal(RecipeQuoteStatus.StaleHandle, _service.Quote(_handle, Id).Status);
    }
    [Fact]
    public void RefiningHonorsDifferentCargoAccessAndNativePerStackSelectionPolicy()
    {
        _item.itemCategory = ItemCategory.Ore;
        _item.gameObject.Components[typeof(OreItemData)] = new OreItemData { contents = new() { new() { yield = .25f } } };
        var id = new RecipeId("vanilla", "refining/component");
        Assert.Equal(4, _service.Quote(_handle, id).Inputs[0].Available); // no interior: refinery cannot use cargo
        Behaviour.UI.Spacestation.SpaceStationInterior.instance = new() { spacestation = _station };
        Assert.Equal(10, _service.Quote(_handle, id).Inputs[0].Available);
        _station.materialStorage.items = new[] { Stack(4), Stack(5, true) }; _player.Reserved = 3;
        _player.currentSpaceShip!.cargo.items = new[] { Stack(4), Stack(2) };
        var automatic = _service.Quote(_handle, id, 2, RefineryInputPolicy.AutomaticSelection);
        Assert.Equal(2, automatic.Inputs[0].Available); Assert.Equal(.5, automatic.Outputs[0].Amount);
        Assert.Equal(15, _service.Quote(_handle, id).Inputs[0].Available);
    }
    [Fact]
    public void OutputRoutesAndConditionalBonusesAreNotGuaranteedDelivery()
    {
        Behaviour.UI.Spacestation.SpaceStationInterior.instance = new() { spacestation = _station };
        SkilltreeNode.industrialForgeBonusCraft.currentIncrease = .5f;
        var quote = _service.Quote(_handle, Id, 2);
        Assert.Equal(2, quote.Outputs.Count); Assert.Equal(.5, quote.Outputs[1].ProbabilityPerBatch);
        Assert.Contains(RecipeInventoryKind.ShipCargo, quote.Outputs[0].PossibleDestinations);
        _player.forgeDepositInCargo = false;
        Assert.DoesNotContain(RecipeInventoryKind.ShipCargo, _service.Quote(_handle, Id).Outputs[0].PossibleDestinations);
        _player.forgeDepositInCargo = true; _player.currentSpaceShip!.cargo.Capacity = 0;
        Assert.DoesNotContain(RecipeInventoryKind.ShipCargo, _service.Quote(_handle, Id).Outputs[0].PossibleDestinations);
    }
    [Fact]
    public void ReplacedStationAndDuplicateIngredientRowsCannotReuseOldAvailability()
    {
        _recipe.itemMaterials.Add(new() { item = _item.gameObject, count = 9 });
        var quote = _service.Quote(_handle, Id);
        Assert.Equal(11, quote.Inputs[1].Required); Assert.Equal(10, quote.Inputs[1].Available);
        Assert.Contains(RecipeBlocker.MissingIngredients, quote.Blockers);
        Source.Galaxy.GalaxyMapData.current!.AddPoi(new SpaceStation { guid = _station.guid });
        Assert.Equal(RecipeQuoteStatus.StaleHandle, _service.Quote(_handle, Id).Status);
    }
    [Fact]
    public void FloatCreditDebitAndUnknownInventoryAreReportedTruthfully()
    {
        _recipe.craftingCost = 16777217; _player.credits = long.MaxValue;
        _station.materialStorage = null!;
        var quote = _service.Quote(_handle, Id);
        Assert.Equal(16777216L, quote.CreditsRequired);
        Assert.Null(quote.Inputs[1].Available); Assert.Contains(RecipeBlocker.InventoryUnavailable, quote.Blockers);
    }
    [Fact]
    public void RefineryOnlyStationDoesNotRequireForge()
    {
        _station.forge = null; Forge.current = null; SpaceStation.TestCurrent = _station;
        Assert.NotNull(_service.CurrentStation);
        _item.gameObject.Components[typeof(OreItemData)] = new OreItemData { contents = new() { new() { yield = .5f } } };
        Assert.Equal(RecipeQuoteStatus.Available, _service.Quote(_handle, new RecipeId("vanilla", "refining/component")).Status);
        Assert.Equal(RecipeQuoteStatus.StationUnavailable, _service.Quote(_handle, Id).Status);
    }
    [Fact]
    public void TemplateKindsWithSameLocalIdentityKeepDistinctRoutesWithoutBuildingItems()
    {
        var equipment = new UnityEngine.GameObject();
        equipment.Components[typeof(Behaviour.Equipment.Builder.EquipmentBuilder)] = new Behaviour.Equipment.Builder.EquipmentBuilder
            { identifier = "same", prefab = new InventoryItemType { itemCategory = ItemCategory.Module } };
        var item = new UnityEngine.GameObject();
        item.Components[typeof(Behaviour.Item.Builder.ItemBuilder)] = new Behaviour.Item.Builder.ItemBuilder
            { identifier = "same", prefab = new InventoryItemType { itemCategory = ItemCategory.Material } };
        _recipe.results.Clear(); _recipe.results.Add(new() { item = equipment }); _recipe.results.Add(new() { item = item });
        var quote = _service.Quote(_handle, Id);
        Assert.Equal(RecipeQuoteStatus.Available, quote.Status);
        Assert.Equal(RecipeResourceKind.EquipmentTemplate, quote.Outputs[0].Resource!.Kind);
        Assert.Equal(RecipeInventoryKind.PlayerArmory, Assert.Single(quote.Outputs[0].PossibleDestinations));
        Assert.Equal(RecipeResourceKind.ItemTemplate, quote.Outputs[1].Resource!.Kind);
        Assert.Equal(RecipeInventoryKind.StationMaterials, Assert.Single(quote.Outputs[1].PossibleDestinations));
    }
    [Fact]
    public void RefineryRewardsRemainConditionalAndRespectIgnoreFlag()
    {
        _item.itemCategory = ItemCategory.Ore;
        var ore = new OreItemData { contents = new() { new() { yield = .25f } } };
        _item.gameObject.Components[typeof(OreItemData)] = ore;
        SkilltreeNode.industrialRefBonusCraft1.isActive = true; SkilltreeNode.industrialCrystalRefineChance.currentIncrease = .5f;
        var id = new RecipeId("vanilla", "refining/component"); var quote = _service.Quote(_handle, id, 2);
        Assert.Equal(3, quote.Outputs.Count); Assert.Equal(.1, quote.Outputs[1].ProbabilityPerBatch); Assert.Null(quote.Outputs[2].Resource);
        Assert.DoesNotContain(RecipeInventoryKind.ShipCargo, quote.Outputs[2].PossibleDestinations);
        ore.ignoreExtraRewards = true; Assert.Single(_service.Quote(_handle, id).Outputs);
    }
    [Fact]
    public void SessionInvalidationReleasesIssuedNativeStationReferences()
    {
        _hub.Invalidate("menu");
        Assert.Equal(RecipeQuoteStatus.StaleHandle, _source.Quote(_handle, Id, 1, RefineryInputPolicy.Manual, 1).Status);
        Assert.Equal(RecipeQuoteStatus.SessionUnavailable, _service.Quote(_handle, Id).Status);
    }
    [Fact]
    public void ColdForgePricingCannotInvokeTransitivePreviewBuilder()
    {
        _recipe.customCost = 0; _recipe.dynamicCost = -1; _item.calcCost = -1;
        var cold = _service.Quote(_handle, Id);
        Assert.Equal(RecipeQuoteStatus.Available, cold.Status); Assert.NotEmpty(cold.Inputs); Assert.NotEmpty(cold.Outputs);
        Assert.Null(cold.CreditsRequired); Assert.Contains(RecipeBlocker.PricingUnavailable, cold.Blockers); Assert.False(cold.RequirementsMet);
        Assert.Equal(0, _item.PreviewBuilderCalls); Assert.Equal(-1, _recipe.dynamicCost); Assert.Equal(-1, _item.calcCost);
        _item.calcCost = 25;
        Assert.Null(_service.Quote(_handle, Id).CreditsRequired);
        Assert.Equal(-1, _recipe.dynamicCost); Assert.Equal(0, _item.PreviewBuilderCalls);
        _recipe.dynamicCost = 25; // Native presentation or mutation initialized the cache, not the quote.
        _item.calcCost = -1; // A warm recipe price does not need to read a cold ingredient price.
        Assert.Equal(25L, _service.Quote(_handle, Id).CreditsRequired); Assert.Equal(0, _item.PreviewBuilderCalls);
    }
    [Fact]
    public void ColdRefineryPricingCannotInvokeTransitivePreviewBuilder()
    {
        _item.calcCost = -1;
        _item.gameObject.Components[typeof(OreItemData)] = new OreItemData { PricingItem = _item, contents = new() { new() { yield = .5f } } };
        var quote = _service.Quote(_handle, new RecipeId("vanilla", "refining/component"));
        Assert.Equal(RecipeQuoteStatus.Available, quote.Status); Assert.Single(quote.Inputs); Assert.Single(quote.Outputs);
        Assert.Null(quote.CreditsRequired); Assert.Contains(RecipeBlocker.PricingUnavailable, quote.Blockers);
        Assert.Equal(0, _item.PreviewBuilderCalls); Assert.Equal(-1, _item.calcCost);
    }
    [Theory]
    [InlineData(16777215, true)] [InlineData(16777216, true)] [InlineData(16777217, false)] [InlineData(16777218, true)]
    public void ExtractionRefusesLossyNativeMaterialCounts(int count, bool supported)
    {
        _player.Materials = 33554432; _player.credits = long.MaxValue;
        InventoryItemType.all = new[] { new InventoryItemType { identifier = "CanisterTestMetal" } };
        var quote = _service.QuoteMaterialExtraction(_handle, new RecipeResourceId("vanilla", "TestMetal", RecipeResourceKind.RefinedMaterial), count);
        Assert.Equal(supported ? RecipeQuoteStatus.Available : RecipeQuoteStatus.InvalidRequest, quote.Status);
        if (supported) Assert.Equal(count, Assert.Single(quote.Inputs).Required);
        else Assert.Empty(quote.Inputs);
    }
    [Fact]
    public void ExtractionQuotesCreditsAndCanistersWithoutQueueOrMutation()
    {
        var canister = new InventoryItemType { identifier = "CanisterTestMetal" };
        InventoryItemType.all = new[] { _item, canister };
        var quote = _service.QuoteMaterialExtraction(_handle, new RecipeResourceId("vanilla", "TestMetal", RecipeResourceKind.RefinedMaterial), 5);
        Assert.Equal(10L, quote.CreditsRequired); Assert.Null(quote.QueueCapacity); Assert.Null(quote.SecondsPerBatch);
        Assert.Equal(5, quote.Outputs[0].Amount); Assert.Equal(100, _player.Materials); Assert.Equal(1000L, _player.credits);
    }
}
