using System;
using System.Collections;
using System.IO;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldManagerEnumeratorTests
{
    private sealed class Probe : IEnumerator, IDisposable
    {
        internal int Moves, Disposals;
        internal Exception? Failure;
        public object? Current { get; set; }
        public bool MoveNext() { Moves++; if (Failure != null) throw Failure; return true; }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => Disposals++;
    }
    [Fact]
    public void NestedContinuationKeepsOriginalGuardAndDisposesAfterRefusal()
    {
        var hub = new LifecycleHub((_, error) => throw error);
        using var host = new WorldLifetimeHookHost(typeof(Source.Galaxy.MapElement).Assembly, hub);
        hub.Begin(SessionOrigin.NewGame, null);
        var manager = new Behaviour.Managers.TestPoiManager { poi = new Source.Galaxy.MapPointOfInterest { guid = "vanilla" } };
        var child = new Probe(); var parent = new Probe { Current = child };
        IEnumerator wrapped = parent;
        try
        {
            WorldLifetimePatches.Host = host;
            WorldLifetimePatches.Initialization.Postfix(manager, ref wrapped);
            WorldLifetimePatches.Host = null;
            Assert.True(wrapped.MoveNext());
            var nested = Assert.IsType<WorldManagerEnumerator>(wrapped.Current);
            Assert.True(nested.MoveNext());
            manager.poi = new Source.Galaxy.MapPointOfInterest { guid = WorldObjectIdentity.ReservedPrefix + "unknown" };
            Assert.Throws<InvalidDataException>(() => nested.MoveNext()); Assert.Equal(1, child.Moves);
            Assert.False(nested.MoveNext());
            nested.Dispose(); nested.Dispose(); Assert.Equal(1, child.Disposals);
            Assert.Throws<InvalidDataException>(() => wrapped.MoveNext()); Assert.Equal(1, parent.Moves);
            ((IDisposable)wrapped).Dispose(); Assert.Equal(1, parent.Disposals);
        }
        finally { WorldLifetimePatches.Host = null; }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuspendedOwnedWorkCannotFollowManagerRebindingOrAReplacementSession(bool newSession)
    {
        var hub = new LifecycleHub((_, error) => throw error);
        var guard = new WorldLifetimeGuard();
        using var host = new WorldLifetimeHookHost(typeof(Source.Galaxy.MapElement).Assembly, hub, guard);
        var session = hub.Begin(SessionOrigin.NewGame, null);
        var identity = new WorldObjectIdentity(new ContentDeclaration("author.one", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
        var poi = new Source.Galaxy.MapPointOfInterest { guid = identity.NativeId };
        guard.Track(session, poi, identity); guard.Ready(session);
        var manager = new Behaviour.Managers.TestPoiManager { poi = poi };
        var child = new Probe(); var parent = new Probe { Current = child };
        using var wrapper = new WorldManagerEnumerator(parent, manager, host);
        Assert.True(wrapper.MoveNext());
        var nested = Assert.IsType<WorldManagerEnumerator>(wrapper.Current);
        Assert.True(nested.MoveNext());
        if (newSession) hub.Begin(SessionOrigin.NewGame, null);
        manager.poi = new Source.Galaxy.MapPointOfInterest { guid = "replacement-vanilla" };
        Assert.True(host.AllowManager(manager));
        Assert.Throws<InvalidDataException>(() => nested.MoveNext());
        Assert.Throws<InvalidDataException>(() => wrapper.MoveNext());
        Assert.Equal(1, child.Moves); Assert.Equal(1, parent.Moves);
        nested.Dispose(); Assert.Equal(1, child.Disposals);
    }

    [Fact]
    public void YieldPredicateCannotPollAfterOriginRevocation()
    {
        bool ready = true;
        var predicate = new Probe();
        var parent = new Probe { Current = predicate };
        using var wrapper = new WorldManagerEnumerator(parent, () => ready);
        Assert.True(wrapper.MoveNext());
        var polling = Assert.IsType<WorldManagerEnumerator>(wrapper.Current);
        Assert.True(polling.MoveNext()); Assert.Equal(1, predicate.Moves);
        ready = false;
        Assert.Throws<InvalidDataException>(() => polling.MoveNext());
        Assert.Equal(1, predicate.Moves);
        Assert.False(polling.MoveNext());
    }

    [Fact]
    public void NativeIteratorExceptionIsPreserved()
    {
        var hub = new LifecycleHub((_, error) => throw error);
        using var host = new WorldLifetimeHookHost(typeof(Source.Galaxy.MapElement).Assembly, hub);
        var manager = new Behaviour.Managers.TestPoiManager { poi = new Source.Galaxy.MapPointOfInterest { guid = "vanilla" } };
        var failure = new InvalidOperationException("native failure");
        var inner = new Probe { Failure = failure };
        using var wrapper = new WorldManagerEnumerator(inner, manager, host);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => wrapper.MoveNext()));
        Assert.False(wrapper.MoveNext()); Assert.Equal(1, inner.Moves);
    }
}
