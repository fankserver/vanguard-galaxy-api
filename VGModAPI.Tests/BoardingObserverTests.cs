using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingObserverTests
{
    [Fact]
    public void DataInventoryDeliveryIsDistinctAndRequiresActualNativeResult()
    {
        using var f = new Fixture(); f.Start();
        var scope = f.Observer.BeginRewards(f.Native);
        f.Observer.InventoryApplied(null, 2, f.DataInventory);
        f.Observer.InventoryApplied(new object(), 2, f.DataInventory);
        f.Observer.InventoryApplied(new object(), 3, new object());
        f.Observer.EndRewards(scope);
        var deliveries = f.Events.Where(e => e.Delivery != null).Select(e => e.Delivery!).ToArray();
        Assert.Equal(2, deliveries.Length);
        Assert.Equal(BoardingDeliveryRoute.DataInventory, deliveries[0].Route);
        Assert.Equal(BoardingDeliveryRoute.Inventory, deliveries[1].Route);
    }
    private static Dictionary<string, object?> Pod(bool player = true, string state = "Returning") => new() { ["isPlayerOwned"] = player, ["state"] = state };
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly BoardingService Service;
        internal readonly BoardingObserver Observer;
        internal readonly List<BoardingEvent> Events = new();
        internal readonly HashSet<object> Dead = new();
        internal readonly Dictionary<string, object?> Location, Unit, Native, Sim;
        internal int Faults;
        internal readonly object DataInventory = new();
        internal Fixture()
        {
            Service = new BoardingService(Hub, (_, _) => { });
            Observer = new BoardingObserver(Hub, Service, (obj, name) => ((Dictionary<string, object?>)obj)[name], obj => !Dead.Contains(obj), _ => Faults++, obj => ReferenceEquals(obj, DataInventory));
            Service.Subscribe("test", Events.Add);
            var session = Hub.Begin(SessionOrigin.SaveLoad, "save"); Hub.PlayerReady(session); Hub.GameplayInitialized(session);
            Location = new() { ["availability"] = BoardingAvailability.Available, ["shipTemplate"] = "Scout", ["shipData"] = new object(), ["faction"] = null, ["isShipBased"] = true, ["dungeonType"] = "Ship" };
            Unit = new() { ["data"] = Location };
            Sim = new()
            {
                ["awaitingPlayerExtraction"] = false, ["victoryAchieved"] = false, ["isComplete"] = false,
                ["structureIntegrity"] = 100f, ["maxStructureIntegrity"] = 100f, ["outcome"] = "InProgress",
                ["compartments"] = new List<object>(), ["friendlyUnits"] = new List<object>(), ["hostileUnits"] = new List<object>()
            };
            Native = new()
            {
                ["location"] = Location, ["boardableTarget"] = Unit, ["simulation"] = Sim,
                ["_podsInFlight"] = 0, ["isComplete"] = false, ["isAutonomous"] = false, ["phase"] = "Active", ["_activePods"] = new ArrayList(),
                ["options"] = new Dictionary<string, object?> { ["autoMove"] = true, ["assignedCrew"] = new Dictionary<string, int> { ["Marine"] = 3 } }
            };
        }
        internal void Start(bool resume = false) => Observer.Guard(() => Observer.OperationReady(Native, resume));
        internal void Signal(string method = "Tick", object? returnedPod = null) => Observer.Guard(() => Observer.OperationSignal(Native, method, returnedPod));
        public void Dispose() { Observer.Dispose(); Hub.Dispose(); }
    }
    [Fact]
    public void DuplicateStartAndUnchangedTicksDoNotReplay()
    {
        using var f = new Fixture(); f.Start(); f.Start(); f.Signal();
        Assert.Single(f.Events); Assert.Equal(BoardingEventKind.OperationStarted, f.Events[0].Kind);
        Assert.Single(f.Service.GetOperations()); Assert.Equal(0, f.Faults);
    }
    [Fact]
    public void ResumedVictoryDoesNotManufactureNewVictory()
    {
        using var f = new Fixture(); f.Sim["victoryAchieved"] = true; f.Sim["outcome"] = "FriendlyVictory";
        f.Start(true); f.Signal();
        Assert.Single(f.Events); Assert.Equal(BoardingEventKind.OperationResumed, f.Events[0].Kind);
    }
    [Fact]
    public void CompletionAndCrewReturnAreSeparate()
    {
        using var f = new Fixture(); var player = Pod(); ((ArrayList)f.Native["_activePods"]!).Add(player); f.Start();
        f.Native["isComplete"] = true; f.Sim["isComplete"] = true; f.Signal();
        Assert.Equal(BoardingPhase.ReturningCrew, Assert.Single(f.Service.GetOperations()).Phase);
        Assert.DoesNotContain(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled);
        ((ArrayList)f.Native["_activePods"]!).Clear(); f.Signal("HandlePodCrewReturned", player);
        Assert.Equal(BoardingPhase.Settled, Assert.Single(f.Service.GetOperations()).Phase);
        Assert.Single(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled);
        f.Signal("HandlePodCrewReturned"); Assert.Single(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled);
    }
    [Fact]
    public void DestroyedIdleTargetIsRetiredAndOldGenerationCannotBeQueried()
    {
        using var f = new Fixture(); f.Observer.Guard(() => f.Observer.TargetReady(f.Unit));
        var old = Assert.Single(f.Service.GetTargets()); f.Dead.Add(f.Unit); f.Observer.Poll();
        Assert.Empty(f.Service.GetTargets()); Assert.Null(f.Service.GetTarget(old.Handle));
        Assert.Contains(f.Events, e => e.Kind == BoardingEventKind.Retired);
    }
    [Fact]
    public void DiscoveryHidesUnknownRoomsAndCountsOnlyLivingCrew()
    {
        using var f = new Fixture();
        var room = new Dictionary<string, object?> { ["index"] = 0, ["type"] = "Airlock", ["state"] = "Unknown", ["isLocked"] = false, ["isDestroyed"] = false };
        ((List<object>)f.Sim["compartments"]!).Add(room);
        ((List<object>)f.Sim["friendlyUnits"]!).Add(new Dictionary<string, object?> { ["compartmentIndex"] = 0, ["state"] = "Killed", ["hp"] = 0 });
        f.Start(); Assert.Empty(Assert.Single(f.Service.GetOperations()).Compartments);
        room["state"] = "Friendly"; f.Signal();
        Assert.Equal(0, Assert.Single(Assert.Single(f.Service.GetOperations()).Compartments).FriendlyCrew);
    }
    [Fact]
    public void MissingNativeMemberDisablesObservationWithoutThrowingIntoGame()
    {
        using var f = new Fixture(); f.Native.Remove("options"); f.Start();
        Assert.Equal(1, f.Faults); Assert.Empty(f.Service.GetTargets());
        Assert.False(Assert.Single(f.Hub.Capabilities, c => c.Name == "boarding-observation").Available);
    }
    [Fact]
    public void LiveTargetAvailabilityChangesAreObservedWithoutOpeningPanel()
    {
        using var f = new Fixture(); f.Observer.Guard(() => f.Observer.TargetReady(f.Unit));
        f.Location["availability"] = BoardingAvailability.Travelling; f.Observer.Poll();
        Assert.Equal(BoardingAvailability.Travelling, Assert.Single(f.Service.GetTargets()).Availability);
        Assert.Equal(BoardingEventKind.TargetChanged, f.Events.Last().Kind);
    }
    public static object EnsureApproachOperation() => new object();
    [Fact]
    public void RestoredApproachIsResumedAndTracksLandingAndCancellation()
    {
        using var f = new Fixture(); f.Native["simulation"] = null; f.Native["phase"] = "Approach";
        Assert.Contains(BindingCatalog.Boarding, binding => binding.Name == "EnsureApproachOperation");
        VGModAPI.Patches.BoardingPatches.Observer = f.Observer;
        try
        {
            VGModAPI.Patches.BoardingPatches.Start.Finalizer(f.Native, typeof(BoardingObserverTests).GetMethod(nameof(EnsureApproachOperation))!, null);
        }
        finally { VGModAPI.Patches.BoardingPatches.Observer = null; }
        Assert.Equal(BoardingEventKind.OperationResumed, f.Events[0].Kind);
        Assert.Equal(BoardingPhase.Approaching, Assert.Single(f.Service.GetOperations()).Phase);
        f.Native["_podsInFlight"] = 1; f.Signal();
        Assert.Equal(BoardingPhase.AwaitingLanding, Assert.Single(f.Service.GetOperations()).Phase);
        f.Native["_podsInFlight"] = 0; f.Native["simulation"] = f.Sim; f.Native["phase"] = "Active"; f.Signal();
        Assert.Equal(BoardingPhase.Active, Assert.Single(f.Service.GetOperations()).Phase);
        f.Signal("ReturnCrewToShip"); f.Native["isComplete"] = true; f.Signal("MarkOperationComplete");
        Assert.Contains(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled);
    }
    [Fact]
    public void SameLocationReplacementCannotReviveTargetWhileOldCrewReturns()
    {
        using var f = new Fixture();
        var player = Pod(); ((ArrayList)f.Native["_activePods"]!).Add(player);
        f.Start(); var target = Assert.Single(f.Service.GetTargets()); var operation = Assert.Single(f.Service.GetOperations());
        f.Dead.Add(f.Unit); f.Observer.Poll(); Assert.Null(f.Service.GetTarget(target.Handle));
        var replacement = new Dictionary<string, object?> { ["data"] = f.Location };
        f.Observer.Guard(() => f.Observer.TargetReady(replacement));
        var next = Assert.Single(f.Service.GetTargets()); Assert.NotEqual(target.Handle, next.Handle);
        f.Native["isComplete"] = true; ((ArrayList)f.Native["_activePods"]!).Clear(); f.Signal("HandlePodCrewReturned", player);
        Assert.Null(f.Service.GetTarget(target.Handle)); Assert.Same(next, f.Service.GetTarget(next.Handle));
        Assert.Contains(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled && e.Operation!.Handle.Equals(operation.Handle));
        f.Observer.Poll(); Assert.Null(f.Service.GetOperation(operation.Handle));
    }
    [Fact]
    public void PoiUnloadInvalidatesActiveOperationWhenNoLiveReturnObligationRemains()
    {
        using var f = new Fixture(); var pod = Pod();
        ((ArrayList)f.Native["_activePods"]!).Add(pod); f.Start();
        var old = Assert.Single(f.Service.GetOperations()).Handle;
        f.Dead.Add(f.Unit); f.Dead.Add(pod); f.Observer.Poll();
        Assert.Empty(f.Service.GetTargets()); Assert.Empty(f.Service.GetOperations());
        f.Signal(); f.Start(); Assert.Null(f.Service.GetOperation(old));
        Assert.DoesNotContain(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled);
    }
    [Fact]
    public void LandedEnemyPodDoesNotBlockLastPlayerReturn()
    {
        using var f = new Fixture(); var pods = (ArrayList)f.Native["_activePods"]!;
        var player = Pod();
        pods.Add(player); pods.Add(Pod(false)); f.Start();
        f.Native["isComplete"] = true; pods.Remove(player); f.Signal("HandlePodCrewReturned", player);
        Assert.Equal(BoardingPhase.Settled, Assert.Single(f.Service.GetOperations()).Phase);
        Assert.Equal(1, Assert.Single(f.Service.GetOperations()).ActivePods);
        Assert.Single(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled);
        f.Observer.Poll(); Assert.Empty(f.Service.GetOperations());
    }
    [Theory]
    [InlineData("RecallAllPods", false)]
    [InlineData("ReturnDockedPodCrew", false)]
    [InlineData("RecallAllPods", true)]
    public void CancellationSettlesOnlyAfterOutstandingPlayerPodsReturn(string boundary, bool launching)
    {
        using var f = new Fixture(); f.Native["simulation"] = null; f.Native["phase"] = "Approach"; f.Start();
        var pods = (ArrayList)f.Native["_activePods"]!;
        var player = Pod(); if (launching) pods.Add(player);
        f.Signal(boundary); f.Native["isComplete"] = true; f.Signal("MarkComplete");
        Assert.Equal(launching ? BoardingPhase.ReturningCrew : BoardingPhase.Settled, Assert.Single(f.Service.GetOperations()).Phase);
        if (launching)
        {
            Assert.DoesNotContain(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled);
            pods.Clear(); f.Signal("HandlePodCrewReturned", player);
        }
        Assert.Single(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecallThenUnobservedPodLossCannotSettle(bool removedFromNativeList)
    {
        using var f = new Fixture(); var player = Pod(state: "Launching"); var pods = (ArrayList)f.Native["_activePods"]!;
        pods.Add(player); f.Native["simulation"] = null; f.Native["phase"] = "Approach"; f.Start();
        f.Signal("RecallAllPods"); f.Native["isComplete"] = true; f.Signal("MarkComplete");
        Assert.Equal(BoardingPhase.ReturningCrew, Assert.Single(f.Service.GetOperations()).Phase);
        f.Dead.Add(f.Unit); f.Dead.Add(player); if (removedFromNativeList) pods.Remove(player);
        f.Observer.Poll();
        Assert.Empty(f.Service.GetOperations());
        Assert.DoesNotContain(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled);
    }
    [Fact]
    public void PartialReturnThenUnloadCannotSettleTheMissingSurvivors()
    {
        using var f = new Fixture(); var first = Pod(); var second = Pod(); var pods = (ArrayList)f.Native["_activePods"]!;
        pods.Add(first); pods.Add(second); f.Start(); f.Native["isComplete"] = true;
        pods.Remove(first); f.Signal("HandlePodCrewReturned", first);
        f.Dead.Add(f.Unit); f.Dead.Add(second); f.Observer.Poll();
        Assert.Empty(f.Service.GetOperations());
        Assert.DoesNotContain(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled);
    }
    [Fact]
    public void DockedRecallDischargesOnlyDockedObligations()
    {
        using var f = new Fixture(); var docked = Pod(state: "Docked"); var pods = (ArrayList)f.Native["_activePods"]!;
        pods.Add(docked); f.Native["simulation"] = null; f.Native["phase"] = "Approach"; f.Start();
        f.Observer.BeforeOperation(f.Native); pods.Remove(docked); f.Dead.Add(docked); f.Signal("RecallAllPods");
        f.Native["isComplete"] = true; f.Signal("MarkComplete");
        Assert.Single(f.Events, e => e.Kind == BoardingEventKind.CrewReturnSettled);
    }
    [Fact]
    public void RewardBatchReturnWithoutApplicationDoesNotReportDelivery()
    {
        using var f = new Fixture(); f.Start();
        var scope = f.Observer.BeginRewards(f.Native); f.Observer.InventoryApplied(null, 10); f.Observer.EndRewards(scope);
        Assert.DoesNotContain(f.Events, e => e.Kind == BoardingEventKind.RewardsDelivered);
    }
    [Fact]
    public void AppliedRewardsAreScopedAndBufferedUntilBatchUnwinds()
    {
        using var f = new Fixture(); f.Start(); f.Observer.InventoryApplied(new object(), 3);
        var scope = f.Observer.BeginRewards(f.Native);
        f.Observer.InventoryApplied(new object(), 2);
        Assert.DoesNotContain(f.Events, e => e.Kind == BoardingEventKind.RewardsDelivered);
        f.Observer.EndRewards(scope); f.Observer.EndRewards(scope);
        var receipt = Assert.Single(f.Events, e => e.Kind == BoardingEventKind.RewardsDelivered);
        Assert.Equal(BoardingDeliveryRoute.Inventory, receipt.Delivery!.Route); Assert.Equal(2, receipt.Delivery.Quantity);
    }
    [Fact]
    public void CreditReceiptUsesObservedIncreaseNotItemValueOrBooleanReturn()
    {
        using var f = new Fixture(); f.Start(); var scope = f.Observer.BeginRewards(f.Native);
        f.Native["creditsBalance"] = 100L; var before = f.Observer.CreditBalance(f.Native);
        f.Observer.CreditsApplied(f.Native, before); f.Native["creditsBalance"] = 130L;
        f.Observer.CreditsApplied(f.Native, before); f.Observer.EndRewards(scope);
        Assert.Equal(30, Assert.Single(f.Events, e => e.Kind == BoardingEventKind.RewardsDelivered).Delivery!.Quantity);
    }
    [Fact]
    public void NestedRewardBatchesDoNotDoubleAttributeApplications()
    {
        using var f = new Fixture(); f.Start(); var outer = f.Observer.BeginRewards(f.Native);
        f.Observer.InventoryApplied(new object(), 1); var inner = f.Observer.BeginRewards(f.Native);
        f.Observer.InventoryApplied(new object(), 2); f.Observer.EndRewards(inner); f.Observer.EndRewards(outer);
        Assert.Equal(new[] { 2, 1 }, f.Events.Where(e => e.Delivery != null).Select(e => e.Delivery!.Quantity));
    }
    [Fact]
    public void ReloadDropsAllPriorNativeInstances()
    {
        using var f = new Fixture(); f.Start(); var handle = Assert.Single(f.Service.GetOperations()).Handle;
        f.Hub.Invalidate("reload"); f.Observer.Poll();
        var id = f.Hub.Begin(SessionOrigin.SaveLoad, "save"); f.Hub.PlayerReady(id); f.Observer.Poll();
        f.Signal(); Assert.Empty(f.Service.GetOperations()); Assert.Null(f.Service.GetOperation(handle));
    }
}
