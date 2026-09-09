using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingRuleServiceTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly BoardingRuleService Rules;
        internal readonly Guid Session;
        internal int Faults;
        internal readonly List<string> Diagnostics = new();
        internal Fixture()
        {
            Hub.SetCapability("boarding-rules", true, "Test bindings.");
            Rules = new BoardingRuleService(Hub, (owner, error) => { Faults++; Diagnostics.Add(owner + ": " + error.Message); });
            Session = Hub.Begin(SessionOrigin.SaveLoad, "save"); Hub.PlayerReady(Session); Hub.GameplayInitialized(Session);
        }
        internal BoardingEncounterContext Context(BoardingEncounterKind kind = BoardingEncounterKind.Ship) => new(Session, kind, 10);
        public void Dispose() { Rules.Dispose(); Hub.Dispose(); }
    }
    [Fact]
    public void TypedHealthLossDiscardsCompositionAndSkipsLaterRules()
    {
        using var f = new Fixture();
        IBoardingRuleService service = f.Rules;
        using var provider = service.AcquireProvider("mod");
        var later = 0;
        provider.RegisterEncounter("first", BoardingRuleScope.Both, _ =>
        {
            f.Hub.SetCapability("boarding-rules", false, "Observer failed.", ServiceUnavailableReason.ObserverFault);
            return new(2, 3);
        }, 10);
        provider.RegisterEncounter("later", BoardingRuleScope.Both, _ => { later++; return new(4, 5); });
        Assert.Equal((1f, 1f), f.Rules.Encounter(f.Context()));
        Assert.Equal(0, later);
        Assert.Equal(ServiceUnavailableReason.ObserverFault, service.Availability.Reason);
        f.Rules.Dispose();
        Assert.Equal(ServiceUnavailableReason.ObserverFault, service.Availability.Reason);
    }
    [Fact]
    public void UnavailableServiceAllowsDeclarationsButNeverEvaluatesThem()
    {
        using var f = new Fixture();
        f.Hub.SetCapability("boarding-rules", false, "Disabled.", ServiceUnavailableReason.Disabled);
        IBoardingRuleService service = f.Rules;
        using var provider = service.AcquireProvider("mod");
        var calls = 0;
        provider.RegisterEncounter("rule", BoardingRuleScope.Both, _ => { calls++; return new(2, 3); });
        Assert.Equal((1f, 1f), f.Rules.Encounter(f.Context()));
        Assert.Equal(0, calls);
        Assert.Null(typeof(ModApi).GetProperty("BoardingRules"));
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IBoardingRules"));
    }
    [Fact]
    public void DisableThresholdAndDenialsPreserveVanillaDefault()
    {
        using var f = new Fixture(); using var mod = f.Rules.AcquireProvider("mod");
        mod.RegisterDisable("threshold", ctx => ctx.HullFraction < .4f ? BoardingDisableDecision.Allow : BoardingDisableDecision.Vanilla);
        Assert.Equal(BoardingDisableDecision.Vanilla, f.Rules.Disable(new(f.Session, 40, 100, 0)));
        Assert.Equal(BoardingDisableDecision.Allow, f.Rules.Disable(new(f.Session, 39, 100, 0)));
        Assert.Equal(BoardingDisableDecision.Allow, f.Rules.Disable(new(f.Session, 0, 100, 0)));
        using var veto = mod.RegisterDisable("veto", _ => BoardingDisableDecision.Deny, -100);
        Assert.Equal(BoardingDisableDecision.Deny, f.Rules.Disable(new(f.Session, 20, 100, 0)));
    }
    [Fact]
    public void ScopeAndHarderMultipliersAreSupported()
    {
        using var f = new Fixture(); using var mod = f.Rules.AcquireProvider("mod");
        mod.RegisterEncounter("hard", BoardingRuleScope.Ships, _ => new(2, 3));
        Assert.Equal((2f, 3f), f.Rules.Encounter(f.Context()));
        Assert.Equal((1f, 1f), f.Rules.Encounter(f.Context(BoardingEncounterKind.Installation)));
        mod.Dispose(); Assert.Equal((1f, 1f), f.Rules.Encounter(f.Context()));
    }
    [Fact]
    public void InvalidProposalDoesNotPartiallyApply()
    {
        using var f = new Fixture(); using var mod = f.Rules.AcquireProvider("mod");
        mod.RegisterEncounter("a", BoardingRuleScope.Both, _ => new(10, 10));
        mod.RegisterEncounter("b", BoardingRuleScope.Both, _ => new(10, 10));
        mod.RegisterEncounter("c", BoardingRuleScope.Both, _ => new(.5f, 2));
        Assert.Equal((100f, 100f), f.Rules.Encounter(f.Context())); Assert.Equal(1, f.Faults);
    }
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    [InlineData(11f)]
    public void InvalidDamageMultipliersAreRejectedWithoutChangingBaseline(float value)
    {
        using var f = new Fixture(); using var mod = f.Rules.AcquireProvider("mod");
        mod.RegisterIntegrity("invalid", BoardingRuleScope.Both, _ => value);
        Assert.Equal(20, f.Rules.Integrity(new(f.Context(), BoardingDamageCause.Hazard, 20, 100)));
        Assert.Equal(1, f.Faults);
    }
    [Fact]
    public void AuthoritativeHostDestructionBypassesDamagePolicies()
    {
        using var f = new Fixture(); using var mod = f.Rules.AcquireProvider("mod");
        var calls = 0; mod.RegisterIntegrity("zero", BoardingRuleScope.Both, _ => { calls++; return 0; });
        Assert.Equal(100, f.Rules.Integrity(new(f.Context(), BoardingDamageCause.HostDestroyed, 100, 100)));
        Assert.Equal(0, calls);
        Assert.Equal(0, f.Rules.Integrity(new(f.Context(), BoardingDamageCause.Scuttle, 100, 100)));
    }
    [Fact]
    public void DeterministicOrderAndDisposalDuringEvaluation()
    {
        using var f = new Fixture(); using var a = f.Rules.AcquireProvider("a"); using var b = f.Rules.AcquireProvider("b");
        var calls = new List<string>(); IDisposable? victim = null;
        b.RegisterScuttle("z", BoardingRuleScope.Both, _ => { calls.Add("b"); return true; });
        a.RegisterScuttle("first", BoardingRuleScope.Both, _ => { calls.Add("a"); victim!.Dispose(); return false; });
        victim = a.RegisterScuttle("victim", BoardingRuleScope.Both, _ => throw new Exception("Disposed rule ran."));
        Assert.False(f.Rules.Scuttle(f.Context())); Assert.Equal(new[] { "a", "b" }, calls); Assert.Equal(0, f.Faults);
    }
    [Fact]
    public void NewRegistrationsStartNextEvaluationAndNestedEvaluationUsesVanilla()
    {
        using var f = new Fixture(); using var mod = f.Rules.AcquireProvider("mod"); var installed = false;
        mod.RegisterScuttle("first", BoardingRuleScope.Both, _ =>
        {
            Assert.True(f.Rules.IsEvaluating); Assert.True(f.Rules.Scuttle(f.Context()));
            if (!installed) { installed = true; mod.RegisterScuttle("later", BoardingRuleScope.Both, _ => false); }
            return true;
        });
        Assert.True(f.Rules.Scuttle(f.Context())); Assert.False(f.Rules.Scuttle(f.Context()));
    }
    [Fact]
    public void SessionChangeAndThrowingCallbacksCannotCommitStaleResults()
    {
        using var f = new Fixture(); using var mod = f.Rules.AcquireProvider("mod");
        mod.RegisterEncounter("bad", BoardingRuleScope.Both, _ => throw new Exception());
        mod.RegisterEncounter("replace", BoardingRuleScope.Both, _ => { f.Hub.Invalidate("replaced"); return new(2, 2); });
        Assert.Equal((1f, 1f), f.Rules.Encounter(f.Context())); Assert.Equal(1, f.Faults);
        Assert.False(f.Rules.IsEvaluating);
    }
    [Fact]
    public void ProbabilityOverridesUsePriorityAndConflictingTiesPreserveVanilla()
    {
        using var f = new Fixture(); using var a = f.Rules.AcquireProvider("a"); using var b = f.Rules.AcquireProvider("b");
        var context = new BoardingDisableContext(f.Session, 20, 100, 0);
        a.RegisterDisableChance("chance", _ => .25f, 10);
        b.RegisterDisableChance("lower", _ => .9f, 0);
        Assert.Equal(.25f, f.Rules.Chance(context));
        using var conflict = b.RegisterDisableChance("conflict", _ => .5f, 10);
        Assert.Null(f.Rules.Chance(context));
        var diagnostic = Assert.Single(f.Diagnostics);
        Assert.Contains("a/chance", diagnostic); Assert.Contains("b/conflict", diagnostic);
        Assert.Equal(1, f.Faults); conflict.Dispose();
        Assert.Equal(.25f, f.Rules.Chance(context));
    }
    [Fact]
    public void ExplosionCanBeDeniedWithoutDisablingScuttleAttempts()
    {
        using var f = new Fixture(); using var mod = f.Rules.AcquireProvider("mod");
        mod.RegisterExplosion("reactor", BoardingRuleScope.Ships, _ => false);
        Assert.True(f.Rules.Scuttle(f.Context())); Assert.False(f.Rules.Explosion(f.Context()));
        Assert.True(f.Rules.Explosion(f.Context(BoardingEncounterKind.Installation)));
    }
    [Fact]
    public void ProviderAndLocalIdentitiesAreScopedToOwningInstance()
    {
        using var f = new Fixture(); var first = f.Rules.AcquireProvider("a"); using var other = f.Rules.AcquireProvider("b");
        var old = first.RegisterDisable("same", _ => BoardingDisableDecision.Allow);
        other.RegisterDisable("same", _ => BoardingDisableDecision.Vanilla);
        Assert.Throws<InvalidOperationException>(() => f.Rules.AcquireProvider("a"));
        Assert.Throws<InvalidOperationException>(() => first.RegisterDisable("same", _ => BoardingDisableDecision.Allow));
        first.Dispose(); using var next = f.Rules.AcquireProvider("a");
        next.RegisterDisable("same", _ => BoardingDisableDecision.Allow); old.Dispose();
        Assert.Equal(BoardingDisableDecision.Allow, f.Rules.Disable(new(f.Session, 1, 100, 0)));
    }
}
