using System;
using System.Linq;
using Behaviour.Crafting;
using Behaviour.Item;
using Source.Galaxy.POI;
using Source.Item;
using Source.Mining;
using Source.Player;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class CraftingCommandNativeTests : IDisposable
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly GamePlayer _player = new() { currentSpaceShip = new(), credits = 1000 };
    private readonly SpaceStation _station = new();
    private readonly InventoryItemType _item = new() { identifier = "part" };
    private readonly InventoryItemType _canister = new() { identifier = "CanisterTestMetal" };
    private readonly CraftingRecipe _recipe = new() { identifier = "test" };
    private readonly RecipeCatalogNativeSource _source;
    private readonly CraftingJobService _jobs;
    private readonly CraftingJobObserver _observer;
    private readonly CraftingCommandService _commands;
    private readonly RecipeStationHandle _handle;
    public CraftingCommandNativeTests()
    {
        var token = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(token); _hub.GameplayInitialized(token);
        GamePlayer.current = _player; _player.currentPointOfInterest = _station;
        Source.Galaxy.GalaxyMapData.current = new(); Source.Galaxy.GalaxyMapData.current.AddPoi(_station);
        _station.forge = new Forge { spaceStation = _station, recipes = new[] { _recipe } }; Forge.current = _station.forge;
        _station.refinery.spaceStation = _station;
        _item.gameObject.Components[typeof(InventoryItemType)] = _item;
        _canister.gameObject.Components[typeof(InventoryItemType)] = _canister;
        InventoryItemType.all = new[] { _item, _canister };
        _recipe.itemMaterials.Add(new() { item = _item.gameObject, count = 2 });
        _recipe.materials.Add(new() { material = RefinedMaterial.TestMetal, amount = .25f });
        _recipe.results.Add(new() { item = _item.gameObject, count = 1 });
        _station.materialStorage.items = new[] { new Inventory.InventoryItem { item = _item, inventory = _station.materialStorage, count = 10 } };
        _source = new(typeof(CraftingRecipe).Assembly, (prefab, type) => ((UnityEngine.GameObject)prefab).Components.TryGetValue(type, out var component) ? component : null, value => value);
        _jobs = new(_hub, _source, (_, _) => { }); _jobs.SetAvailable(true); _observer = new(_hub, _jobs, _source);
        _source.CommandSession = () => _hub.CurrentSession?.Phase == SessionPhase.GameplayInitialized ? _hub.CurrentSession.Id : null;
        _source.CommandObserver = _observer; _source.CommandJobEvents = _jobs; _source.RefreshCommandUi = _ => { };
        _commands = new(_hub, _jobs, _source, _ => { }); _commands.SetAvailable(true);
        _handle = _source.CurrentStation(_hub.CurrentSession!.Id)!;
        _station.materialStorage.AddHandler = (item, count) => Add(_station.materialStorage, item, count);
        _player.currentSpaceShip.cargo.AddHandler = (item, count) => Add(_player.currentSpaceShip.cargo, item, count);
        _station.forge.QueueHandler = Queue;
        _station.forge.CancelHandler = raw =>
        {
            var job = (Job)raw; var scope = _observer.Begin("jobCancelForge", _station.forge, new object[] { job });
            var material = _observer.Begin("jobMaterialAdd", _player, new object[] { RefinedMaterial.TestMetal, .25f * job.remainingAmount });
            _player.Materials += .25f * job.remainingAmount; _observer.End(material, null, null);
            _station.materialStorage.Add(_item, 2 * job.remainingAmount);
            _player.credits += (long)_recipe.craftingCost * job.remainingAmount; _station.forge.jobs.Remove(job); _observer.End(scope, null, null);
        };
        _station.refinery.ExtractHandler = (_, count) =>
        { _player.Materials -= count; _player.credits -= Refinery.GetExtractCost(RefinedMaterial.TestMetal, count); _player.currentSpaceShip.cargo.Add(_canister, count); };
    }
    private bool Queue(CraftingRecipe recipe, int count)
    {
        var scope = _observer.Begin("jobQueueForge", _station.forge!, new object[] { recipe, count });
        _player.credits -= (long)(float)((long)recipe.craftingCost * count); _player.Materials -= .25f * count;
        _station.materialStorage.items.Single().count -= 2 * count;
        _station.forge!.jobs.Add(new Job { parent = _station.forge, recipe = recipe, initialAmount = count, remainingAmount = count });
        _observer.End(scope, true, null); return true;
    }
    private Inventory.InventoryItem Add(Inventory inventory, InventoryItemType item, int count)
    {
        var scope = _observer.Begin("jobInventoryAdd", inventory, new object[] { item, count, false, false });
        var row = inventory.items.FirstOrDefault(value => ReferenceEquals(value.item, item));
        if (row == null) { row = new() { item = item, inventory = inventory }; inventory.items = inventory.items.Append(row).ToArray(); }
        row.count += count; _observer.End(scope, row, null); return row;
    }
    private CraftingCommandRequest QueueRequest(CraftingProtectionPolicy policy = CraftingProtectionPolicy.NativeConsumption) =>
        CraftingCommandRequest.Queue("test", Guid.NewGuid(), _handle, new("vanilla", "forge/test"), 2, policy);
    [Fact]
    public void QueueVerifiesDebitsAndCancellationUsesCurrentRefundPrice()
    {
        var queued = _commands.Execute(QueueRequest()); Assert.Equal(CraftingCommandStatus.Succeeded, queued.Status);
        Assert.Equal(-20, queued.CreditDelta); Assert.Equal(6, _station.materialStorage.items.Single().count);
        _recipe.craftingCost = 30;
        Forge.current = new Forge(); // Cancellation must use the job's parent, not the static current Forge.
        var cancelled = _commands.Execute(CraftingCommandRequest.Cancel("test", Guid.NewGuid(), queued.Jobs.Single()));
        Assert.Equal(CraftingCommandStatus.Succeeded, cancelled.Status); Assert.Equal(60, cancelled.CreditDelta);
        Assert.Equal(10, _station.materialStorage.items.Single().count); Assert.Equal(100, _player.Materials);
    }
    [Fact]
    public void ExplicitActionCanPrepareColdPriceWithoutChangingReadOnlyQuotePolicy()
    {
        _recipe.customCost = 0; _recipe.dynamicCost = -1; _item.calcCost = -1;
        var result = _commands.Execute(QueueRequest()); Assert.Equal(CraftingCommandStatus.Succeeded, result.Status);
        Assert.Equal(1, _item.PreviewBuilderCalls);
    }
    [Fact]
    public void ProtectedInputsRefuseNativeConsumption()
    {
        _station.materialStorage.items.Single().favourite = true;
        Assert.Equal(CraftingCommandStatus.ProtectedInputs, _commands.Execute(QueueRequest(CraftingProtectionPolicy.ProtectFavourites)).Status);
        _station.materialStorage.items.Single().favourite = false; _player.Reserved = 1;
        Assert.Equal(CraftingCommandStatus.ProtectedInputs, _commands.Execute(QueueRequest(CraftingProtectionPolicy.ProtectFavouritesAndMissionItems)).Status);
        Assert.Empty(_station.forge!.jobs); Assert.Equal(1000, _player.credits);
    }
    [Fact]
    public void ExtractionVerifiesImmediateCargoDeliveryAndRefusesFullCargo()
    {
        var material = new RecipeResourceId("vanilla", "TestMetal", RecipeResourceKind.RefinedMaterial);
        var result = _commands.Execute(CraftingCommandRequest.Extract("test", Guid.NewGuid(), _handle, material, 5));
        Assert.Equal(CraftingCommandStatus.Succeeded, result.Status); Assert.Equal(-10, result.CreditDelta);
        Assert.Equal(5, result.Deliveries.Single().VerifiedAmount); Assert.Empty(_station.refinery.jobs);
        _player.currentSpaceShip!.cargo.Capacity = 0;
        Assert.Equal(CraftingCommandStatus.StorageUnavailable, _commands.Execute(CraftingCommandRequest.Extract("test", Guid.NewGuid(), _handle, material, 1)).Status);
        Assert.Equal(95, _player.Materials);
    }
    [Fact]
    public void ProtectedRefiningUsesExplicitExclusionAndFreshCapacityGuard()
    {
        var ore = new Behaviour.Mining.OreItemData { item = _item };
        _item.gameObject.Components[typeof(Behaviour.Mining.OreItemData)] = ore;
        var protectedRow = _station.materialStorage.items.Single(); protectedRow.favourite = true;
        var allowed = new Inventory.InventoryItem { inventory = _station.materialStorage, item = _item, count = 5 };
        _station.materialStorage.items = new[] { protectedRow, allowed };
        _station.refinery.QueueHandler = (actualOre, count, exclude) =>
        {
            Assert.True(exclude);
            var frame = _observer.Begin("jobQueueRefinery", _station.refinery, new object[] { actualOre, count, exclude });
            _player.credits -= actualOre.refinementCost * count; allowed.count -= count;
            _station.refinery.jobs.Add(new RefineJob { parent = _station.refinery, ore = actualOre, initialAmount = count, remainingAmount = count });
            _observer.End(frame, true, null); return true;
        };
        var request = CraftingCommandRequest.Queue("test", Guid.NewGuid(), _handle, new("vanilla", "refining/part"), 2, CraftingProtectionPolicy.ProtectFavourites);
        Assert.Equal(CraftingCommandStatus.Succeeded, _commands.Execute(request).Status); Assert.Equal(10, protectedRow.count); Assert.Equal(3, allowed.count);
        _station.refinery.maxJobs = 1;
        Assert.Equal(CraftingCommandStatus.QueueFull, _commands.Execute(CraftingCommandRequest.Queue("test", Guid.NewGuid(), _handle,
            request.Recipe!, 1, CraftingProtectionPolicy.ProtectFavourites)).Status);
    }
    [Fact]
    public void AutoSellIsPlayerSavePreferenceAndReadsNeverSynchronizeIt()
    {
        Refinery.autoSell = true; Assert.False(Register.HasFlag("AutoSell"));
        var read = _commands.ReadSettings(_handle.SessionId); Assert.True(read.EffectiveAutoSell); Assert.False(read.StoredAutoSellPreference);
        Assert.False(Register.HasFlag("AutoSell"));
        var changed = _commands.Execute(CraftingCommandRequest.Configure("test", Guid.NewGuid(), _handle.SessionId, CraftingSetting.PlayerAutoSell, false));
        Assert.Equal(CraftingCommandStatus.Succeeded, changed.Status); Assert.False(Refinery.autoSell); Assert.False(Register.HasFlag("AutoSell"));
    }
    public void Dispose()
    {
        _commands.Dispose(); _observer.Dispose(); _jobs.Dispose(); _hub.Dispose(); GamePlayer.current = null; Forge.current = null;
        Source.Galaxy.GalaxyMapData.current = null; SpaceStation.TestCurrent = null; Refinery.autoSell = false;
        InventoryItemType.all = Array.Empty<InventoryItemType>();
    }
    private sealed class RefineJob
    {
        public Refinery parent = null!;
        public Behaviour.Mining.OreItemData ore { get; set; } = null!;
        public int initialAmount { get; set; }
        public int remainingAmount { get; set; }
        public float refineTime { get; set; } = 3;
        public float jobProgress { get; set; }
    }
    private sealed class Job
    {
        public Forge parent = null!;
        public CraftingRecipe recipe { get; set; } = null!;
        public int initialAmount { get; set; }
        public int remainingAmount { get; set; }
        public int craftedLevel = 10;
        public float craftingTime { get; set; } = 3;
        public float jobProgress { get; set; }
    }
}
