using System;
using System.IO;
using UiSurfaces;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WindowVisitsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vg-window-visits-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static LifecycleHub Hub()
    {
        var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected callback failure", error));
        hub.SetAvailable("session-lifecycle", "test"); hub.SetAvailable("save-outcomes", "test");
        return hub;
    }
    private PersistenceService Persistence(LifecycleHub hub) => new(hub, new GenerationStore(_root), slot => slot, _ => new string('a', 64));
    private static void Start(LifecycleHub hub, string? slot = null)
    {
        var id = hub.Begin(slot == null ? SessionOrigin.NewGame : SessionOrigin.SaveLoad, slot);
        hub.PlayerReady(id); hub.GameplayInitialized(id);
    }
    private static void Save(LifecycleHub hub, string slot)
    {
        var operation = Guid.NewGuid();
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, operation, slot));
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSucceeded, hub.CurrentSession, operation, slot));
    }

    [Fact]
    public void CommittedProgressSurvivesProviderAndServiceRecreation()
    {
        using (var hub = Hub())
        using (var saves = Persistence(hub))
        using (var visits = new WindowVisits(saves, "example.ui"))
        {
            Assert.Null(visits.Count); Assert.False(visits.RecordOpen());
            Start(hub);
            Assert.Equal(0, visits.Count);
            Assert.True(visits.RecordOpen()); Assert.True(visits.RecordOpen());
            Save(hub, "slot-a");
            Assert.True(visits.RecordOpen()); // Unsaved change must not survive reload.
        }
        using var restoredHub = Hub();
        using var restoredSaves = Persistence(restoredHub);
        using var restored = new WindowVisits(restoredSaves, "example.ui");
        Start(restoredHub, "slot-a");
        Assert.Equal(2, restored.Count);
    }

    [Fact]
    public void NewGameAndSlotSwitchRestoreIndependentCountersWithoutReregistering()
    {
        using var hub = Hub(); using var saves = Persistence(hub);
        using var visits = new WindowVisits(saves, "example.ui");
        Start(hub); visits.RecordOpen(); Save(hub, "slot-a");
        Start(hub); Assert.Equal(0, visits.Count);
        visits.RecordOpen(); visits.RecordOpen(); Save(hub, "slot-b");
        Start(hub, "slot-a"); Assert.Equal(1, visits.Count);
        Start(hub, "slot-b"); Assert.Equal(2, visits.Count);
    }

    [Fact]
    public void SaveInFlightRefusesMutationAndFailedSaveDoesNotReplaceCommittedProgress()
    {
        using var hub = Hub(); using var saves = Persistence(hub);
        using var visits = new WindowVisits(saves, "example.ui");
        Start(hub); visits.RecordOpen(); Save(hub, "slot-a");
        visits.RecordOpen();
        var operation = Guid.NewGuid();
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, operation, "slot-a"));
        Assert.False(visits.RecordOpen());
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveFailed, hub.CurrentSession, operation, "slot-a"));
        Start(hub, "slot-a"); Assert.Equal(1, visits.Count);
    }

    [Fact]
    public void RefusedRegistrationDoesNotCreateAnUnsavedFallback()
    {
        var saves = new FakeSaveDataService { Result = SaveDataRegistrationStatus.SessionAlreadyStarted };
        Assert.Throws<InvalidOperationException>(() => new WindowVisits(saves, "example.ui"));
    }

    [Fact]
    public void InvalidPayloadOverflowAndBlockedStateNeverSilentlyResetOrIncrement()
    {
        var saves = new FakeSaveDataService();
        using var visits = new WindowVisits(saves, "example.ui");
        var session = new SessionSnapshot(Guid.NewGuid(), SessionPhase.PlayerReady, SessionOrigin.NewGame, null);
        Assert.False(saves.Provider!.Validate(new byte[3]));
        Assert.False(saves.Provider.Validate(new byte[] { 0, 0, 0, 128 }));
        Assert.Throws<ArgumentException>(() => saves.Provider.Restore(session, new byte[3]));
        saves.Provider.Restore(session, new byte[] { 255, 255, 255, 127 });
        saves.Registration.SetState(new SaveDataState(SaveDataStateKind.Ready, session.Id), read: true, mutate: true);
        Assert.Equal(int.MaxValue, visits.Count); Assert.False(visits.RecordOpen());
        saves.Registration.SetState(new SaveDataState(SaveDataStateKind.Blocked, session.Id, SaveDataBlockReason.PublicationFailed));
        Assert.Null(visits.Count); Assert.False(visits.RecordOpen());
        visits.Dispose(); visits.Dispose();
        Assert.Equal(0, saves.Registration.Listeners); Assert.Equal(1, saves.Registration.DisposeCalls);
        Assert.Null(visits.Count);
    }
}
