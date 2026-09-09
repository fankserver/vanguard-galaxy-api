using System;
using System.IO;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarPatronCoordinatorTests
{
    private static LifecycleHub Bound()
    {
        var hub = new LifecycleHub((_, error) => throw error);
        hub.SetCapability("session-lifecycle", true, "Bound.");
        hub.SetCapability("save-outcomes", true, "Bound.");
        return hub;
    }

    [Fact]
    public void RealCoordinatorRestoresPatronsAcrossColdServicesAndOlderSaveRollback()
    {
        string root = Path.Combine(Path.GetTempPath(), "vg-bar-coordinator-" + Guid.NewGuid().ToString("N"));
        string hash = new('a', 64);
        var row = new BarPatronState(new BarPatronId("author", "contact"), "station", "Saved name", "Description", "seed");
        try
        {
            var store = new GenerationStore(root);
            using (var hub = Bound())
            using (var persistence = new PersistenceService(hub, store, path => path, _ => hash))
            using (var patrons = new BarPatronPersistence(persistence, hub, hub.CheckThread))
            {
                var session = hub.Begin(SessionOrigin.NewGame, null);
                hub.PlayerReady(session); hub.GameplayInitialized(session);
                Assert.True(patrons.Put(session, "author", row));
                Save(hub);
                Assert.NotNull(store.Load("slot", hash));
            }
            using var laterHub = Bound();
            using var laterPersistence = new PersistenceService(laterHub, store, path => path, _ => hash);
            using var laterPatrons = new BarPatronPersistence(laterPersistence, laterHub, laterHub.CheckThread);
            var loaded = Load(laterHub);
            Assert.True(laterPatrons.Read(loaded, out var restored));
            Assert.Equal("Saved name", Assert.Single(restored).Name);
            Assert.True(laterPatrons.Remove(loaded, "author", row.Id));
            hash = new string('b', 64);
            Save(laterHub);
            hash = new string('a', 64);
            var rollback = Load(laterHub);
            Assert.False(laterPatrons.Read(loaded, out _));
            Assert.True(laterPatrons.Read(rollback, out restored));
            Assert.Equal(row.Id, Assert.Single(restored).Id);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Guid Load(LifecycleHub hub)
    {
        var session = hub.Begin(SessionOrigin.SaveLoad, "slot");
        hub.PlayerReady(session); hub.GameplayInitialized(session);
        return session;
    }
    private static void Save(LifecycleHub hub)
    {
        var operation = Guid.NewGuid();
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, operation, "slot"));
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSucceeded, hub.CurrentSession, operation, "slot"));
    }
}
