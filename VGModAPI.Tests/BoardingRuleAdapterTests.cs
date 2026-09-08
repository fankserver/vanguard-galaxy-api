using System;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingRuleAdapterTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { });
        internal readonly BoardingRuleService Rules;
        internal readonly BoardingRuleAdapter Adapter;
        internal readonly Dictionary<string, object?> Sim = new() { ["kind"] = "HostileShip", ["level"] = 10, ["power"] = 1f, ["health"] = 2f, ["integrity"] = 100f };
        internal readonly Dictionary<string, object?> Location = new() { ["locationKind"] = true, ["locationLevel"] = 10 };
        internal readonly Dictionary<string, object?> Ship = new() { ["destroyed"] = false, ["hull"] = 20f, ["maxHull"] = 100f, ["unitData"] = new Dictionary<string, object?> { ["emp"] = 10f } };
        internal bool Eligible = true, Live = true;
        internal int Conversions, Faults;
        internal object? ConvertedDamage;
        internal Action? OnConvert;
        internal Fixture()
        {
            Rules = new BoardingRuleService(Hub, (_, _) => { });
            Adapter = new BoardingRuleAdapter(Hub, Rules, (key, obj) => ((Dictionary<string, object?>)obj)[key],
                (key, obj, value) => ((Dictionary<string, object?>)obj)[key] = value, _ => Eligible, _ => Live,
                (_, damage, _, _) => { Conversions++; ConvertedDamage = damage; OnConvert?.Invoke(); }, _ => Faults++);
            var session = Hub.Begin(SessionOrigin.SaveLoad, "save"); Hub.PlayerReady(session); Hub.GameplayInitialized(session);
        }
        public void Dispose() { Adapter.Dispose(); Hub.Dispose(); }
    }
    [Fact]
    public void ForceConversionKeepsNativeDamageAndStructuralGuards()
    {
        using var f = new Fixture(); using var provider = f.Rules.AcquireProvider("mod");
        provider.RegisterDisable("always", _ => BoardingDisableDecision.Allow);
        var damage = new object(); f.Eligible = false;
        Assert.Equal(BoardingDisableDecision.Vanilla, f.Adapter.PrepareDisable(f.Ship, damage, out var absent)); Assert.Null(absent);
        f.Eligible = true;
        Assert.Equal(BoardingDisableDecision.Allow, f.Adapter.PrepareDisable(f.Ship, damage, out var plan));
        f.Adapter.Convert(plan!); Assert.Equal(1, f.Conversions); Assert.Same(damage, f.ConvertedDamage);
    }
    [Fact]
    public void NativeConversionExceptionIsNotRetriedOrSwallowed()
    {
        using var f = new Fixture(); using var provider = f.Rules.AcquireProvider("mod");
        provider.RegisterDisable("always", _ => BoardingDisableDecision.Allow);
        f.Adapter.PrepareDisable(f.Ship, new object(), out var plan);
        var error = new InvalidOperationException("native failure"); f.OnConvert = () => throw error;
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => f.Adapter.Convert(plan!)));
        Assert.Equal(1, f.Conversions); Assert.Equal(0, f.Faults);
    }
    [Fact]
    public void DestructionDuringPolicyEvaluationCannotConvertStaleTarget()
    {
        using var f = new Fixture(); using var provider = f.Rules.AcquireProvider("mod");
        provider.RegisterDisable("replace", _ => { f.Live = false; return BoardingDisableDecision.Allow; });
        Assert.Equal(BoardingDisableDecision.Vanilla, f.Adapter.PrepareDisable(f.Ship, new object(), out var plan)); Assert.Null(plan);
    }
    [Fact]
    public void CreationTuningAppliesOnceAndDoesNotTouchRestoredSimulation()
    {
        using var f = new Fixture(); using var provider = f.Rules.AcquireProvider("mod");
        provider.RegisterEncounter("easier", BoardingRuleScope.Ships, _ => new(.5f, .5f));
        f.Adapter.ApplyScaling(f.Sim); Assert.Equal(2f, f.Sim["health"]);
        var scope = f.Adapter.BeginCreation(f.Location, false); f.Adapter.ApplyScaling(f.Sim); f.Adapter.ApplyScaling(f.Sim); f.Adapter.EndScope(scope);
        Assert.Equal(.5f, f.Sim["power"]); Assert.Equal(1f, f.Sim["health"]);
        f.Adapter.ApplyScaling(f.Sim); Assert.Equal(1f, f.Sim["health"]);
    }
    [Fact]
    public void NestedDamageCausesApplyExactlyOnceAndHostDestructionWins()
    {
        using var f = new Fixture(); using var provider = f.Rules.AcquireProvider("mod"); var causes = new List<BoardingDamageCause>();
        provider.RegisterIntegrity("half", BoardingRuleScope.Both, context => { causes.Add(context.Cause); return .5f; });
        var combat = f.Adapter.BeginCause(f.Sim, BoardingDamageCause.Combat);
        var ammo = f.Adapter.BeginCause(f.Sim, BoardingDamageCause.Ammunition);
        Assert.Equal(10, f.Adapter.Damage(f.Sim, 20)); f.Adapter.EndScope(ammo);
        Assert.Equal(10, f.Adapter.Damage(f.Sim, 20));
        var host = f.Adapter.BeginCause(f.Sim, BoardingDamageCause.HostDestroyed);
        var scuttle = f.Adapter.BeginCause(f.Sim, BoardingDamageCause.Scuttle);
        Assert.Equal(20, f.Adapter.Damage(f.Sim, 20));
        f.Adapter.EndScope(scuttle); f.Adapter.EndScope(host); f.Adapter.EndScope(combat);
        Assert.Equal(new[] { BoardingDamageCause.Ammunition, BoardingDamageCause.Combat }, causes);
    }
    [Fact]
    public void ScuttleVetoIsIndependentOfIntegrityMultiplier()
    {
        using var f = new Fixture(); using var provider = f.Rules.AcquireProvider("mod");
        provider.RegisterIntegrity("zero", BoardingRuleScope.Ships, _ => 0);
        Assert.True(f.Adapter.AllowScuttle(f.Sim));
        provider.RegisterScuttle("preventExplosion", BoardingRuleScope.Ships, _ => false);
        Assert.False(f.Adapter.AllowScuttle(f.Sim));
    }
    [Theory]
    [InlineData(.1f, 1f)]
    [InlineData(10f, 100f)]
    [InlineData(0f, 0f)]
    public void HealthOnlyTuningChangesPreEntryHeuristic(float health, float expected)
    {
        using var f = new Fixture(); using var provider = f.Rules.AcquireProvider("mod");
        provider.RegisterEncounter("health", BoardingRuleScope.Ships, _ => new(1, health));
        var scope = f.Adapter.BeginEstimate(f.Location);
        Assert.Equal(expected, f.Adapter.EstimatePower(10));
        f.Adapter.EndScope(scope);
        Assert.Equal(2f, f.Sim["health"]);
    }
    [Fact]
    public void EstimateUsesSameScopedTuningWithoutChangingLiveFields()
    {
        using var f = new Fixture(); using var provider = f.Rules.AcquireProvider("mod");
        provider.RegisterEncounter("hard", BoardingRuleScope.Ships, _ => new(2, 3));
        Assert.Equal(10, f.Adapter.EstimatePower(10));
        var scope = f.Adapter.BeginEstimate(f.Location); Assert.Equal(60, f.Adapter.EstimatePower(10)); f.Adapter.EndScope(scope);
        Assert.Equal(10, f.Adapter.EstimatePower(10)); Assert.Equal(1f, f.Sim["power"]);
    }
}
