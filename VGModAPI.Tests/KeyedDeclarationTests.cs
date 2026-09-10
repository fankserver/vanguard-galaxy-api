using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>Named regression: disposing a superseded keyed handle must not revoke its replacement.</summary>
public sealed class KeyedDeclarationTests : IDisposable
{
    private readonly LifecycleHub _hub = new((_, _) => { });

    [Fact]
    public void DisposingASupersededProtectionHandleDoesNotRevokeTheReplacement()
    {
        using var service = new UnitProtectionService(_hub);
        service.SetAvailable(true);
        var old = service.Protect("unit-a", key: "escort");
        var replacement = service.Protect("unit-b", key: "escort");
        Assert.False(service.IsProtected("unit-a"));
        Assert.True(service.IsProtected("unit-b"));
        old.Dispose();
        Assert.True(service.IsProtected("unit-b"));
        replacement.Dispose();
        Assert.False(service.IsProtected("unit-b"));
    }

    [Fact]
    public void KeyedRedeclarationReplacesOnlyTheSameKey()
    {
        using var service = new UnitProtectionService(_hub);
        service.SetAvailable(true);
        service.Protect("unit-a", key: "escort");
        service.Protect("unit-b", key: "convoy");
        service.Protect("unit-c", key: "escort");
        Assert.False(service.IsProtected("unit-a"));
        Assert.True(service.IsProtected("unit-b"));
        Assert.True(service.IsProtected("unit-c"));
    }

    [Fact]
    public void UnkeyedDeclarationsRemainIndependent()
    {
        using var service = new UnitProtectionService(_hub);
        service.SetAvailable(true);
        service.Protect("unit-a");
        service.Protect("unit-a");
        Assert.True(service.IsProtected("unit-a"));
    }

    [Fact]
    public void KeyedTrafficSuppressionReplacesTheAuthorsPreviousAnchor()
    {
        using var service = new AmbientTrafficService(_hub);
        service.SetAvailable(true);
        var old = service.SuppressAtStation("station-a", key: "hub");
        service.SuppressAtStation("station-b", key: "hub");
        old.Dispose();
        Assert.False(service.ShouldSuppress(AmbientSpawnSite.Station, "station-a", null, _ => null));
        Assert.True(service.ShouldSuppress(AmbientSpawnSite.Station, "station-b", null, _ => null));
    }

    [Fact]
    public void KeyedDroneTuningReplacesTheAuthorsPreviousDeclaration()
    {
        using var service = new DroneBayService(_hub);
        service.SetAvailable(true);
        var old = service.Tune("unit-a", new DroneBayTuning(launchSeconds: 2), key: "boss");
        service.Tune("unit-a", new DroneBayTuning(launchSeconds: 5), key: "boss");
        old.Dispose();
        Assert.Equal(5, service.EffectiveFor("unit-a")!.LaunchSeconds);
    }

    [Fact]
    public void FailedDroneDisposeCannotOrphanTheKeyedReplacementChain()
    {
        using var service = new DroneBayService(_hub);
        service.SetAvailable(true);
        var first = service.Tune("unit-a", new DroneBayTuning(launchSeconds: 2), key: "boss");
        first.Dispose();
        first.Dispose(); // A second (already-disposed) dispose must not touch the keyed map.
        var second = service.Tune("unit-a", new DroneBayTuning(launchSeconds: 3), key: "boss");
        service.Tune("unit-a", new DroneBayTuning(launchSeconds: 5), key: "boss");
        second.Dispose();
        Assert.Equal(5, service.EffectiveFor("unit-a")!.LaunchSeconds);
    }

    [Fact]
    public void InvalidKeysAreRejected()
    {
        using var service = new UnitProtectionService(_hub);
        service.SetAvailable(true);
        Assert.Throws<ArgumentException>(() => service.Protect("unit-a", key: " "));
        Assert.Throws<ArgumentException>(() => service.Protect("unit-a", key: new string('k', 129)));
        Assert.Throws<ArgumentException>(() => service.Protect("unit-a", key: "a\u0000b"));
    }

    public void Dispose() => _hub.Dispose();
}
