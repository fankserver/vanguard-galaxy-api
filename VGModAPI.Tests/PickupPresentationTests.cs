using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PickupPresentationTests
{
    private static readonly UiColor Tint = new(0.2f, 0.7f, 1);
    private static ItemPickupPresentation Pickup => new("equipment", "Railgun Mk.VII", 2, Tint);

    [Fact]
    public void ResolverReceivesActualPickupAndFirstColorWins()
    {
        var hub = new LifecycleHub((_, _) => { });
        using var service = new PickupPresentationService(hub);
        service.SetAvailable(true);
        using var first = service.Register("first", p =>
        {
            Assert.Equal("equipment", p.ItemId);
            Assert.Equal("Railgun Mk.VII", p.DisplayName);
            Assert.Equal(2, p.Count);
            Assert.True(hub.IsDispatchingCallbacks);
            return p.RarityColor;
        });
        using var second = service.Register("second", _ => throw new Exception("must not run"));
        Assert.Equal(Tint, service.Resolve(Pickup));
        Assert.False(hub.IsDispatchingCallbacks);
    }

    [Fact]
    public void ExceptionsAndAbstentionsLeaveLaterResolversAvailable()
    {
        int failures = 0;
        var hub = new LifecycleHub((_, _) => failures++);
        using var service = new PickupPresentationService(hub);
        service.SetAvailable(true);
        service.Register("broken", _ => throw new InvalidOperationException());
        service.Register("abstain", _ => null);
        service.Register("tint", p => p.RarityColor);
        Assert.Equal(Tint, service.Resolve(Pickup));
        Assert.Equal(1, failures);
        Assert.Null(service.Resolve(new("standard", "Iron", 1, null)));
    }

    [Fact]
    public void UnavailableDisposedAndReentrantCallsDoNotStyle()
    {
        var hub = new LifecycleHub((_, _) => { });
        using var service = new PickupPresentationService(hub);
        service.Register("tint", p => { Assert.Null(service.Resolve(p)); return Tint; });
        Assert.Null(service.Resolve(Pickup));
        service.SetAvailable(true);
        Assert.Equal(Tint, service.Resolve(Pickup));
        service.Dispose();
        Assert.Null(service.Resolve(Pickup));
        Assert.Throws<ObjectDisposedException>(() => service.Register("later", _ => Tint));
    }

    [Fact]
    public void RemovedResolversAreSkippedAndNewResolversWaitForNextNotification()
    {
        var hub = new LifecycleHub((_, _) => { });
        using var service = new PickupPresentationService(hub);
        service.SetAvailable(true);
        IDisposable? second = null, added = null;
        service.Register("first", _ =>
        {
            second!.Dispose();
            added ??= service.Register("added", _ => Tint);
            return null;
        });
        second = service.Register("second", _ => throw new Exception("removed"));
        Assert.Null(service.Resolve(Pickup));
        Assert.Equal(Tint, service.Resolve(Pickup));
    }

    [Fact]
    public void DuplicateOwnersAreRejectedAndDisposalAllowsReplacement()
    {
        using var service = new PickupPresentationService(new LifecycleHub((_, _) => { }));
        var registration = service.Register("owner", _ => Tint);
        Assert.Throws<InvalidOperationException>(() => service.Register("owner", _ => null));
        registration.Dispose();
        using var replacement = service.Register("owner", _ => null);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public void ColorsRejectInvalidComponents(float value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new UiColor(value, 0, 0));
}
