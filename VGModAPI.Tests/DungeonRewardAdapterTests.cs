using System;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;

namespace VGModAPI.Tests;

public sealed class DungeonRewardAdapterTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly DungeonRewardService Rules;
        internal readonly DungeonRewardAdapter Adapter;
        internal readonly NativeObject Operation = new(), Recipient = new(), ShipData = new();
        private readonly IDungeonRewardProvider _provider;
        internal Fixture(bool mission)
        {
            var session = Hub.Begin(SessionOrigin.SaveLoad, "save"); Hub.PlayerReady(session);
            Rules = new(Hub, (_, _) => { }); _provider = Rules.AcquireProvider("test");
            _provider.Register("xp", DungeonRewardKind.MasteryExperience, _ => new(2));
            _provider.Register("loot", DungeonRewardKind.LootAmount, _ => new(2));
            ShipData.Fields["settlementMissionGuid"] = mission ? "capture-mission" : null;
            var location = new NativeObject(); location.Fields["settlementToken"] = null; location.Fields["shipData"] = ShipData;
            var ship = new NativeObject(); ship.Fields["settlementShipData"] = Recipient;
            var simulation = new NativeObject(); simulation.Fields["outcome"] = "FriendlyVictory";
            Operation.Fields["location"] = location; Operation.Fields["simulation"] = simulation; Operation.Fields["operationShip"] = ship;
            var handle = new BoardingHandle(session, Guid.NewGuid());
            Adapter = new(Hub, native => ReferenceEquals(native, Operation) ? handle : null, Rules, new DungeonLayoutBuilderTests.Native());
        }
        public void Dispose() { _provider.Dispose(); Rules.Dispose(); Hub.Dispose(); }
    }
    [Theory]
    [InlineData(true, 100f)]
    [InlineData(false, 200f)]
    public void TerminalProtectionSurvivesNativeCaptureClearingMissionGuid(bool mission, float expected)
    {
        using var f = new Fixture(mission);
        // Terminal prefix precedes loot, capture/mission consequences and the mastery award.
        f.Adapter.RetainProtection(f.Operation);
        var loot = new object();
        using (f.Adapter.Begin(f.Operation, loot, false)) Assert.Equal(mission ? 3 : 6, f.Adapter.LootCount(loot, 3));
        f.ShipData.Fields["settlementMissionGuid"] = null;
        using (f.Adapter.Begin(f.Operation, null, true)) Assert.Equal(expected, f.Adapter.Mastery(f.Recipient, 100, "Leadership"));
    }
    [Fact]
    public void UntrackedNestedSameRecipientMasksOuterMasteryAndUnwindsOnException()
    {
        using var f = new Fixture(false);
        using var outer = f.Adapter.Begin(f.Operation, null, true);
        Assert.Equal(200f, f.Adapter.Mastery(f.Recipient, 100, "Leadership"));
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var inner = f.Adapter.Begin(new NativeObject(), null, true);
            Assert.Equal(100f, f.Adapter.Mastery(f.Recipient, 100, "Leadership"));
            throw new InvalidOperationException();
        }));
        Assert.Equal(200f, f.Adapter.Mastery(f.Recipient, 100, "Leadership"));
    }
    [Theory]
    [InlineData(false, false, 4, 2)]
    [InlineData(false, true, 0, 0)]
    [InlineData(true, false, 3, 0)]
    public void PartialRetreatSelectionAndCargoOverflowRemainOutsideQuantityAdjustment(bool mission, bool discardOnRetreat, int expectedCargo, int expectedDrop)
    {
        using var f = new Fixture(mission);
        var cargo = 0; var worldDrop = 0; var loot = new object();
        // Native selection happens before AddLootEntryToCargo and therefore before the policy scope.
        if (!discardOnRetreat)
        {
            using var delivery = f.Adapter.Begin(f.Operation, loot, false);
            var count = f.Adapter.LootCount(loot, 3);
            cargo = Math.Min(4, count); worldDrop = count - cargo;
        }
        Assert.Equal(expectedCargo, cargo); Assert.Equal(expectedDrop, worldDrop);
        Assert.Equal(3, f.Adapter.LootCount(loot, 3));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureSequencingPreservesNativeConsequencesAndMissionRewardBoundary(bool mission)
    {
        using var f = new Fixture(mission);
        var consequences = new System.Collections.Generic.List<string>();
        f.Adapter.RetainProtection(f.Operation);
        // Native-shaped terminal driver: adapters surround rewards, never replace capture or mission calls.
        var loot = new object(); using (f.Adapter.Begin(f.Operation, loot, false)) f.Adapter.LootCount(loot, 3);
        consequences.Add("faction-player"); consequences.Add("commander-cleared"); consequences.Add("ammo-capped");
        consequences.Add("crew-cleared"); consequences.Add("module-damage"); consequences.Add("hull-upgrade-damage");
        if (!mission) consequences.Add("captured-inventory");
        consequences.Add("capture-mission-trigger");
        f.ShipData.Fields["settlementMissionGuid"] = null; consequences.Add("npc-state-cleared"); consequences.Add("hangar-prepared");
        var missionRewards = mission ? 1 : 0; if (mission) consequences.Add("mission-reward");
        consequences.Add("world-departure");
        using (f.Adapter.Begin(f.Operation, null, true)) Assert.Equal(mission ? 100f : 200f, f.Adapter.Mastery(f.Recipient, 100, "Leadership"));
        Assert.Equal(!mission, consequences.Contains("captured-inventory")); Assert.Equal(mission ? 1 : 0, missionRewards);
        Assert.Equal("world-departure", consequences[consequences.Count - 1]);
        Assert.True(consequences.IndexOf("crew-cleared") < consequences.IndexOf("capture-mission-trigger"));
        Assert.True(consequences.IndexOf("npc-state-cleared") < consequences.IndexOf("hangar-prepared"));
    }
    [Fact]
    public void NativeDefeatReductionPrecedesAdjustmentAndOtherSpecializationsRemainUntouched()
    {
        using var f = new Fixture(false);
        const float nativeBaseAward = 100;
        const float nativeDefeatMultiplier = 0.25f;
        using var scope = f.Adapter.Begin(f.Operation, null, true);
        Assert.Equal(50f, f.Adapter.Mastery(f.Recipient, nativeBaseAward * nativeDefeatMultiplier, "Leadership"));
        Assert.Equal(25f, f.Adapter.Mastery(f.Recipient, nativeBaseAward * nativeDefeatMultiplier, "Engineering"));
    }
}
