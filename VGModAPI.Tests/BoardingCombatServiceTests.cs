using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingCombatServiceTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly BoardingCombatService Rules;
        internal readonly Guid Session;
        internal int Faults;
        internal Fixture()
        {
            Hub.SetCapability("boarding-combat", true, "Test bindings.");
            Rules = new BoardingCombatService(Hub, (_, _) => Faults++);
            Session = Hub.Begin(SessionOrigin.SaveLoad, "save"); Hub.PlayerReady(Session); Hub.GameplayInitialized(Session);
        }
        internal BoardingCombatContext Context(BoardingCombatPolicyKind kind, BoardingCombatSide side = BoardingCombatSide.Defenders)
            => new(new(Session, BoardingEncounterKind.Ship, 5), kind, side, 0, 10);
        public void Dispose() { Rules.Dispose(); Hub.Dispose(); }
    }
    [Fact]
    public void TypedHealthLossDiscardsNumericAndVetoPolicies()
    {
        using var f = new Fixture(); IBoardingCombatService service = f.Rules;
        using var provider = service.AcquireProvider("mod");
        provider.RegisterMultiplier("power", BoardingRuleScope.Both, BoardingCombatPolicyKind.Power, _ =>
        { f.Hub.SetCapability("boarding-combat", false, "Fault.", ServiceUnavailableReason.ObserverFault); return 3; });
        Assert.Equal(10, f.Rules.Scale(f.Context(BoardingCombatPolicyKind.Power)));
        var calls = 0;
        provider.RegisterVeto("veto", BoardingRuleScope.Both, BoardingCombatPolicyKind.Surrender, _ => { calls++; return false; });
        Assert.True(f.Rules.Allow(f.Context(BoardingCombatPolicyKind.Surrender)));
        Assert.Equal(0, calls);
        f.Rules.Dispose();
        Assert.Equal(ServiceUnavailableReason.ObserverFault, service.Availability.Reason);
        Assert.Null(typeof(ModApi).GetProperty("BoardingCombat"));
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IBoardingCombatRules"));
    }
    [Fact]
    public void CompetingNumericPoliciesAreBoundedAndSideAware()
    {
        using var f = new Fixture(); using var a = f.Rules.AcquireProvider("a"); using var b = f.Rules.AcquireProvider("b");
        a.RegisterMultiplier("power", BoardingRuleScope.Ships, BoardingCombatPolicyKind.Power, c => c.Side == BoardingCombatSide.Defenders ? 2 : 1);
        b.RegisterMultiplier("power", BoardingRuleScope.Both, BoardingCombatPolicyKind.Power, _ => .5f);
        Assert.Equal(10, f.Rules.Scale(f.Context(BoardingCombatPolicyKind.Power)));
        Assert.Equal(5, f.Rules.Scale(f.Context(BoardingCombatPolicyKind.Power, BoardingCombatSide.Attackers)));
        b.RegisterMultiplier("invalid", BoardingRuleScope.Both, BoardingCombatPolicyKind.Power, _ => float.NaN);
        Assert.Equal(10, f.Rules.Scale(f.Context(BoardingCombatPolicyKind.Power))); Assert.Equal(1, f.Faults);
    }
    [Theory]
    [InlineData(BoardingCombatPolicyKind.Surrender)]
    [InlineData(BoardingCombatPolicyKind.Defection)]
    [InlineData(BoardingCombatPolicyKind.Reinforcement)]
    [InlineData(BoardingCombatPolicyKind.Hazard)]
    [InlineData(BoardingCombatPolicyKind.Venting)]
    public void DiscreteVetoesAggregateWithoutSkippingOtherContributions(BoardingCombatPolicyKind kind)
    {
        using var f = new Fixture(); using var a = f.Rules.AcquireProvider("a"); using var b = f.Rules.AcquireProvider("b"); var calls = 0;
        a.RegisterVeto("deny", BoardingRuleScope.Both, kind, _ => false);
        b.RegisterVeto("allow", BoardingRuleScope.Both, kind, _ => { calls++; return true; });
        Assert.False(f.Rules.Allow(f.Context(kind))); Assert.Equal(1, calls);
        a.Dispose(); Assert.True(f.Rules.Allow(f.Context(kind)));
    }
    [Fact]
    public void NestedEvaluationUsesBaselineAndSessionReplacementDiscardsResult()
    {
        using var f = new Fixture(); using var a = f.Rules.AcquireProvider("a");
        a.RegisterMultiplier("health", BoardingRuleScope.Both, BoardingCombatPolicyKind.InitialHealth, context =>
        {
            Assert.True(f.Rules.IsEvaluating); Assert.Equal(10, f.Rules.Scale(context));
            f.Hub.Invalidate("replaced"); return 2;
        });
        Assert.Equal(10, f.Rules.Scale(f.Context(BoardingCombatPolicyKind.InitialHealth))); Assert.False(f.Rules.IsEvaluating);
    }
    [Fact]
    public void UnsupportedPolicyFormsAreRejectedAtRegistration()
    {
        using var f = new Fixture(); using var a = f.Rules.AcquireProvider("a");
        Assert.Throws<ArgumentException>(() => a.RegisterVeto("power", BoardingRuleScope.Both, BoardingCombatPolicyKind.Power, _ => true));
        Assert.Throws<ArgumentException>(() => a.RegisterMultiplier("hazard", BoardingRuleScope.Both, BoardingCombatPolicyKind.Hazard, _ => 1));
    }
}
