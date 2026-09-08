using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class CraftingJobServiceTests : IDisposable
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly Source _source = new();
    private readonly CraftingJobService _service;
    private readonly RecipeStationHandle _station;
    private readonly List<CraftingJobEvent> _events = new();
    private int _faults;
    public CraftingJobServiceTests()
    {
        var token = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(token); _hub.GameplayInitialized(token);
        _station = new(_hub.CurrentSession!.Id, Guid.NewGuid(), "Station");
        _service = new(_hub, _source, (_, _) => _faults++); _service.SetAvailable(true);
        _service.Subscribe("test", _events.Add);
    }
    private CraftingJobSnapshot Job(int remaining = 5) => new(new(_station, Guid.NewGuid()), new("vanilla", "forge/test"),
        RecipeProcess.Forge, CraftingJobState.Active, 5, remaining, 20, .3, 4);
    private void Emit(CraftingJobSnapshot job, CraftingJobEventKind kind = CraftingJobEventKind.Queued) =>
        _service.Observe(kind, job, Array.Empty<CraftingDeliverySnapshot>(), CraftingDeliveryStatus.NotApplicable, "Observed");
    [Fact]
    public void RestoredPartialJobsAreQueryableWithoutQueueReplay()
    {
        var job = Job(3); _source.Rows.Add(job);
        var result = _service.Read(_station);
        Assert.Equal(CraftingJobQueryStatus.Available, result.Status); Assert.Same(job, Assert.Single(result.Jobs));
        Assert.Empty(_events); Assert.Equal(3, result.Jobs[0].RemainingBatches); Assert.Equal(20, result.Jobs[0].CraftedLevel);
        _service.Read(_station); Assert.Empty(_events);
    }
    [Fact]
    public void QueueIsDeduplicatedButEachBatchFactRemainsDistinct()
    {
        var job = Job(); Emit(job); Emit(job);
        Emit(job, CraftingJobEventKind.BatchObserved); Emit(job, CraftingJobEventKind.BatchObserved);
        Assert.Equal(new[] { CraftingJobEventKind.Queued, CraftingJobEventKind.BatchObserved, CraftingJobEventKind.BatchObserved }, _events.Select(item => item.Kind));
        Assert.Equal(new long[] { 1, 2, 3 }, _events.Select(item => item.Sequence));
    }
    [Fact]
    public void MissingJobIsInvalidatedWithoutInventingFinishOrCancellation()
    {
        var job = Job(2); _source.Rows.Add(job); _service.Read(_station); _source.Rows.Clear();
        Assert.Empty(_service.Read(_station).Jobs);
        var fact = Assert.Single(_events); Assert.Equal(CraftingJobEventKind.Invalidated, fact.Kind);
        Assert.Equal(CraftingJobState.Invalidated, fact.Job.State); Assert.Equal(2, fact.Job.RemainingBatches);
        Assert.Contains(job.Handle, _source.Forgotten);
    }
    [Fact]
    public void SubscribersCanDisposeOthersAndFailWithoutBreakingDispatch()
    {
        IDisposable? second = null;
        _service.Subscribe("first", _ => { second!.Dispose(); throw new InvalidOperationException(); });
        var calls = 0; second = _service.Subscribe("second", _ => calls++);
        Emit(Job()); Assert.Equal(0, calls); Assert.Equal(1, _faults); Assert.Single(_events);
    }
    [Fact]
    public void ReentrantSessionReplacementRetainsInvalidationAndStopsStaleFactDelivery()
    {
        var later = new List<CraftingJobEventKind>();
        _service.Subscribe("replace", fact =>
        {
            if (fact.Kind != CraftingJobEventKind.Queued) return;
            var token = _hub.Begin(SessionOrigin.SaveLoad, "other"); _hub.PlayerReady(token); _hub.GameplayInitialized(token);
        });
        _service.Subscribe("later", fact => later.Add(fact.Kind));
        Emit(Job());
        Assert.Equal(new[] { CraftingJobEventKind.Invalidated }, later);
        Assert.Equal(CraftingJobQueryStatus.StaleHandle, _service.Read(_station).Status);
    }
    [Fact]
    public void NativeErrorsAndBoundsNeverReturnEmptySuccess()
    {
        _source.Error = new InvalidOperationException(); Assert.Equal(CraftingJobQueryStatus.NativeFailure, _service.Read(_station).Status);
        _source.Error = new RecipeCatalogLimitException(); Assert.Equal(CraftingJobQueryStatus.LimitExceeded, _service.Read(_station).Status);
    }
    [Fact]
    public void TerminalJobIsNotInvalidatedAgainOnSessionChange()
    {
        var job = Job(0); Emit(job);
        Emit(CraftingJobService.WithState(job, CraftingJobState.Finished), CraftingJobEventKind.Finished);
        _hub.Begin(SessionOrigin.NewGame, null);
        Assert.DoesNotContain(_events, item => item.Kind == CraftingJobEventKind.Invalidated);
    }
    [Fact]
    public void MainThreadAndDisposalRulesApplyToQueriesAndSubscriptions()
    {
        Exception? error = null;
        var worker = new Thread(() => error = Record.Exception(() => _service.Read(_station)));
        worker.Start(); worker.Join(); Assert.IsType<InvalidOperationException>(error);
        _service.Dispose(); Assert.Equal(CraftingJobQueryStatus.IntegrationUnavailable, _service.Read(_station).Status);
        Assert.Throws<ObjectDisposedException>(() => _service.Subscribe("test", _ => { })); Emit(Job()); Assert.Empty(_events);
    }
    public void Dispose() { _service.Dispose(); _hub.Dispose(); }
    private sealed class Source : ICraftingJobSource
    {
        internal readonly List<CraftingJobSnapshot> Rows = new();
        internal readonly List<CraftingJobHandle> Forgotten = new();
        internal Exception? Error;
        public CraftingJobListSnapshot ReadJobs(RecipeStationHandle station)
        {
            if (Error != null) throw Error;
            return new(CraftingJobQueryStatus.Available, "Read", Rows);
        }
        public void ForgetJob(CraftingJobHandle job) => Forgotten.Add(job);
        public void InvalidateJobs() => Rows.Clear();
    }
}
