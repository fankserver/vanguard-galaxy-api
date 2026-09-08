using System;
using System.IO;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldTravelScopesTests
{
    [Fact]
    public void MultiHopAllowsSuccessorButOnlyBookkeepingRetirementForPredecessor()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object();
        var route = scopes.Begin(session, player, manager, new object());
        var first = scopes.First(route, new object()); var waypoint = new object();
        var handoff = scopes.PrepareHandoff(first, waypoint);
        var next = scopes.AcceptHandoff(handoff, waypoint);
        Assert.Throws<InvalidDataException>(() => scopes.RequireActive(first, session, player, manager));
        scopes.RequireActive(next, session, player, manager);
        scopes.RetirePredecessor(first);
        Assert.Throws<InvalidDataException>(() => scopes.RetirePredecessor(first));
        Assert.Throws<InvalidDataException>(() => scopes.AcceptHandoff(handoff, waypoint));
        scopes.Complete(next);
        Assert.Throws<InvalidDataException>(() => scopes.RequireActive(next, session, player, manager));
    }
    [Fact]
    public void SameTargetReplacementAndStaleCancellationCannotBorrowOrRevokeNewRoute()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object(); var target = new object();
        var old = scopes.Begin(session, player, manager, target); var leg = scopes.First(old, target);
        var pending = scopes.PrepareHandoff(leg, target);
        var current = scopes.Begin(session, player, manager, target); var next = scopes.First(current, target);
        Assert.Throws<InvalidDataException>(() => scopes.AcceptHandoff(pending, target));
        Assert.False(scopes.Cancel(old)); scopes.RequireActive(next, session, player, manager);
        Assert.True(scopes.Cancel(current));
        Assert.Throws<InvalidDataException>(() => scopes.RequireActive(next, session, player, manager));
    }
    [Fact]
    public void IndependentChildrenKeepOriginAndStaleLegFailureCannotCancelSuccessor()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object();
        var route = scopes.Begin(session, player, manager, new object()); var first = scopes.First(route, new object());
        using var execution = scopes.Enter(first);
        var childOrigin = scopes.CaptureExecuting(); Assert.Same(first, childOrigin);
        var waypoint = new object(); var next = scopes.AcceptHandoff(scopes.PrepareHandoff(first, waypoint), waypoint);
        Assert.Throws<InvalidDataException>(() => scopes.CaptureExecuting());
        Assert.False(scopes.Cancel(first));
        using (scopes.Enter(next)) Assert.Same(next, scopes.CaptureExecuting());
        Assert.Throws<InvalidDataException>(() => scopes.RequireActive(childOrigin!, session, player, manager));
        scopes.RequireActive(next, session, player, manager);
    }
    [Fact]
    public void OutOfOrderScopeDisposalPreservesCurrentNestedOrigin()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object();
        var route = scopes.Begin(session, player, manager, new object()); var first = scopes.First(route, new object());
        var outer = scopes.Enter(first); var waypoint = new object();
        var next = scopes.AcceptHandoff(scopes.PrepareHandoff(first, waypoint), waypoint);
        var inner = scopes.Enter(next);
        outer.Dispose(); Assert.Same(next, scopes.CaptureExecuting());
        inner.Dispose(); Assert.Null(scopes.CaptureExecuting());
        Assert.True(scopes.Cancel(next));
    }
    [Fact]
    public void HandoffAndContinuationRequireExactObservedIdentities()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object();
        var route = scopes.Begin(session, player, manager, new object()); var leg = scopes.First(route, new object());
        var target = new object(); var pending = scopes.PrepareHandoff(leg, target);
        Assert.Throws<InvalidDataException>(() => scopes.AcceptHandoff(pending, new object()));
        Assert.Throws<InvalidDataException>(() => scopes.RequireActive(leg, Guid.NewGuid(), player, manager));
        Assert.Throws<InvalidDataException>(() => scopes.RequireActive(leg, session, new object(), manager));
        Assert.Throws<InvalidDataException>(() => scopes.RequireActive(leg, session, player, new object()));
        scopes.RequireActive(leg, session, player, manager);
        var next = scopes.AcceptHandoff(pending, target);
        scopes.RequireActive(next, session, player, manager);
    }
}
