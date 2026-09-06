using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class TravelArrivalScopesTests
{
    [Fact]
    public void NestedOverrideBaseCallsHaveOneSuccessfulBoundary()
    {
        var scopes = new TravelArrivalScopes(); var manager = new object();
        var outer = scopes.Begin(manager); var middle = scopes.Begin(manager); var inner = scopes.Begin(manager);
        Assert.False(scopes.End(inner, null)); Assert.False(scopes.End(middle, null)); Assert.True(scopes.End(outer, null));
        Assert.False(scopes.End(outer, null));
    }

    [Fact]
    public void CaughtNestedFailureDoesNotBecomeSuccess()
    {
        var scopes = new TravelArrivalScopes(); var manager = new object();
        var outer = scopes.Begin(manager); var inner = scopes.Begin(manager);
        Assert.False(scopes.End(inner, new InvalidOperationException("native failure")));
        Assert.False(scopes.End(outer, null));
    }

    [Fact]
    public void IndependentManagerHasOwnBoundaryEvenWhenNested()
    {
        var scopes = new TravelArrivalScopes();
        var outer = scopes.Begin(new object()); var inner = scopes.Begin(new object());
        Assert.True(scopes.End(inner, null)); Assert.True(scopes.End(outer, null));
    }

    [Fact]
    public void InterveningManagerDoesNotHideReentrantDuplicate()
    {
        var scopes = new TravelArrivalScopes(); var a = new object();
        var outer = scopes.Begin(a); var middle = scopes.Begin(new object()); var inner = scopes.Begin(a);
        Assert.False(scopes.End(inner, null)); Assert.True(scopes.End(middle, null)); Assert.True(scopes.End(outer, null));
    }

    [Fact]
    public void ResetRejectsStaleScopesWithoutPoppingReplacement()
    {
        var scopes = new TravelArrivalScopes(); var old = scopes.Begin(new object()); scopes.Reset();
        var current = scopes.Begin(new object()); Assert.False(scopes.End(old, null)); Assert.True(scopes.End(current, null));
    }

    [Fact]
    public void OutOfOrderCloseFailsInsteadOfManufacturingSuccess()
    {
        var scopes = new TravelArrivalScopes(); var outer = scopes.Begin(new object()); var inner = scopes.Begin(new object());
        Assert.Throws<InvalidOperationException>(() => scopes.End(outer, null));
        Assert.True(scopes.End(inner, null)); Assert.True(scopes.End(outer, null));
    }
}
