using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonRewardScopesTests
{
    [Fact]
    public void NestedLootEntriesDoNotBorrowOuterIdentityAndUnwindOnFailure()
    {
        using var hub = new LifecycleHub((_, _) => { }); var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session);
        using var rules = new DungeonRewardService(hub, (_, _) => { }); using var provider = rules.AcquireProvider("mod");
        using var registration = provider.Register("loot", DungeonRewardKind.LootAmount, _ => new(2));
        var scopes = new DungeonRewardScopes(rules); var operation = new BoardingHandle(hub.CurrentSession!.Id, Guid.NewGuid());
        var outer = new object(); var inner = new object();
        using (scopes.Begin(operation, DungeonRewardKind.LootAmount, "FriendlyVictory", false, outer))
        {
            Assert.Equal(6, scopes.LootAmount(outer, 3)); Assert.Equal(3, scopes.LootAmount(inner, 3));
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var nested = scopes.Begin(operation, DungeonRewardKind.LootAmount, "FriendlyVictory", true, inner);
                Assert.Equal(3, scopes.LootAmount(outer, 3)); Assert.Equal(3, scopes.LootAmount(inner, 3));
                throw new InvalidOperationException();
            }));
            Assert.Equal(6, scopes.LootAmount(outer, 3));
        }
        Assert.Equal(3, scopes.LootAmount(outer, 3));
    }
}
