using System;
using System.Collections;
using System.IO;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldTravelChildEnumeratorTests
{
    private sealed class Probe : IEnumerator, IDisposable
    {
        internal int Moves;
        internal Action? Cleanup;
        public object? Current { get; set; }
        public bool MoveNext() { Moves++; return true; }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => Cleanup?.Invoke();
    }
    [Fact]
    public void NestedChildAndStaleCleanupCannotBorrowSuccessorExecution()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object();
        var route = scopes.Begin(session, player, manager, new object()); var first = scopes.First(route, new object());
        var child = new Probe(); var parent = new Probe { Current = child };
        using var wrapper = new WorldTravelChildEnumerator(parent, scopes, first, () => scopes.RequireActive(first, session, player, manager));
        Assert.True(wrapper.MoveNext()); var nested = Assert.IsType<WorldTravelChildEnumerator>(wrapper.Current);
        Assert.True(nested.MoveNext());
        var waypoint = new object(); var next = scopes.AcceptHandoff(scopes.PrepareHandoff(first, waypoint), waypoint);
        Assert.Throws<InvalidDataException>(() => nested.MoveNext()); Assert.Equal(1, child.Moves);
        scopes.RequireActive(next, session, player, manager);
        child.Cleanup = () => Assert.Throws<InvalidDataException>(() => scopes.CaptureExecuting());
        using (scopes.Enter(next))
        {
            nested.Dispose();
            Assert.Same(next, scopes.CaptureExecuting());
        }
    }
    [Fact]
    public void ReentrantVerificationReplacementStopsOldBodyWithoutCancellingNewRoute()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object(); var target = new object();
        var route = scopes.Begin(session, player, manager, target); var first = scopes.First(route, target);
        WorldTravelScopes.Leg? next = null; var inner = new Probe();
        using var wrapper = new WorldTravelChildEnumerator(inner, scopes, first, () =>
        {
            next = scopes.First(scopes.Begin(session, player, manager, target), target);
        });
        Assert.Throws<InvalidDataException>(() => wrapper.MoveNext()); Assert.Equal(0, inner.Moves);
        scopes.RequireActive(next!, session, player, manager);
    }
}
