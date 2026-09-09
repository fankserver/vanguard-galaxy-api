using System;
using System.Collections.Generic;
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

public sealed class CraftingJobObserverTests : IDisposable
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly RecipeCatalogNativeSource _source;
    private readonly CraftingJobService _service;
    private readonly CraftingJobObserver _observer;
    private readonly SpaceStation _station = new();
    private readonly GamePlayer _player = new() { currentSpaceShip = new() };
    private readonly CraftingRecipe _recipe = new() { identifier = "test" };
    private readonly InventoryItemType _item = new() { identifier = "output" };
    private readonly List<CraftingJobEvent> _events = new();
    private Forge Forge => _station.forge!;
    public CraftingJobObserverTests()
    {
        var token = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(token); _hub.GameplayInitialized(token);
        GamePlayer.current = _player; _player.currentPointOfInterest = _station;
        Source.Galaxy.GalaxyMapData.current = new(); Source.Galaxy.GalaxyMapData.current.AddPoi(_station);
        _station.forge = new Forge { spaceStation = _station }; Source.Mining.Forge.current = Forge;
        _item.gameObject.Components[typeof(InventoryItemType)] = _item;
        _recipe.results.Add(new() { item = _item.gameObject, count = 1 });
        _source = new(typeof(CraftingRecipe).Assembly,
            (prefab, type) => ((UnityEngine.GameObject)prefab).Components.TryGetValue(type, out var component) ? component : null, value => value);
        _service = new(_hub, _source, (_, _) => { }); _service.SetAvailable(true);
        _observer = new(_hub, _service, _source); _service.Subscribe("test", _events.Add);
    }
    private Job Queue(int amount = 1)
    {
        var frame = _observer.Begin("jobQueueForge", Forge, new object[] { _recipe, amount });
        Assert.NotNull(frame);
        var job = new Job { parent = Forge, recipe = _recipe, initialAmount = amount, remainingAmount = amount };
        Forge.jobs.Add(job); _observer.End(frame, true, null); return job;
    }
    private Inventory.InventoryItem Add(CraftingJobObserver.Scope? frame, int count = 1)
    {
        Assert.NotNull(frame);
        var row = new Inventory.InventoryItem { item = _item, inventory = _station.materialStorage, count = count };
        _station.materialStorage.items = _station.materialStorage.items.Append(row).ToArray();
        _observer.End(frame, row, null); return row;
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void GeneratedDeliveryUsesTemplateAndRequiresExactStoredObject(bool equipment, bool replaced)
    {
        _item.identifier = "";
        if (equipment) _item.equipmentBuilder = new() { identifier = "generated-template" };
        else _item.itemBuilder = new() { identifier = "generated-template" };
        _item.gameObject.Components.Remove(typeof(InventoryItemType));
        if (equipment) _item.gameObject.Components[typeof(Behaviour.Equipment.Builder.EquipmentBuilder)] = _item.equipmentBuilder!;
        else _item.gameObject.Components[typeof(Behaviour.Item.Builder.ItemBuilder)] = _item.itemBuilder!;
        var job = Queue();
        var batch = _observer.Begin("jobBatchForge", job, Array.Empty<object>()); job.remainingAmount--;
        var route = _observer.Begin("jobRouteForge", job, new object[] { _item, 1 });
        var transfer = _observer.Begin("jobInventoryAdd", _station.materialStorage, new object[] { _item, 1, false, false });
        var row = new Inventory.InventoryItem { inventory = _station.materialStorage, count = 1,
            item = replaced ? new InventoryItemType { identifier = "", itemLevel = _item.itemLevel, rarity = _item.rarity,
                equipmentBuilder = _item.equipmentBuilder, itemBuilder = _item.itemBuilder } : _item };
        _station.materialStorage.items = new[] { row };
        _observer.End(transfer, row, null); _observer.End(route, null, null); _observer.End(batch, null, null);
        Assert.Null(_observer.PumpFault());
        var fact = _events.Last(); var delivery = Assert.Single(fact.Deliveries);
        Assert.Equal(new RecipeResourceId("vanilla", "generated-template", equipment ? RecipeResourceKind.EquipmentTemplate : RecipeResourceKind.ItemTemplate), delivery.Resource);
        Assert.Equal(replaced ? CraftingDeliveryStatus.Unresolved : CraftingDeliveryStatus.Verified, fact.DeliveryStatus);
        Assert.Equal(replaced ? (double?)null : 1d, delivery.VerifiedAmount);
    }
    [Fact]
    public void DirectQueueAndFailedAdmissionAreDistinct()
    {
        var rejected = _observer.Begin("jobQueueForge", Forge, new object[] { _recipe, 1 });
        _observer.End(rejected, false, null); Assert.Empty(_events);
        Queue(); Assert.Equal(CraftingJobEventKind.Queued, Assert.Single(_events).Kind); Assert.Null(_observer.PumpFault());
    }
    [Fact]
    public void MultipleBatchesDeliverIndividuallyThenFinish()
    {
        var job = Queue(2);
        var progress = _observer.Begin("jobProgressForge", Forge, new object[] { 10f });
        for (var i = 0; i < 2; i++)
        {
            var batch = _observer.Begin("jobBatchForge", job, Array.Empty<object>()); job.remainingAmount--;
            var route = _observer.Begin("jobRouteForge", job, new object[] { _item, 1 });
            Add(_observer.Begin("jobInventoryAdd", _station.materialStorage, new object[] { _item, 1, false, false }));
            _observer.End(route, null, null); _observer.End(batch, null, null);
        }
        Forge.jobs.Remove(job); _observer.End(progress, null, null);
        Assert.Null(_observer.PumpFault());
        Assert.Equal(new[] { CraftingJobEventKind.Queued, CraftingJobEventKind.BatchObserved, CraftingJobEventKind.BatchObserved, CraftingJobEventKind.Finished }, _events.Select(item => item.Kind));
        Assert.All(_events.Where(item => item.Kind == CraftingJobEventKind.BatchObserved), item =>
        { Assert.Equal(CraftingDeliveryStatus.Verified, item.DeliveryStatus); Assert.Equal(1, Assert.Single(item.Deliveries).VerifiedAmount); });
    }
    [Fact]
    public void NestedAddsToSameRowDoNotDoubleCount()
    {
        var job = Queue(); var batch = _observer.Begin("jobBatchForge", job, Array.Empty<object>()); job.remainingAmount--;
        var route = _observer.Begin("jobRouteForge", job, new object[] { _item, 1 });
        var outer = _observer.Begin("jobInventoryAdd", _station.materialStorage, new object[] { _item, 1, false, false });
        var row = Add(_observer.Begin("jobInventoryAdd", _station.materialStorage, new object[] { _item, 1, false, false }));
        row.count++; _observer.End(outer, row, null); _observer.End(route, null, null); _observer.End(batch, null, null);
        var fact = _events.Last(); Assert.Equal(2, fact.Deliveries.Count); Assert.Equal(2, fact.Deliveries.Sum(item => item.VerifiedAmount));
        Assert.Null(_observer.PumpFault());
    }
    [Fact]
    public void BatchExceptionKeepsPartialReceiptWithoutClaimingFinished()
    {
        var job = Queue(); var batch = _observer.Begin("jobBatchForge", job, Array.Empty<object>()); job.remainingAmount--;
        var route = _observer.Begin("jobRouteForge", job, new object[] { _item, 1 });
        Add(_observer.Begin("jobInventoryAdd", _station.materialStorage, new object[] { _item, 1, false, false }));
        _observer.End(route, null, null); _observer.End(batch, null, new InvalidOperationException("Native failure"));
        var fact = _events.Last(); Assert.Equal(CraftingDeliveryStatus.Unresolved, fact.DeliveryStatus); Assert.Single(fact.Deliveries);
        Assert.DoesNotContain(_events, item => item.Kind == CraftingJobEventKind.Finished); Assert.Null(_observer.PumpFault());
    }
    [Fact]
    public void MixedUnsupportedOutputPreventsAggregateVerification()
    {
        _recipe.results.Add(new() { item = new UnityEngine.GameObject(), count = 1 });
        var job = Queue(); var batch = _observer.Begin("jobBatchForge", job, Array.Empty<object>()); job.remainingAmount--;
        var route = _observer.Begin("jobRouteForge", job, new object[] { _item, 1 });
        Add(_observer.Begin("jobInventoryAdd", _station.materialStorage, new object[] { _item, 1, false, false }));
        _observer.End(route, null, null); _observer.End(batch, null, null);
        Assert.Equal(CraftingDeliveryStatus.Unresolved, _events.Last().DeliveryStatus);
        Assert.Equal(CraftingDeliveryStatus.Verified, Assert.Single(_events.Last().Deliveries).Status);
        Assert.Null(_observer.PumpFault());
    }
    [Fact]
    public void NestedBatchSubscriberCannotDonateAnUnrelatedReceiptToOuterJob()
    {
        var a = Queue(); var b = Queue(); var c = Queue();
        var bHandle = _events[1].Job.Handle;
        _service.Subscribe("callback-transfer", fact =>
        {
            if (fact.Kind != CraftingJobEventKind.BatchObserved || !fact.Job.Handle.Equals(bHandle)) return;
            var unrelated = _observer.Begin("jobInventoryAdd", _station.materialStorage, new object[] { _item, 1, false, false });
            Assert.Null(unrelated);
            _station.materialStorage.items = _station.materialStorage.items.Append(new Inventory.InventoryItem
                { item = _item, inventory = _station.materialStorage, count = 1 }).ToArray();
            // A genuinely new job operation inside the callback retains its own receipts.
            var cb = _observer.Begin("jobBatchForge", c, Array.Empty<object>()); c.remainingAmount--;
            var cr = _observer.Begin("jobRouteForge", c, new object[] { _item, 1 });
            Add(_observer.Begin("jobInventoryAdd", _station.materialStorage, new object[] { _item, 1, false, false }));
            _observer.End(cr, null, null); _observer.End(cb, null, null);
        });
        var ab = _observer.Begin("jobBatchForge", a, Array.Empty<object>()); a.remainingAmount--;
        var ar = _observer.Begin("jobRouteForge", a, new object[] { _item, 1 });
        var bb = _observer.Begin("jobBatchForge", b, Array.Empty<object>()); b.remainingAmount--;
        var br = _observer.Begin("jobRouteForge", b, new object[] { _item, 1 });
        Add(_observer.Begin("jobInventoryAdd", _station.materialStorage, new object[] { _item, 1, false, false }));
        _observer.End(br, null, null); _observer.End(bb, null, null);
        Add(_observer.Begin("jobInventoryAdd", _station.materialStorage, new object[] { _item, 1, false, false }));
        _observer.End(ar, null, null); _observer.End(ab, null, null);
        var batches = _events.Where(item => item.Kind == CraftingJobEventKind.BatchObserved).ToArray();
        Assert.Equal(3, batches.Length); Assert.All(batches, fact => Assert.Single(fact.Deliveries));
        Assert.Equal(3, batches.Sum(fact => fact.Deliveries.Sum(item => item.VerifiedAmount)));
        Assert.Equal(4, _station.materialStorage.items.Sum(item => item.count));
        Assert.Null(_observer.PumpFault());
    }
    [Fact]
    public void CancellationInsideProgressIsNotAlsoFinished()
    {
        var job = Queue(3); job.remainingAmount = 2;
        var progress = _observer.Begin("jobProgressForge", Forge, new object[] { 1f });
        var cancel = _observer.Begin("jobCancelForge", Forge, new object[] { job });
        Forge.jobs.Remove(job); _observer.End(cancel, null, null); _observer.End(progress, null, null);
        Assert.Equal(CraftingJobEventKind.Cancelled, _events.Last().Kind);
        Assert.Equal(2, _events.Last().Job.RemainingBatches); Assert.DoesNotContain(_events, item => item.Kind == CraftingJobEventKind.Finished);
        Assert.Null(_observer.PumpFault());
    }
    [Fact]
    public void NestedCancellationCannotResurrectAnInFlightBatchHandle()
    {
        var job = Queue(2); var handle = _events.Single().Job.Handle;
        var batch = _observer.Begin("jobBatchForge", job, Array.Empty<object>()); job.remainingAmount--;
        var cancel = _observer.Begin("jobCancelForge", Forge, new object[] { job });
        Forge.jobs.Remove(job); _observer.End(cancel, null, null); _observer.End(batch, null, null);
        Assert.Equal(handle, _events.Last().Job.Handle); Assert.Equal(CraftingJobState.Invalidated, _events.Last().Job.State);
        Assert.Empty(_service.Read(handle.Station).Jobs); Assert.Null(_observer.PumpFault());
    }
    [Theory]
    [InlineData(.3f, .35f, true)]
    [InlineData(16777216f, .25f, false)]
    public void MaterialBatchUsesPositiveNativeFloatExpectation(float before, float requested, bool fulfilled)
    {
        _player.Materials = before;
        var refinery = _station.refinery; refinery.spaceStation = _station;
        var job = new RefineJob { parent = refinery, ore = new() { item = _item } }; refinery.jobs.Add(job);
        var batch = _observer.Begin("jobBatchRefinery", job, Array.Empty<object>()); job.remainingAmount--;
        var transfer = _observer.Begin("jobMaterialAdd", _player, new object[] { RefinedMaterial.TestMetal, requested });
        _player.Materials = (float)(before + requested);
        _observer.End(transfer, null, null); _observer.End(batch, null, null);
        var fact = Assert.Single(_events);
        Assert.Equal(fulfilled ? CraftingDeliveryStatus.Verified : CraftingDeliveryStatus.Unresolved, fact.DeliveryStatus);
        Assert.Equal((double)(float)(before + requested) - before, Assert.Single(fact.Deliveries).VerifiedAmount);
        Assert.Null(_observer.PumpFault());
    }
    [Fact]
    public void RefineryMaterialDeltasExcludeNestedAdds()
    {
        var refinery = _station.refinery; refinery.spaceStation = _station;
        var job = new RefineJob { parent = refinery, ore = new() { item = _item } }; refinery.jobs.Add(job);
        var batch = _observer.Begin("jobBatchRefinery", job, Array.Empty<object>()); job.remainingAmount--;
        var outer = _observer.Begin("jobMaterialAdd", _player, new object[] { RefinedMaterial.TestMetal, .25f });
        var inner = _observer.Begin("jobMaterialAdd", _player, new object[] { RefinedMaterial.TestMetal, .25f });
        _player.Materials += .25f; _observer.End(inner, null, null);
        _player.Materials += .25f; _observer.End(outer, null, null); _observer.End(batch, null, null);
        var fact = Assert.Single(_events); Assert.Equal(CraftingDeliveryStatus.Verified, fact.DeliveryStatus);
        Assert.Equal(.5, fact.Deliveries.Sum(item => item.VerifiedAmount)); Assert.Null(_observer.PumpFault());
    }
    [Fact]
    public void ReloadedPartialProgressHasNewSessionHandleWithoutQueueReplay()
    {
        var job = Queue(5); job.remainingAmount = 2; job.jobProgress = .5f;
        var old = _source.CurrentStation(_hub.CurrentSession!.Id)!;
        var before = Assert.Single(_service.Read(old).Jobs);
        var token = _hub.Begin(SessionOrigin.SaveLoad, "save-as"); _hub.PlayerReady(token); _hub.GameplayInitialized(token);
        Forge.jobs.Clear(); Forge.jobs.Add(new Job { parent = Forge, recipe = _recipe, initialAmount = 5, remainingAmount = 2, jobProgress = .5f });
        var restored = Assert.Single(_service.Read(_source.CurrentStation(_hub.CurrentSession!.Id)!).Jobs);
        Assert.NotEqual(before.Handle, restored.Handle); Assert.Equal(before.RemainingBatches, restored.RemainingBatches);
        Assert.Equal(before.ProgressRatio, restored.ProgressRatio); Assert.Equal(before.CraftedLevel, restored.CraftedLevel);
        Assert.Single(_events, item => item.Kind == CraftingJobEventKind.Queued);
    }
    [Fact]
    public void MissingDefinitionAndNullRowsDoNotDisappearAsEmptySuccess()
    {
        Forge.jobs.Add(new Job { parent = Forge, recipe = null!, initialAmount = 1, remainingAmount = 1 });
        var station = _source.CurrentStation(_hub.CurrentSession!.Id)!;
        Assert.Equal(CraftingJobQueryStatus.NativeFailure, _service.Read(station).Status);
        Forge.jobs.Clear(); Forge.jobs.Add(null!);
        Assert.Equal(CraftingJobQueryStatus.NativeFailure, _service.Read(station).Status);
    }
    [Fact]
    public void SessionReplacementDiscardsInFlightNativeScopes()
    {
        var job = Queue(); var frame = _observer.Begin("jobBatchForge", job, Array.Empty<object>());
        _hub.Begin(SessionOrigin.SaveLoad, "replacement"); _observer.End(frame, null, null);
        Assert.DoesNotContain(_events, item => item.Kind == CraftingJobEventKind.BatchObserved); Assert.Null(_observer.PumpFault());
    }
    public void Dispose()
    {
        _observer.Dispose(); _service.Dispose(); _hub.Dispose(); GamePlayer.current = null; Source.Mining.Forge.current = null;
        Source.Galaxy.GalaxyMapData.current = null; SpaceStation.TestCurrent = null;
    }
    private sealed class RefineJob
    {
        public Refinery parent = null!;
        public Behaviour.Mining.OreItemData ore { get; set; } = null!;
        public int initialAmount { get; set; } = 1;
        public int remainingAmount { get; set; } = 1;
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
