using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingCombatAdapterTests
{
    [Fact]
    public void UnitScalingIsScopedAndDoesNotLeakBetweenSimulations()
    {
        using var hub = new LifecycleHub((_, _) => { }); using var rules = new BoardingCombatService(hub, (_, _) => { });
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); hub.GameplayInitialized(session);
        var adapter = new BoardingCombatAdapter(hub, rules, (obj, key) => ((Dictionary<string, object?>)obj)[key], (obj, key, value) => ((Dictionary<string, object?>)obj)[key] = value);
        using var mod = rules.AcquireProvider("mod"); mod.RegisterMultiplier("power", BoardingRuleScope.Ships, BoardingCombatPolicyKind.Power, _ => 2);
        var ship = new Dictionary<string, object?> { ["combatKind"] = "HostileShip", ["combatLevel"] = 1 };
        var station = new Dictionary<string, object?> { ["combatKind"] = "HostileStation", ["combatLevel"] = 1 };
        var unit = new Dictionary<string, object?> { ["combatFriendly"] = false };
        Assert.Equal(5, adapter.UnitValue(unit, BoardingCombatPolicyKind.Power, 5));
        var outer = adapter.Begin(ship); Assert.Equal(10, adapter.UnitValue(unit, BoardingCombatPolicyKind.Power, 5));
        var inner = adapter.Begin(station); Assert.Equal(5, adapter.UnitValue(unit, BoardingCombatPolicyKind.Power, 5));
        adapter.End(inner); Assert.Equal(10, adapter.UnitValue(unit, BoardingCombatPolicyKind.Power, 5));
        adapter.End(outer); Assert.Equal(5, adapter.UnitValue(unit, BoardingCombatPolicyKind.Power, 5));
    }
    [Fact]
    public void MoraleScalingPrecedesSurrenderEvaluationAndIsNotAppliedTwice()
    {
        using var hub = new LifecycleHub((_, _) => { }); using var rules = new BoardingCombatService(hub, (_, _) => { });
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); hub.GameplayInitialized(session);
        var adapter = new BoardingCombatAdapter(hub, rules, (obj, key) => ((Dictionary<string, object?>)obj)[key], (obj, key, value) => ((Dictionary<string, object?>)obj)[key] = value);
        var unit = new Dictionary<string, object?> { ["combatFriendly"] = false, ["combatMorale"] = .8f };
        var sim = new Dictionary<string, object?> { ["combatKind"] = "HostileShip", ["combatLevel"] = 1, ["friendlyUnits"] = new ArrayList(), ["hostileUnits"] = new ArrayList { unit } };
        using var mod = rules.AcquireProvider("mod"); mod.RegisterMultiplier("morale", BoardingRuleScope.Both, BoardingCombatPolicyKind.Morale, _ => 0);
        mod.RegisterVeto("surrender", BoardingRuleScope.Both, BoardingCombatPolicyKind.Surrender, _ => { Assert.Equal(.8f, unit["combatMorale"]); return false; });
        var state = adapter.BeginMorale(sim); unit["combatMorale"] = .2f;
        Assert.False(adapter.Allow(sim, BoardingCombatPolicyKind.Surrender, BoardingCombatSide.Defenders, unit, 0));
        adapter.EndMorale(state, true); Assert.Equal(.8f, unit["combatMorale"]);
    }
}
