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
