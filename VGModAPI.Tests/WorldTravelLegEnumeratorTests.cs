using System;
using System.Collections;
using System.IO;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldTravelLegEnumeratorTests
{
    private sealed class Step : IEnumerator
    {
        internal Func<bool> Advance = () => false;
        public object? Current { get; set; }
        public bool MoveNext() => Advance();
        public void Reset() => throw new NotSupportedException();
    }
    [Fact]
    public void SynchronousSuccessorHandoffSkipsPredecessorRootSharedStateTail()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object();
        var route = scopes.Begin(session, player, manager, new object()); var leg = scopes.First(route, new object());
        WorldTravelScopes.Leg? successor = null; var waypoint = new object(); int tailWrites = 0, moves = 0;
        var travel = new Step { Advance = () => { successor = scopes.AcceptHandoff(scopes.PrepareHandoff(leg, waypoint), waypoint); return false; } };
        var startTravel = new Step { Current = travel, Advance = () => { if (++moves == 1) return true; tailWrites++; return false; } };
        using var wrapper = new WorldTravelLegEnumerator(startTravel, scopes, leg, () => scopes.RequireActive(leg, session, player, manager));
        Assert.True(wrapper.MoveNext());
        var nested = Assert.IsType<WorldTravelLegEnumerator>(wrapper.Current);
        Assert.False(nested.MoveNext()); Assert.False(wrapper.MoveNext());
        Assert.Equal(0, tailWrites); Assert.Equal(1, moves);
        scopes.RequireActive(successor!, session, player, manager);
        nested.Dispose();
    }
    [Fact]
    public void UnexpectedYieldAfterHandoffRefusesWithoutCancellingSuccessor()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object();
        var route = scopes.Begin(session, player, manager, new object()); var leg = scopes.First(route, new object());
        WorldTravelScopes.Leg? successor = null; var target = new object();
        var inner = new Step { Advance = () => { successor = scopes.AcceptHandoff(scopes.PrepareHandoff(leg, target), target); return true; } };
        using var wrapper = new WorldTravelLegEnumerator(inner, scopes, leg, () => scopes.RequireActive(leg, session, player, manager));
        Assert.Throws<InvalidDataException>(() => wrapper.MoveNext());
        scopes.RequireActive(successor!, session, player, manager);
    }
}
