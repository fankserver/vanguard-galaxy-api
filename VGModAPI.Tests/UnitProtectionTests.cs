using System;
using System.Collections.Generic;
using Behaviour.Unit;
using Source.Data;
using Source.Mining;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using Xunit;

namespace VGModAPI.Tests;

public sealed class UnitProtectionTests : IDisposable
{
    private readonly List<Exception> _errors = new();
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly UnitProtectionService _service;
    private readonly UnitProtectionRuntime _runtime;
    private readonly TestUnit _ally = new(), _lookalike = new();

    public UnitProtectionTests()
    {
        _service = new UnitProtectionService(_hub); _service.SetAvailable(true);
        _runtime = new UnitProtectionRuntime(typeof(AbstractUnit).Assembly, _service, _errors.Add);
        _ally.unitData.SetTestGuid("foundations-promise");
        _ally.unitData.currentHullHP = 500; _ally.unitData.currentArmorHP = 200; _ally.unitData.currentShieldHP = 300;
        _lookalike.unitData.SetTestGuid("same-class-other-ship");
        _lookalike.unitData.currentHullHP = 500;
    }

    private static void NativeLethalDamage(TestUnit unit)
    {
        // The inspected native flow: pools drain, destruction latches, then the invincibility
        // clamp rescues the hull before the death branch, leaving the destroyed latch behind.
        unit.unitData.currentShieldHP = 0; unit.unitData.currentArmorHP = 0;
        unit.unitData.currentHullHP = 0; unit.unitData.empCharge = 40;
        unit.unitData.battleDamage.Add(new SpriteBreakPoint());
        unit.SetTestDestroyed(true);
        if (unit.isInvincible && unit.unitData.currentHullHP <= 0) unit.unitData.currentHullHP = 1;
        else throw new InvalidOperationException("Lethal damage destroyed the unit.");
    }

    [Fact]
    public void ProtectedUnitSurvivesLethalDamageWithItsRecordedConditionAndFlagsRestored()
    {
        using var declaration = _service.Protect("foundations-promise");
        var condition = _runtime.Enter(_ally);
        Assert.NotNull(condition); Assert.True(_ally.isInvincible);
        NativeLethalDamage(_ally);
        _runtime.Exit(condition);
        Assert.False(_ally.isInvincible); Assert.False(_ally.isDestroyed);
        Assert.Equal(500, _ally.unitData.currentHullHP); Assert.Equal(200, _ally.unitData.currentArmorHP);
        Assert.Equal(300, _ally.unitData.currentShieldHP); Assert.Equal(0, _ally.unitData.empCharge);
        Assert.Empty(_ally.unitData.battleDamage);
        Assert.Empty(_errors);
    }

    [Fact]
    public void ExactIdentityScopingLeavesLookalikesAndUnprotectedUnitsVanilla()
    {
        using var declaration = _service.Protect("foundations-promise");
        Assert.Null(_runtime.Enter(_lookalike));
        Assert.Null(_runtime.Enter(null));
        Assert.Null(_runtime.Enter(new object()));
        Assert.False(_lookalike.isInvincible);
        Assert.False(_service.IsProtected("same-class-other-ship"));
        Assert.False(_service.IsProtected(null));
    }

    [Fact]
    public void ProtectionRebindsToTheRematerialisedInstanceCarryingTheSameIdentity()
    {
        using var declaration = _service.Protect("foundations-promise");
        Assert.NotNull(_runtime.Enter(_ally));
        // Save/load rebuilds the live object; the persistent identity is what stays stable.
        var reloaded = new TestUnit();
        reloaded.unitData.SetTestGuid("foundations-promise");
        var condition = _runtime.Enter(reloaded);
        Assert.NotNull(condition); Assert.True(reloaded.isInvincible);
        _runtime.Exit(condition);
        Assert.False(reloaded.isInvincible);
    }

    [Fact]
    public void DisposalAndUnavailabilityRestoreVanillaLethalityWithoutDiscardingDeclarations()
    {
        var declaration = _service.Protect("foundations-promise");
        declaration.Dispose(); declaration.Dispose();
        Assert.Null(_runtime.Enter(_ally));
        using var retained = _service.Protect("foundations-promise");
        _service.SetAvailable(false);
        Assert.Null(_runtime.Enter(_ally));
        _service.SetAvailable(true);
        Assert.NotNull(_runtime.Enter(_ally));
    }

    [Fact]
    public void ForeignFlagsAndPriorDestructionAreNeverOverwritten()
    {
        using var declaration = _service.Protect("foundations-promise");
        _ally.isInvincible = true; // Another mod's standing flag is preserved, not cleared.
        _ally.SetTestDestroyed(true); // Already-destroyed units are not resurrected.
        var condition = _runtime.Enter(_ally);
        _ally.unitData.currentHullHP = 0;
        _runtime.Exit(condition);
        Assert.True(_ally.isInvincible);
        Assert.True(_ally.isDestroyed);
        Assert.Equal(500, _ally.unitData.currentHullHP);
    }

    [Fact]
    public void PatchEntryPointsBracketOnlyProtectedUnitsAndNeverSwallowVanillaExceptions()
    {
        UnitProtectionPatches.Runtime = _runtime;
        using var declaration = _service.Protect("foundations-promise");
        UnitProtectionPatches.Damage.Prefix(_ally, out var state);
        Assert.NotNull(state); Assert.True(_ally.isInvincible);
        NativeLethalDamage(_ally);
        var vanillaFault = new InvalidOperationException("native");
        Assert.Same(vanillaFault, UnitProtectionPatches.Damage.Finalizer(state, vanillaFault));
        Assert.False(_ally.isDestroyed); Assert.Equal(500, _ally.unitData.currentHullHP);
        UnitProtectionPatches.Damage.Prefix(_lookalike, out var unprotectedState);
        Assert.Null(unprotectedState);
        Assert.Null(UnitProtectionPatches.Damage.Finalizer(null, null));
        UnitProtectionPatches.Runtime = null;
        UnitProtectionPatches.Damage.Prefix(_ally, out state);
        Assert.Null(state);
    }

    [Fact]
    public void ForeignThreadDamageIsContainedReportedOnceAndFailsOpen()
    {
        using var declaration = _service.Protect("foundations-promise");
        var entered = new List<object?>();
        Assert.Null(ServiceNotificationTests.OnWorker(() =>
        {
            entered.Add(_runtime.Enter(_ally));
            entered.Add(_runtime.Enter(_ally));
        }));
        Assert.Equal(new object?[] { null, null }, entered);
        Assert.Single(_errors);
        Assert.False(_ally.isInvincible);
        Assert.NotNull(_runtime.Enter(_ally));
    }

    [Fact]
    public void InvalidIdentitiesThreadViolationsAndDisposedServiceAreProgrammingErrors()
    {
        Assert.Throws<ArgumentException>(() => _service.Protect(" "));
        Assert.Throws<ArgumentException>(() => _service.Protect("a\u0007b"));
        Assert.Throws<ArgumentException>(() => _service.Protect(new string('x', 5000)));
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => _service.Protect("id")));
        var declaration = _service.Protect("foundations-promise");
        _service.Dispose();
        Assert.Equal(ServiceUnavailableReason.ApiStopped, _service.Availability.Reason);
        Assert.False(_service.IsProtected("foundations-promise"));
        Assert.Throws<ObjectDisposedException>(() => _service.Protect("foundations-promise"));
        declaration.Dispose(); _service.Dispose();
    }

    public void Dispose()
    {
        UnitProtectionPatches.Runtime = null;
        _service.Dispose(); _hub.Dispose();
    }
}
