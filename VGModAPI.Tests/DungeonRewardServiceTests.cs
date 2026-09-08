using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonRewardServiceTests
{
    [Fact]
    public void PoliciesComposeButNeverChangeMissionTokenCaptureRewards()
    {
        using var hub = new LifecycleHub((_, _) => { }); var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session);
        using var service = new DungeonRewardService(hub, (_, _) => { }); using var a = service.AcquireProvider("a"); using var b = service.AcquireProvider("b");
        var calls = 0;
        using var ar = a.Register("same", DungeonRewardKind.MasteryExperience, _ => { calls++; return new(2); });
        using var br = b.Register("same", DungeonRewardKind.MasteryExperience, _ => new(0.5));
        var operation = new BoardingHandle(hub.CurrentSession!.Id, Guid.NewGuid());
        Assert.Equal(25d, service.Apply(new(operation, DungeonRewardKind.MasteryExperience, "HostileVictory", false, 25)));
        Assert.Equal(25d, service.Apply(new(operation, DungeonRewardKind.MasteryExperience, "FriendlyVictory", true, 25))); Assert.Equal(1, calls);
    }
    [Fact]
    public void CallbackDisposalAndSessionChangeCannotApplyStaleAdjustment()
    {
        using var hub = new LifecycleHub((_, _) => { }); var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session);
        using var service = new DungeonRewardService(hub, (_, _) => { }); var provider = service.AcquireProvider("a");
        var operation = new BoardingHandle(hub.CurrentSession!.Id, Guid.NewGuid());
        using var registration = provider.Register("dispose", DungeonRewardKind.LootAmount, context =>
        {
            Assert.Equal(context.NativeAmount, service.Apply(context)); provider.Dispose(); return new(10);
        });
        Assert.Equal(3d, service.Apply(new(operation, DungeonRewardKind.LootAmount, "FriendlyVictory", false, 3)));
        using var next = service.AcquireProvider("a"); using var nextRegistration = next.Register("session", DungeonRewardKind.LootAmount, _ => { hub.Begin(SessionOrigin.SaveLoad, "next"); return new(10); });
        Assert.Equal(3d, service.Apply(new(operation, DungeonRewardKind.LootAmount, "FriendlyVictory", false, 3)));
    }
}
