using System;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;

namespace VGModAPI.Tests;

// Static patch adapters must not be shared by concurrent tests.
[Collection("Dungeon terminal patches")]
public sealed class DungeonTerminalIntegrationTests
{
    private sealed class NativeTerminal
    {
        internal readonly NativeObject Operation = new(), ShipData = new(), Recipient = new();
        internal int CapturedInventory, MissionRewards, MissionFailures, CaptureCalls, Departures;
        internal bool CrewCleared, ModulesDamaged, AmmoCapped, HangarPrepared;
        internal NativeTerminal(bool mission)
        {
            ShipData.Fields["settlementMissionGuid"] = mission ? "mission" : null;
            var location = new NativeObject(); location.Fields["settlementToken"] = null; location.Fields["shipData"] = ShipData;
            var ship = new NativeObject(); ship.Fields["settlementShipData"] = Recipient;
            var simulation = new NativeObject(); simulation.Fields["outcome"] = "InProgress";
            Operation.Fields["location"] = location; Operation.Fields["operationShip"] = ship; Operation.Fields["simulation"] = simulation;
        }
        internal float Run(bool victory, bool patched, Action capture, Action claimMission, Action failMission, Action depart)
        {
            if (patched) DungeonRewardPatches.Terminal.Prefix(Operation);
            if (victory)
            {
                var token = (string?)ShipData.Fields["settlementMissionGuid"];
                capture();
                if (!string.IsNullOrEmpty(token)) claimMission();
                depart();
            }
            else failMission();
            IDisposable? scope = null;
            if (patched) DungeonRewardPatches.MasteryScope.Prefix(Operation, out scope);
            var award = 100f * (victory ? 1f : 0.25f);
            try { if (patched) DungeonRewardPatches.Mastery.Prefix(Recipient, ref award, "Leadership"); }
            finally { if (patched) Assert.Null(DungeonRewardPatches.MasteryScope.Finalizer(scope, null)); }
            return award;
        }
        internal void Capture()
        {
            CaptureCalls++;
            CrewCleared = true; ModulesDamaged = true; AmmoCapped = true; HangarPrepared = true;
            if (string.IsNullOrEmpty((string?)ShipData.Fields["settlementMissionGuid"])) CapturedInventory++;
            ShipData.Fields["settlementMissionGuid"] = null;
        }
        internal float Execute(bool victory, bool patched) => Run(victory, patched, Capture, () => MissionRewards++, () => MissionFailures++, () => Departures++);
    }
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public void ActualPatchEntryPointsPreserveCaptureAndMissionCallbacks(bool mission, bool victory, bool policyThrows)
    {
        using var hub = new LifecycleHub((_, _) => { }); var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session);
        hub.SetCapability("dungeon-rewards", true, "Test bindings."); using var rules = new DungeonRewardService(hub, (_, _) => { }); using var provider = rules.AcquireProvider("mod");
        var policyCalls = 0;
        using var registration = provider.Register("xp", DungeonRewardKind.MasteryExperience, _ =>
        { policyCalls++; if (policyThrows) throw new InvalidOperationException("provider"); return new(2); });
        var baseline = new NativeTerminal(mission); var instrumented = new NativeTerminal(mission);
        var handle = new BoardingHandle(session, Guid.NewGuid());
        DungeonRewardPatches.Adapter = new DungeonRewardAdapter(hub, obj => ReferenceEquals(obj, instrumented.Operation) ? handle : null, rules, new DungeonLayoutBuilderTests.Native());
        try
        {
            var nativeAward = baseline.Execute(victory, false);
            var award = instrumented.Execute(victory, true);
            Assert.Equal(baseline.CaptureCalls, instrumented.CaptureCalls);
            Assert.Equal(baseline.CapturedInventory, instrumented.CapturedInventory);
            Assert.Equal(baseline.MissionRewards, instrumented.MissionRewards);
            Assert.Equal(baseline.MissionFailures, instrumented.MissionFailures);
            Assert.Equal(baseline.Departures, instrumented.Departures);
            Assert.Equal(baseline.CrewCleared, instrumented.CrewCleared);
            Assert.Equal(baseline.ModulesDamaged, instrumented.ModulesDamaged);
            Assert.Equal(baseline.AmmoCapped, instrumented.AmmoCapped);
            Assert.Equal(baseline.HangarPrepared, instrumented.HangarPrepared);
            Assert.Equal(mission || policyThrows ? nativeAward : nativeAward * 2, award);
            Assert.Equal(mission ? 0 : 1, policyCalls);
            Assert.Equal(victory ? 0 : 1, instrumented.MissionFailures);
        }
        finally { DungeonRewardPatches.Adapter = null; }
    }
}

[CollectionDefinition("Dungeon terminal patches", DisableParallelization = true)]
public sealed class DungeonTerminalPatchCollection { }
