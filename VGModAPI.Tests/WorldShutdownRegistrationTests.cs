using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldShutdownRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootShutdownReleasesWorldOwnersBeforeCoordinatorEvenWhenDeferred(bool deferred)
    {
        var directory = Path.Combine(Path.GetTempPath(), "world-shutdown-" + Guid.NewGuid().ToString("N"));
        var failures = new List<Exception>();
        try
        {
            using var hub = new LifecycleHub((_, error) => failures.Add(error));
            hub.SetCapability("session-lifecycle", true, "Test bindings.");
            hub.SetCapability("save-outcomes", true, "Test bindings.");
            using var persistence = new PersistenceService(hub, new GenerationStore(directory), Path.GetFullPath, _ => "unused");
            using var world = new WorldPersistenceBindings(persistence, hub, null!, null!, null!);
            var coordinator = (PersistenceCoordinator)typeof(PersistenceService).GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(persistence)!;
            var state = (ISaveDataRegistration)typeof(WorldPersistenceBindings).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
            bool stopped = false;
            WorldShutdownRegistration.Register(hub.Services, () =>
            {
                Assert.NotEqual(SaveDataStateKind.Disposed, coordinator.State(WorldStateCodec.Owner).Kind);
                Assert.False((bool)state.GetType().GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!);
                world.Dispose();
                Assert.True((bool)state.GetType().GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!);
                stopped = true;
            }, persistence);
            if (deferred)
            {
                hub.Changed += fact =>
                {
                    if (fact.Kind != LifecycleEventKind.SessionStarting) return;
                    hub.Dispose();
                    Assert.False(stopped);
                };
                hub.Begin(SessionOrigin.NewGame, null);
            }
            else hub.Dispose();
            Assert.True(stopped);
            Assert.Equal(SaveDataStateKind.Disposed, coordinator.State(WorldStateCodec.Owner).Kind);
            world.Dispose(); // Later OnDestroy cleanup remains harmless.
            Assert.Empty(failures);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
