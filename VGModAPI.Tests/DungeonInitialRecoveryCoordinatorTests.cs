using System;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;
namespace VGModAPI.Tests;
public sealed class DungeonInitialRecoveryCoordinatorTests
{
    private sealed class Instance : IDungeonReturnInstance
    {
        private readonly List<string> _calls;
        internal Instance(List<string> calls) => _calls = calls;
        public bool Alive => true;
        public void Activate() => _calls.Add("activate");
        public void Dispose() => _calls.Add("dispose");
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly DungeonPodPersistenceTests.Persistence Persistence = new();
        internal readonly DungeonPodPersistence State;
        internal readonly DungeonInitialRecoveryCoordinator Queue;
        internal readonly DungeonInitialRecoveryPorts Ports;
        internal readonly NativeObject Location = new(), Operation = new();
        internal readonly List<string> Calls = new();
        internal readonly Guid Id = Guid.NewGuid();
        internal bool LiveLocation = true, Hydrated = true;
        internal Fixture(bool ship, DungeonPodPhase? phase = null, string savedPhase = "Active")
        {
            State = new(Hub, Persistence); var session = Hub.Begin(SessionOrigin.SaveLoad, "save"); Hub.PlayerReady(session); Persistence.Provider.Restore(Hub.CurrentSession!, null);
            var native = new DungeonLayoutBuilderTests.Native(); var identity = new DungeonOperationResumeAdapter(State, native); var pods = new DungeonPodResumeAdapter(State, native);
            Location.Fields["dungeonType"] = "Station";
            var data = new NativeObject(); data.Fields["savedSimulation"] = new object(); Location.Fields["dungeonData"] = data;
            var recipient = new NativeObject(); recipient.Fields["resumeShipGuid"] = "ship"; var vessel = new NativeObject(); vessel.Fields["resumeShipData"] = recipient;
            Operation.Fields["location"] = Location; Operation.Fields["operationShip"] = vessel; Operation.Fields["isAutonomous"] = false;
            Operation.Fields["restoreDocking"] = (Action)(() => Calls.Add("dock"));
            State.TrackOperation(new(Id, Guid.NewGuid(), null, "ship", "Station", savedPhase, "", "", DungeonTerminalProgress.NotStarted, false,
                walkReturn: savedPhase == "Extraction" ? new DungeonWalkReturnState(new Dictionary<string, int>()) : null));
            if (savedPhase == "Extraction") using (var terminal = State.BeginTerminal(Id)) terminal!.Completed();
            identity.LoadedLocation(Location, Id);
            if (phase.HasValue)
                State.Track(new(Guid.NewGuid(), Id, phase.Value, true, false, false, new Dictionary<string, int>(), parentShipId: "ship", transport: new("native-pod", phase == DungeonPodPhase.Docked, new Dictionary<string, int> { ["Marine"] = 1 }, new float[9], "donor")));
            Ports = new()
            {
                ContainsWalkLocation = value => LiveLocation && ReferenceEquals(value, Location), IsLiveTarget = _ => true, HasLivePod = _ => false,
                Resolve = _ => vessel, SimulationReady = _ => Hydrated,
                Create = (_, _, _, _, active) => { Assert.Equal(savedPhase != "Approach", active); Calls.Add("create"); return Operation; },
                ValidateOperation = _ => { Calls.Add("validate"); return true; },
                BuildPod = (_, _, _, _, _) => { Calls.Add("build"); return new Instance(Calls); }, BindPod = (_, _) => Calls.Add("bind"),
                Register = _ => Calls.Add("register"), Observe = _ => Calls.Add("observe"), Quarantine = (_, _) => Calls.Add("quarantine")
            };
            Queue = new(State, identity, pods, native, Ports, _ => Calls.Add("error")); Assert.True(Queue.Queue(Location, ship ? new object() : null));
        }
        public void Dispose() { Queue.Dispose(); State.Dispose(); Hub.Dispose(); }
    }
    [Theory]
    [InlineData((int)DungeonPodPhase.Docked)]
    [InlineData((int)DungeonPodPhase.Launching)]
    [InlineData((int)DungeonPodPhase.Attached)]
    public void ShipQueueSeedsEveryPodBeforePublicationAndActivation(int phase)
    {
        using var f = new Fixture(true, (DungeonPodPhase)phase); f.Queue.Poll(); f.Queue.Poll();
        Assert.Equal(new[] { "create", "validate", "build", "bind", "register", "observe", "activate" }, f.Calls);
    }
    [Fact]
    public void UnloadedLocationCannotUseReadyRecipientOrCurrentDockingFacilities()
    {
        using var f = new Fixture(false); f.LiveLocation = false; f.Queue.Poll(); Assert.Empty(f.Calls);
        f.LiveLocation = true; f.Hydrated = false; f.Queue.Poll(); Assert.Empty(f.Calls);
        f.Hydrated = true; f.Queue.Poll();
        Assert.Equal(new[] { "create", "validate", "dock", "register", "observe" }, f.Calls);
    }
    [Theory]
    [InlineData("Approach")]
    [InlineData("Extraction")]
    public void LocationQueueSelectsSavedPhaseAndDoesNotReplayTerminalEffects(string phase)
    {
        using var f = new Fixture(false, savedPhase: phase); f.Queue.Poll(); f.Queue.Poll();
        Assert.Equal(new[] { "create", "validate", "dock", "register", "observe" }, f.Calls);
        if (phase == "Extraction") Assert.Null(f.State.BeginTerminal(f.Id));
    }
    [Fact]
    public void QuarantineFailureDoesNotPreventPodCleanup()
    {
        using var f = new Fixture(true, DungeonPodPhase.Docked);
        f.Ports.Observe = _ => throw new InvalidOperationException("observe");
        f.Ports.Quarantine = (_, _) => throw new InvalidOperationException("quarantine");
        f.Queue.Poll(); Assert.Contains("dispose", f.Calls); Assert.DoesNotContain("activate", f.Calls);
    }
    [Fact]
    public void RecursivePollCannotConstructOrPublishTwice()
    {
        using var f = new Fixture(true, DungeonPodPhase.Docked); var create = f.Ports.Create;
        f.Ports.Create = (saved, ship, location, target, active) => { f.Queue.Poll(); return create(saved, ship, location, target, active); };
        f.Queue.Poll();
        Assert.Equal(new[] { "create", "validate", "build", "bind", "register", "observe", "activate" }, f.Calls);
    }
    [Theory]
    [InlineData("restore")]
    [InlineData("clear")]
    [InlineData("readonly")]
    public void PublicationInvalidationNeverActivatesStalePods(string change)
    {
        using var f = new Fixture(true, DungeonPodPhase.Docked);
        f.Ports.Observe = _ =>
        {
            f.Calls.Add("observe");
            if (change == "restore") f.Persistence.Provider.Restore(f.Hub.CurrentSession!, null);
            else if (change == "clear") f.Queue.Clear();
            else f.Persistence.MutationAllowed = false;
        };
        f.Queue.Poll();
        Assert.DoesNotContain("activate", f.Calls); Assert.Contains("dispose", f.Calls); Assert.Contains("quarantine", f.Calls);
    }
    [Fact]
    public void FailureAfterRegistrationDisposesPodsAndDoesNotRetryOrActivate()
    {
        using var f = new Fixture(true, DungeonPodPhase.Docked);
        f.Ports.Observe = _ => throw new InvalidOperationException("publication failed"); f.Queue.Poll(); f.Queue.Poll();
        Assert.Equal(new[] { "create", "validate", "build", "bind", "register", "quarantine", "dispose", "error" }, f.Calls);
    }
}
