using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonRewardServiceTests
{
    [Fact]
    public void TypedHealthLossPreservesNativeRewardAndClosesFurtherEvaluation()
    {
        using var hub = new LifecycleHub((_, _) => { });
        var session = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(session);
        hub.SetCapability("dungeon-rewards", true, "Test bindings.");
        using var engine = new DungeonRewardService(hub, (_, _) => { });
        IDungeonRewardService service = engine;
        using var provider = service.AcquireProvider("mod");
        var calls = 0;
        provider.Register("reward", DungeonRewardKind.LootAmount, _ =>
        { calls++; hub.SetCapability("dungeon-rewards", false, "Fault.", ServiceUnavailableReason.ObserverFault); return new(10); });
        var context = new DungeonRewardContext(new(session, Guid.NewGuid()), DungeonRewardKind.LootAmount, "HostileVictory", false, 3);
        Assert.Equal(3, engine.Apply(context));
        Assert.Equal(3, engine.Apply(context));
        Assert.Equal(1, calls);
        engine.Dispose();
        Assert.Equal(ServiceUnavailableReason.ObserverFault, service.Availability.Reason);
        Assert.Null(typeof(ModApi).GetProperty("DungeonRewards"));
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IDungeonRewardRules"));
    }
    [Fact]
    public void DisposalClosesRegistrationBeforeHealthNotification()
    {
        using var hub = new LifecycleHub((_, _) => { });
        hub.SetCapability("dungeon-rewards", true, "Test bindings.");
        using var service = new DungeonRewardService(hub, (_, _) => { });
        Exception? rejection = null;
        service.AvailabilityChanged += _ => { service.Dispose(); rejection = Record.Exception(() => service.AcquireProvider("late")); };
        service.Dispose();
        Assert.IsType<ObjectDisposedException>(rejection);
        Assert.Equal(ServiceUnavailableReason.ApiStopped, service.Availability.Reason);
    }
    [Fact]
    public void PoliciesComposeButNeverChangeMissionTokenCaptureRewards()
    {
        using var hub = new LifecycleHub((_, _) => { }); var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session);
        hub.SetCapability("dungeon-rewards", true, "Test bindings."); using var service = new DungeonRewardService(hub, (_, _) => { }); using var a = service.AcquireProvider("a"); using var b = service.AcquireProvider("b");
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
        hub.SetCapability("dungeon-rewards", true, "Test bindings."); using var service = new DungeonRewardService(hub, (_, _) => { }); var provider = service.AcquireProvider("a");
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
