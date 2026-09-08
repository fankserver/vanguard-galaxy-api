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
        _source = new(typeof(CraftingRecipe).Assembly, (_, _) => null, value => value);
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
