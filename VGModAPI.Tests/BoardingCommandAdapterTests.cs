using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingCommandAdapterTests
{
    private sealed class Native : IBoardingCommandNativeBindings
    {
        internal readonly Dictionary<string, int> Roster = new() { ["Marine"] = 5 };
        internal readonly Dictionary<string, object?> Ship = new() { ["capacity"] = 10, ["destroyed"] = false };
        internal readonly Dictionary<string, object?> Location = new() { ["availability"] = BoardingAvailability.Available, ["shipTemplate"] = "Scout", ["shipData"] = new Dictionary<string, object?>(), ["isShipBased"] = true, ["level"] = 1, ["isEnterable"] = true };
        internal readonly Dictionary<string, object?> Unit;
        internal Dictionary<string, object?>? Operation;
        internal BoardingCommandAdapter Adapter = null!;
        internal int Notifications, Transports;
        internal bool Travel, LevelGap;
        internal Action? BeforeStart;
        internal readonly List<string> Calls = new();
        public object? Player { get; }
        public object? Manager { get; } = new object();
        internal Native()
        {
            Unit = new() { ["data"] = Location };
            Player = new Dictionary<string, object?> { ["playerShipData"] = new Dictionary<string, object?> { ["ship"] = Ship, ["crewData"] = new Dictionary<string, object?> { ["crew"] = Roster } } };
        }
        public object? Get(object? obj, string key) => obj is Dictionary<string, object?> dict && dict.TryGetValue(key, out var value) ? value : null;
        public bool ValidCrew(string id) => id == "Marine";
        public object OutcomeReason(string name) => name;
        public object CreateOptions(BoardingCrewManifest crew, BoardingCommandOptions options) => new Dictionary<string, object?> { ["assignedCrew"] = new Dictionary<string, int>(crew.Crew), ["autoMove"] = options.AutoMove };
        public void ApplyOptions(object native, BoardingCommandOptions options) { }
        public object? Call(string key, object? target, params object[] args)
        {
            Calls.Add(key);
            switch (key)
            {
                case "travel": return Travel;
                case "commandLevelGap": return LevelGap;
                case "commandGetOperation": return Operation;
                case "commandNotifyCrew": Notifications++; return null;
                case "boardingStartLocation":
                case "boardingStartShip":
                    BeforeStart?.Invoke();
                    Operation = new() { ["location"] = Location, ["boardableTarget"] = Unit, ["operationShip"] = Ship, ["options"] = args[2], ["phase"] = "Approach", ["_podsInFlight"] = 0, ["_activePods"] = new ArrayList(), ["isComplete"] = false, ["isAutonomous"] = false };
                    if (key == "boardingStartLocation") return Operation;
                    Assert.False(Adapter.RemoveAssigned(Operation, out var debit));
                    Assert.Equal(2, debit!["Marine"]); Assert.Equal(0, Notifications); Transports++;
                    Assert.False(Adapter.RemoveAssigned(Operation, out var duplicate)); Assert.Empty(duplicate!);
                    return Operation;
                case "commandAbandon": Operation!["isComplete"] = true; return null;
                default: return null;
            }
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly Native Native = new();
        internal readonly BoardingService Events;
        internal readonly BoardingObserver Observer;
        internal readonly BoardingCommandAdapter Adapter;
        internal readonly BoardingHandle Target;
        internal Fixture()
        {
            Events = new BoardingService(Hub, (_, _) => { });
            Observer = new BoardingObserver(Hub, Events, Native.Get, _ => true, error => throw error);
            var session = Hub.Begin(SessionOrigin.SaveLoad, "save"); Hub.PlayerReady(session); Hub.GameplayInitialized(session);
            Observer.Guard(() => Observer.TargetReady(Native.Unit)); Target = Events.GetTargets().Single().Handle;
            Adapter = new BoardingCommandAdapter(Native, Observer, Events, _ => true); Native.Adapter = Adapter;
        }
        internal BoardingCommandResult Start() => Adapter.Execute(Target, BoardingCommandKind.Start, new BoardingCrewManifest(new Dictionary<string, int> { ["Marine"] = 2 }), new BoardingCommandOptions(), false);
        public void Dispose() { Observer.Dispose(); Hub.Dispose(); }
    }
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void ReinforcementWithoutReceivingSimulationConservesCrew(bool ship, int inFlight)
    {
        using var f = new Fixture(); f.Native.Location["isShipBased"] = ship; Assert.True(f.Start().Admitted);
        f.Native.Operation!["_podsInFlight"] = inFlight;
        var before = f.Native.Roster["Marine"];
        var result = f.Adapter.Execute(f.Target, BoardingCommandKind.Reinforce,
            new BoardingCrewManifest(new Dictionary<string, int> { ["Marine"] = 1 }), null, false);
        Assert.Equal(BoardingCommandStatus.WrongPhase, result.Status); Assert.Equal(before, f.Native.Roster["Marine"]);
        Assert.DoesNotContain("commandReinforce", f.Native.Calls);
    }
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void LevelGapRestrictionIsInstallationOnly(bool ship, bool admitted)
    {
        using var f = new Fixture(); f.Native.Location["isShipBased"] = ship; f.Native.LevelGap = true;
        Assert.Equal(admitted, f.Start().Admitted);
    }
    [Fact]
    public void HudCancellationRevokesControllerButApiCancellationDoesNot()
    {
        using var f = new Fixture();
        using var commands = new BoardingCommandService(f.Hub, f.Events, f.Adapter, () => false);
        Assert.True(commands.AcquireControl("mod", f.Target, out var controller).Admitted);
        Assert.True(controller!.Start(new BoardingCrewManifest(new Dictionary<string, int> { ["Marine"] = 2 }), new()).Admitted);
        Assert.True(controller.CancelApproach().Admitted); Assert.True(controller.IsActive);
        f.Adapter.HudCancel(new Dictionary<string, object?> { ["hudBoardable"] = f.Native.Unit }, commands);
        Assert.False(controller.IsActive); Assert.Equal(BoardingCommandStatus.ControlConflict, controller.Resume().Status);
    }
    [Fact]
    public void WalkEntryRevalidatesDelayedCrewAndCancelsWithoutPartialDebit()
    {
        using var f = new Fixture(); f.Native.Location["isShipBased"] = false;
        Assert.True(f.Start().Admitted); Assert.Equal(5, f.Native.Roster["Marine"]);
        f.Native.Roster["Marine"] = 1;
        Assert.False(f.Adapter.BeginWalk(f.Native.Operation!, out var scope)); Assert.Null(scope);
        Assert.Equal(1, f.Native.Roster["Marine"]); Assert.Contains("commandAbandon", f.Native.Calls);
    }
    [Fact]
    public void WalkEntryDebitsOnceAndDefersNotificationUntilEntryFinishes()
    {
        using var f = new Fixture(); f.Native.Location["isShipBased"] = false; Assert.True(f.Start().Admitted);
        Assert.True(f.Adapter.BeginWalk(f.Native.Operation!, out var scope));
        Assert.False(f.Adapter.RemoveAssigned(f.Native.Operation!, out var manifest)); Assert.Equal(2, manifest!["Marine"]);
        Assert.Equal(0, f.Native.Notifications); Assert.Null(f.Adapter.EndWalk(scope, null));
        Assert.Equal(1, f.Native.Notifications); Assert.Equal(3, f.Native.Roster["Marine"]);
    }
    [Fact]
    public void InitialCrewDebitsOnceAndNotifiesAfterTransport()
    {
        using var f = new Fixture(); Assert.True(f.Start().Admitted);
        Assert.Equal(3, f.Native.Roster["Marine"]); Assert.Equal(1, f.Native.Transports); Assert.Equal(1, f.Native.Notifications);
        Assert.Equal(BoardingCommandStatus.OperationExists, f.Start().Status);
    }
    [Fact]
    public void CrewChangeInsideNativeStartIsRevalidatedBeforeDebit()
    {
        using var f = new Fixture(); f.Native.BeforeStart = () => f.Native.Roster["Marine"] = 1;
        Assert.Throws<InvalidOperationException>(() => f.Start());
        Assert.Equal(1, f.Native.Roster["Marine"]); Assert.Equal(0, f.Native.Transports);
    }
    [Fact]
    public void TravelRejectsBeforeNativeMutation()
    {
        using var f = new Fixture(); f.Native.Travel = true;
        Assert.Equal(BoardingCommandStatus.Travelling, f.Start().Status);
        Assert.DoesNotContain("boardingStartShip", f.Native.Calls); Assert.Equal(5, f.Native.Roster["Marine"]);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CancellationUsesExactOperationBeforeOrAfterLaunch(int inFlight)
    {
        using var f = new Fixture(); Assert.True(f.Start().Admitted); f.Native.Operation!["_podsInFlight"] = inFlight;
        Assert.True(f.Adapter.Execute(f.Target, BoardingCommandKind.CancelApproach, null, null, false).Admitted);
        Assert.Contains("commandAbandon", f.Native.Calls); Assert.DoesNotContain("commandCancel", f.Native.Calls);
    }
}
