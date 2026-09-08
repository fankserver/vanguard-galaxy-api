using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingTacticalAdapterTests
{
    private sealed class Native : IBoardingTacticalNativeBindings
    {
        internal readonly Dictionary<string, object?> Room = new() { ["state"] = "Friendly", ["adjacent"] = new ArrayList { 1 } };
        internal readonly Dictionary<string, object?> Neighbor = new() { ["state"] = "Unknown", ["adjacent"] = new ArrayList { 0 } };
        internal readonly Dictionary<string, object?> Unit = new() { ["friendly"] = true, ["alive"] = true, ["compartmentIndex"] = 0, ["directiveTarget"] = -1 };
        internal readonly Dictionary<string, object?> Sim;
        internal bool Candidate;
        public object? Player { get; } = new Dictionary<string, object?> { ["credits"] = 100L };
        public object? Manager => null;
        internal Native() => Sim = new() { ["grenades"] = 1, ["buyoutCost"] = 10, ["buyoutPending"] = true, ["compartments"] = new ArrayList { Room, Neighbor }, ["friendlyUnits"] = new ArrayList { Unit } };
        public object? Get(object? obj, string key) => obj is Dictionary<string, object?> dict && dict.TryGetValue(key, out var value) ? value : null;
        public object? Call(string key, object? obj, params object[] args) => key switch
        {
            "tacticalCooldown" or "tacticalUnlockTime" => 0f,
            "tacticalEligible" or "tacticalCapacity" => 3,
            "tacticalCandidate" => Candidate ? Unit : null,
            "tacticalAlive" => Get(obj, "alive") is true,
            "tacticalSpecialist" or "tacticalCanBarricade" or "tacticalCanGrenade" => true,
            "tacticalBarricadeHeld" => false,
            _ => throw new InvalidOperationException(key)
        };
        public void Set(object obj, string key, object? value) => ((Dictionary<string, object?>)obj)[key] = value;
        public object EnumArgument(string key, int index, string name) => name;
        public object OutcomeReason(string name) => name;
        public bool ValidCrew(string id) => true;
        public object CreateOptions(BoardingCrewManifest crew, BoardingCommandOptions options) => throw new NotSupportedException();
        public void ApplyOptions(object native, BoardingCommandOptions options) => throw new NotSupportedException();
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly Native Native = new();
        internal readonly BoardingTacticalAdapter Adapter;
        internal Fixture()
        {
            var session = Hub.Begin(SessionOrigin.SaveLoad, "save"); Hub.PlayerReady(session); Hub.GameplayInitialized(session);
            Adapter = new BoardingTacticalAdapter(Hub, Native, null!, null!, null!);
        }
        public void Dispose() => Hub.Dispose();
    }
    [Fact]
    public void NativeMovementCannotClearOrdersForInvalidRoomButCanExploreAdjacentRoom()
    {
        using var f = new Fixture();
        Assert.False(f.Adapter.ValidateNative(f.Native.Sim, "IssueMovementOrder", new object[] { -1, "Any", 1 }));
        Assert.False(f.Adapter.ValidateNative(f.Native.Sim, "IssueMovementOrder", new object[] { 99, "Any", 1 }));
        Assert.True(f.Adapter.ValidateNative(f.Native.Sim, "IssueMovementOrder", new object[] { 1, "Any", 1 }));
        Assert.False(f.Adapter.ValidateNative(f.Native.Sim, "IssueMovementOrder", new object[] { 1, "Any", 4 }));
    }
    [Fact]
    public void NativeUnlockValidatesTheSuppliedUnitRatherThanAnotherSpecialist()
    {
        using var f = new Fixture(); f.Native.Neighbor["isLocked"] = true;
        Assert.True(f.Adapter.ValidateNative(f.Native.Sim, "TryUnlockCompartment", new object[] { 1, f.Native.Unit }));
        var foreign = new Dictionary<string, object?>(f.Native.Unit);
        Assert.False(f.Adapter.ValidateNative(f.Native.Sim, "TryUnlockCompartment", new object[] { 1, foreign }));
        f.Native.Unit["alive"] = false;
        Assert.False(f.Adapter.ValidateNative(f.Native.Sim, "TryUnlockCompartment", new object[] { 1, f.Native.Unit }));
    }
    [Fact]
    public void NativeDirectMovementPreservesOversizedGroupPartialAdmission()
    {
        using var f = new Fixture(); var units = new ArrayList();
        for (var i = 0; i < 5; i++) units.Add(new Dictionary<string, object?>(f.Native.Unit));
        f.Native.Sim["friendlyUnits"] = units;
        // Native capacity query returns three; all five form a valid group and native selects the fitting subset.
        Assert.True(f.Adapter.ValidateNative(f.Native.Sim, "MoveCrewTo", new object[] { units, 1 }));
    }
    [Fact]
    public void SnapshotResolvesRequestedOperationRatherThanTargetsNewestOperation()
    {
        using var f = new Fixture(); using var events = new BoardingService(f.Hub, (_, _) => { });
        using var observer = new BoardingObserver(f.Hub, events, f.Native.Get, _ => true, error => throw error);
        var location = new Dictionary<string, object?> { ["availability"] = BoardingAvailability.Available, ["shipTemplate"] = "Scout", ["isShipBased"] = true };
        var unit = new Dictionary<string, object?> { ["data"] = location };
        Dictionary<string, object?> Operation(object sim) => new()
        {
            ["location"] = location, ["boardableTarget"] = unit, ["simulation"] = sim, ["phase"] = "Active", ["isComplete"] = false,
            ["_podsInFlight"] = 0, ["isAutonomous"] = false, ["_activePods"] = new ArrayList(),
            ["options"] = new Dictionary<string, object?> { ["autoMove"] = false, ["assignedCrew"] = new Dictionary<string, int>() }
        };
        var oldSim = new Dictionary<string, object?>(f.Native.Sim) { ["grenades"] = 2, ["compartments"] = new ArrayList(), ["friendlyUnits"] = new ArrayList(), ["hostileUnits"] = new ArrayList(), ["structureIntegrity"] = 100f, ["maxStructureIntegrity"] = 100f, ["outcome"] = "InProgress", ["awaitingPlayerExtraction"] = false, ["victoryAchieved"] = false, ["isComplete"] = false };
        var nextSim = new Dictionary<string, object?>(oldSim) { ["grenades"] = 7 };
        var oldOp = Operation(oldSim); observer.Guard(() => observer.OperationReady(oldOp, false));
        var oldHandle = events.GetOperations()[0].Handle;
        var nextOp = Operation(nextSim); observer.Guard(() => observer.OperationReady(nextOp, false));
        var nextHandle = events.GetTargets()[0].Operation!;
        var adapter = new BoardingTacticalAdapter(f.Hub, f.Native, observer, events, null!);
        Assert.Equal(2, adapter.GetSnapshot(oldHandle)!.GrenadeCharges);
        Assert.Equal(7, adapter.GetSnapshot(nextHandle)!.GrenadeCharges);
        Assert.Same(oldOp, observer.ResolveCommandOperation(oldHandle));
    }
    [Fact]
    public void AutonomousDirectMovementRejectsForeignDuplicateAndInvalidOriginUnits()
    {
        using var f = new Fixture();
        Assert.True(f.Adapter.ValidateNative(f.Native.Sim, "MoveCrewTo", new object[] { new ArrayList { f.Native.Unit }, 1 }));
        Assert.False(f.Adapter.ValidateNative(f.Native.Sim, "MoveCrewTo", new object[] { new ArrayList { f.Native.Unit, f.Native.Unit }, 1 }));
        Assert.False(f.Adapter.ValidateNative(f.Native.Sim, "MoveCrewTo", new object[] { new ArrayList { new Dictionary<string, object?>(f.Native.Unit) }, 1 }));
        f.Native.Unit["compartmentIndex"] = -1;
        Assert.False(f.Adapter.ValidateNative(f.Native.Sim, "MoveCrewTo", new object[] { new ArrayList { f.Native.Unit }, 1 }));
    }
    [Fact]
    public void NativeBuyoutRequiresCandidateAndGrenadeRequiresCharges()
    {
        using var f = new Fixture(); Assert.False(f.Adapter.ValidateNative(f.Native.Sim, "AcceptBuyOut", Array.Empty<object>()));
        f.Native.Candidate = true; Assert.True(f.Adapter.ValidateNative(f.Native.Sim, "AcceptBuyOut", Array.Empty<object>()));
        Assert.True(f.Adapter.ValidateNative(f.Native.Sim, "ThrowGrenade", new object[] { 0 }));
        f.Native.Sim["grenades"] = 0; Assert.False(f.Adapter.ValidateNative(f.Native.Sim, "ThrowGrenade", new object[] { 0 }));
        f.Native.Sim["isComplete"] = true; Assert.False(f.Adapter.ValidateNative(f.Native.Sim, "AcceptBuyOut", Array.Empty<object>()));
    }
}
