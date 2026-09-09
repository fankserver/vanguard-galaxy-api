using System;
using System.IO;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldGenerationHookTests
{
    [Fact]
    public void CapturedHostBudgetFailureRefusesSnapshotPublication()
    {
        var previous = WorldLifetimePatches.Host;
        var hub = new LifecycleHub((_, error) => throw error);
        var guard = new WorldLifetimeGuard();
        var creation = new WorldCreationCoordinator(null!, hub.CheckThread);
        using var host = new WorldLifetimeHookHost(typeof(Source.Galaxy.MapElement).Assembly, hub, guard, generationFailure: creation.Refuse);
        var session = hub.Begin(SessionOrigin.NewGame, null); creation.Reset(session);
        Assert.True(creation.TryRestore(session, () => Array.Empty<WorldSnapshotInstance>()));
        var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
        var poi = new Source.Galaxy.MapPointOfInterest { guid = identity.NativeId };
        guard.Track(session, poi, identity); guard.Ready(session);
        WorldLifetimePatches.Generation.Capture? capture = null, descriptor = null;
        try
        {
            WorldLifetimePatches.Host = host;
            WorldLifetimePatches.Generation.Prefix(poi, out capture);
            WorldLifetimePatches.Host = null;
            for (int i = 0; i < 512; i++) WorldLifetimePatches.Generation.BuilderPrefix();
            WorldLifetimePatches.Host = host;
            WorldLifetimePatches.Generation.StaticPrefix(poi, out descriptor);
            WorldLifetimePatches.Host = null;
            for (int i = 0; i < 512; i++) WorldLifetimePatches.Generation.BuilderPrefix();
            Assert.Throws<InvalidDataException>(() => WorldLifetimePatches.Generation.BuilderPrefix());
            Assert.Throws<InvalidDataException>(() => creation.Snapshot());
            Assert.False(host.AllowUse(poi));
            Assert.IsType<InvalidDataException>(WorldLifetimePatches.Generation.Finalizer(descriptor, null));
            Assert.IsType<InvalidDataException>(WorldLifetimePatches.Generation.Finalizer(capture, null));
        }
        finally { WorldLifetimePatches.Generation.Finalizer(descriptor, null); WorldLifetimePatches.Generation.Finalizer(capture, null); WorldLifetimePatches.Host = previous; }
    }
}
