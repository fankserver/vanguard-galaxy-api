using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class EquipmentTargetingTests
{
    private static TractorModule Module => new(2, 3);
    [Theory]
    [InlineData(0, 2)]
    [InlineData(3, 3)]
    [InlineData(10, 5)]
    public void CapacityStaysBetweenVanillaAndPhysicalBeamCount(int limit, int expected)
        => Assert.Equal(expected, EquipmentService.AutomaticCapacity(Module, new(limit)));
    [Theory]
    [InlineData(2, false, true)]
    [InlineData(3, false, false)]
    [InlineData(5, false, false)]
    [InlineData(5, true, true)]
    public void BusyCountIncludesBothPoolsAndManualBorrowIsIndependent(int busy, bool manual, bool expected)
        => Assert.Equal(expected, EquipmentService.MayBorrow(Module, new(3, true), busy, manual));
    [Fact]
    public void ManualBorrowRequiresOptIn()
        => Assert.False(EquipmentService.MayBorrow(Module, new(5), 0, true));
    [Fact]
    public void NegativeLimitIsRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => new TractorTargeting(-1));
    [Fact]
    public void UnavailableAndDisabledRulesLeaveVanillaUntouched()
    {
        using var service = new EquipmentService(new LifecycleHub((_, _) => { }));
        var enabled = false;
        service.ConfigurePlayerTractorModules("mod", _ => enabled ? new(3, true) : null);
        Assert.Null(service.Resolve(Module));
        service.SetAvailable(true);
        Assert.Null(service.Resolve(Module));
        enabled = true;
        Assert.Equal(3, service.Resolve(Module)!.AutomaticBeamLimit);
    }
    [Fact]
    public void CallbackFailuresAreIsolatedAndReentrancyAbstains()
    {
        int failures = 0;
        var hub = new LifecycleHub((_, _) => failures++);
        using var service = new EquipmentService(hub);
        service.SetAvailable(true);
        service.ConfigurePlayerTractorModules("broken", _ => throw new InvalidOperationException());
        service.ConfigurePlayerTractorModules("valid", module =>
        {
            Assert.True(hub.IsDispatchingCallbacks);
            Assert.Null(service.Resolve(module));
            return new(4);
        });
        Assert.Equal(4, service.Resolve(Module)!.AutomaticBeamLimit);
        Assert.Equal(1, failures);
        Assert.False(hub.IsDispatchingCallbacks);
    }
    [Fact]
    public void RemovalDuringEvaluationSuppressesLaterRulesAndDisposeClosesService()
    {
        using var service = new EquipmentService(new LifecycleHub((_, _) => { }));
        service.SetAvailable(true);
        IDisposable? removed = null;
        service.ConfigurePlayerTractorModules("first", _ => { removed!.Dispose(); return null; });
        removed = service.ConfigurePlayerTractorModules("second", _ => new(5));
        Assert.Null(service.Resolve(Module));
        service.Dispose();
        Assert.Null(service.Resolve(Module));
        Assert.Equal(ServiceUnavailableReason.ApiStopped, service.Availability.Reason);
        Assert.Throws<ObjectDisposedException>(() => service.ConfigurePlayerTractorModules("later", _ => null));
    }
    [Fact]
    public void OwnerIdentityCannotBeDuplicatedButCanBeReusedAfterDisposal()
    {
        using var service = new EquipmentService(new LifecycleHub((_, _) => { }));
        var lease = service.ConfigurePlayerTractorModules("mod", _ => null);
        Assert.Throws<InvalidOperationException>(() => service.ConfigurePlayerTractorModules("mod", _ => null));
        lease.Dispose();
        using var replacement = service.ConfigurePlayerTractorModules("mod", _ => null);
    }
}
