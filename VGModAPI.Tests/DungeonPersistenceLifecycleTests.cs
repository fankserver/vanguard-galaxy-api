using System;
using System.Collections.Generic;
using System.IO;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
namespace VGModAPI.Tests;
public sealed class DungeonPersistenceLifecycleTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "dungeon-lifecycle-" + Guid.NewGuid().ToString("N"));
        internal readonly LifecycleHub Hub = new((_, error) => throw new InvalidOperationException("Lifecycle fault", error));
        internal readonly Dictionary<string, string> Hashes = new() { ["slot"] = new('a', 64), ["copy"] = new('b', 64) };
        internal readonly GenerationStore Store; internal readonly PersistenceService Persistence; internal readonly DungeonPodPersistence State;
        internal readonly Guid Operation = Guid.NewGuid(), Pod = Guid.NewGuid();
        private readonly float[] _livePose = new float[9];
        private string _livePhase = "Active";
        private readonly DungeonRecoveryCheckpoint _checkpoint;
        internal Fixture()
        {
            Store = new(_root); Persistence = new(Hub, Store, value => value, value => Hashes[value]); State = new(Hub, Persistence);
            Start(SessionOrigin.NewGame);
            DungeonPodPersistenceTests.TrackOperation(State, Operation);
            var crew = new Dictionary<string, int> { ["Marine"] = 3 };
            Assert.True(State.Track(new(Pod, Operation, DungeonPodPhase.Returning, true, true, false, crew, parentShipId: "ship-guid", transport: new("pod", false, crew, new float[9], "ship-guid"))));
            var live = new DungeonLiveTransportIndex(_ => true); live.Track(Pod, _livePose);
            _checkpoint = new(State, () => new object[] { Operation }, _ =>
            {
                var saved = State.Operation(Operation)!;
                return State.TrackOperation(new(saved.Id, saved.LocationId, saved.ContentOccurrence, saved.AttackerShipId, saved.DungeonType, _livePhase, saved.Outcome, saved.MissionProtection, saved.TerminalProgress, saved.Autonomous));
            }, () => { }, live, (id, pod) => Assert.True(State.RefreshTransportPose(id, (float[])pod)));
        }
        internal void Start(SessionOrigin origin, string? path = null)
        { var id = Hub.Begin(origin, path); Hub.PlayerReady(id); Hub.GameplayInitialized(id); }
        internal void Save(string slot, LifecycleEventKind result, float x)
        {
            _livePhase = x > 20 ? "Extraction" : "Active"; _livePose[0] = x;
            _checkpoint.Run();
            var id = Guid.NewGuid(); Hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, Hub.CurrentSession, id, slot));
            Assert.False(State.CanMutate);
            Hub.Publish(new LifecycleEvent(result, Hub.CurrentSession, id, slot));
        }
        public void Dispose() { State.Dispose(); Persistence.Dispose(); Hub.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    [Fact]
    public void SaveAsRollbackSlotSwitchAndNewGameUseIndependentDungeonGenerations()
    {
        using var f = new Fixture(); f.Save("slot", LifecycleEventKind.SaveSucceeded, 12);
        f.Save("copy", LifecycleEventKind.SaveSucceeded, 27);
        f.Start(SessionOrigin.SaveLoad, "slot"); Assert.Equal(12f, f.State.Get(f.Pod)!.Transport!.Pose[0]);
        f.Hashes["slot"] = new('c', 64); f.Save("slot", LifecycleEventKind.SaveSucceeded, 49);
        f.Hashes["slot"] = new('a', 64); f.Start(SessionOrigin.SaveLoad, "slot"); Assert.Equal(12f, f.State.Get(f.Pod)!.Transport!.Pose[0]);
        f.Start(SessionOrigin.SaveLoad, "copy"); Assert.Equal(27f, f.State.Get(f.Pod)!.Transport!.Pose[0]);
        Assert.Equal("Extraction", f.State.Operation(f.Operation)!.NativePhase);
        Assert.Equal(DungeonTerminalProgress.NotStarted, f.State.Operation(f.Operation)!.TerminalProgress);
        Assert.Equal(3, f.State.Get(f.Pod)!.ReturnCrew["Marine"]); Assert.False(f.State.Get(f.Pod)!.ReturnAttempted);
        f.Start(SessionOrigin.NewGame); Assert.Empty(f.State.Snapshot); Assert.Null(f.State.Operation(f.Operation));
    }
    [Theory]
    [InlineData(LifecycleEventKind.SaveFailed)]
    [InlineData(LifecycleEventKind.SaveSkipped)]
    public void UnsuccessfulSaveCannotPublishCheckpointedDungeonState(LifecycleEventKind result)
    {
        using var f = new Fixture(); f.Save("slot", LifecycleEventKind.SaveSucceeded, 12);
        f.Save("copy", result, 99);
        f.Start(SessionOrigin.SaveLoad, "slot"); Assert.Equal(12f, f.State.Get(f.Pod)!.Transport!.Pose[0]);
        Assert.Equal(3, f.State.Get(f.Pod)!.ReturnCrew["Marine"]);
        Assert.Equal("Active", f.State.Operation(f.Operation)!.NativePhase);
        Assert.False(f.State.Get(f.Pod)!.ReturnAttempted);
        if (result == LifecycleEventKind.SaveSkipped) Assert.Null(f.Store.Load("copy", f.Hashes["copy"]));
        else Assert.Throws<InvalidDataException>(() => f.Store.Load("copy", f.Hashes["copy"]));
    }
}
