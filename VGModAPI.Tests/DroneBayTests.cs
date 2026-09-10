using System;
using System.Collections.Generic;
using System.Linq;
using Behaviour.Equipment.Module;
using Behaviour.Unit;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DroneBayTests : IDisposable
{
    private readonly List<Exception> _reports = new();
    private readonly List<string> _notices = new();
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly DroneBayService _service;
    private readonly DroneBayRuntime _runtime;
    private readonly List<object> _ships = new();
    private readonly TestUnit _boss = new(), _playerShip = new();
    private readonly DroneBayModule _bossBay = new(), _playerBay = new();
    private readonly Guid _session;
    private double _now;

    public DroneBayTests()
    {
        _hub.SetCapability("session-lifecycle", true, "Test binding.");
        _service = new DroneBayService(_hub); _service.SetAvailable(true);
        _runtime = new DroneBayRuntime(typeof(DroneBayModule).Assembly, _hub, _service,
            () => _ships.ToArray(),
            unit => ReferenceEquals(unit, _boss) ? _bossBay : ReferenceEquals(unit, _playerShip) ? _playerBay
                : _bindings.FirstOrDefault(binding => ReferenceEquals(binding.Unit, unit)).Bay,
            drone => ((Drone)drone).DroneName = "destroyed",
            _notices.Add, _reports.Add);
        _session = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(_session); _hub.GameplayInitialized(_session);
        // The boss shares its class with the player's ship; identity is the persistent data guid.
        _boss.unitData.SetTestGuid("boss-redemption"); _bossBay.parent = _boss;
        _playerShip.unitData.SetTestGuid("player-redemption"); _playerBay.parent = _playerShip;
        _ships.AddRange(new object[] { _boss, _playerShip });
        Drone.TestCatalog["Combat Laser Drone"] = new Drone { DroneName = "laser" };
        Drone.TestCatalog["Combat Missile Drone"] = new Drone { DroneName = "missile" };
    }
    private void Advance() { _now += 1; _runtime.Tick(_now); }

    [Fact]
    public void LaunchTimingIsScopedToTheExactUnitAndDisposalRestoresVanillaImmediately()
    {
        var declaration = _service.Tune("boss-redemption", new DroneBayTuning(launchSeconds: 0.05));
        Assert.Equal(0.05, _runtime.LaunchSeconds(_bossBay));
        Assert.Null(_runtime.LaunchSeconds(_playerBay)); // Same class, different instance: vanilla.
        Assert.Null(_runtime.LaunchSeconds(new DroneBayModule())); // Parentless bay: vanilla.
        Assert.Null(_runtime.LaunchSeconds(null));
        declaration.Dispose();
        Assert.Null(_runtime.LaunchSeconds(_bossBay));
        Assert.Empty(_reports);
    }

    [Fact]
    public void ReplacementRollCyclesTheAuthoredCompositionDeterministically()
    {
        using var declaration = _service.Tune("boss-redemption",
            new DroneBayTuning(replacementDrones: new[] { "Combat Missile Drone", "Combat Laser Drone" }));
        Assert.Equal("missile", ((Drone)_runtime.ReplacementPrefab(_bossBay, 0)!).DroneName);
        Assert.Equal("laser", ((Drone)_runtime.ReplacementPrefab(_bossBay, 1)!).DroneName);
        Assert.Equal("missile", ((Drone)_runtime.ReplacementPrefab(_bossBay, 2)!).DroneName);
        Assert.Null(_runtime.ReplacementPrefab(_playerBay, 0)); // The player's own bay rolls vanilla.
        Assert.Empty(_reports);
    }

    [Fact]
    public void UnknownReplacementNamesAreSkippedWithOneReportAndAllUnknownFallsBackToVanilla()
    {
        using var declaration = _service.Tune("boss-redemption",
            new DroneBayTuning(replacementDrones: new[] { "Missing Drone", "Combat Laser Drone" }));
        Assert.Equal("laser", ((Drone)_runtime.ReplacementPrefab(_bossBay, 0)!).DroneName);
        Assert.Equal("laser", ((Drone)_runtime.ReplacementPrefab(_bossBay, 1)!).DroneName);
        Assert.Single(_reports);
        using var allUnknown = _service.Tune("boss-redemption",
            new DroneBayTuning(replacementDrones: new[] { "Missing Drone", "Also Missing" }));
        Assert.Null(_runtime.ReplacementPrefab(_bossBay, 0));
        Assert.Equal(2, _reports.Count); // "Also Missing" reported once; "Missing Drone" not repeated.
    }

    [Fact]
    public void ComplementRebuildsThroughNativeInitialisationInStaggeredBatchesThenDeploys()
    {
        var stale = new Drone { DroneName = "stale" };
        _bossBay.drones.Add(stale);
        using var declaration = _service.Tune("boss-redemption", new DroneBayTuning(complement: 25));
        Advance();
        Assert.Equal("destroyed", stale.DroneName);
        Assert.Equal(25, _bossBay._droneAmount); Assert.Equal(0, _bossBay.droneBonusAmount);
        Assert.Equal(10, _bossBay.AddedIndices.Count); Assert.False(_bossBay.shouldDeploy);
        Advance(); Advance();
        Assert.Equal(25, _bossBay.AddedIndices.Count);
        Assert.Equal(Enumerable.Range(0, 25), _bossBay.AddedIndices);
        Assert.True(_bossBay.shouldDeploy);
        Assert.Single(_notices);
        Assert.Empty(_playerBay.AddedIndices); Assert.Equal(3, _playerBay.droneBonusAmount);
        var added = _bossBay.AddedIndices.Count;
        Advance();
        Assert.Equal(added, _bossBay.AddedIndices.Count); // Built once, not rebuilt every tick.
    }

    [Fact]
    public void ComplementWaitsForTheUnitAndRestartsOnlyOnSessionReplacement()
    {
        using var declaration = _service.Tune("late-boss", new DroneBayTuning(complement: 5));
        Advance();
        Assert.Empty(_notices);
        var late = new TestUnit(); late.unitData.SetTestGuid("late-boss");
        var lateBay = new DroneBayModule { parent = late };
        _ships.Add(late); _bindings.Add((late, lateBay));
        Advance();
        Assert.Equal(5, lateBay.AddedIndices.Count); Assert.True(lateBay.shouldDeploy);
        lateBay.AddedIndices.Clear(); lateBay.shouldDeploy = false;
        Advance();
        Assert.Empty(lateBay.AddedIndices); // Same session: never rebuilt.
        var replacement = _hub.Begin(SessionOrigin.SaveLoad, "save"); _hub.PlayerReady(replacement); _hub.GameplayInitialized(replacement);
        Advance();
        Assert.Equal(5, lateBay.AddedIndices.Count); // New session: the authored complement re-applies.
    }
    private readonly List<(TestUnit Unit, DroneBayModule Bay)> _bindings = new();

    [Fact]
    public void AmbiguousIdentityMissingBayAndUnavailabilityTuneNothing()
    {
        var twin = new TestUnit(); twin.unitData.SetTestGuid("boss-redemption");
        _ships.Add(twin);
        using var declaration = _service.Tune("boss-redemption", new DroneBayTuning(launchSeconds: 0.05, complement: 5));
        // Launch scoping reads the bay's own parent identity and is unaffected by world ambiguity.
        Assert.Equal(0.05, _runtime.LaunchSeconds(_bossBay));
        Advance();
        Assert.Empty(_bossBay.AddedIndices); // Ambiguous world identity builds nothing.
        _ships.Remove(twin);
        _service.SetAvailable(false); // Unavailable integration tunes nothing anywhere.
        Assert.Null(_runtime.LaunchSeconds(_bossBay));
        Advance();
        Assert.Empty(_bossBay.AddedIndices);
        _service.SetAvailable(true);
        Advance();
        Assert.Equal(5, _bossBay.AddedIndices.Count);
    }

    [Fact]
    public void LaterDeclarationsOverridePerAspectAndValidationRejectsProgrammingErrors()
    {
        using var first = _service.Tune("boss-redemption",
            new DroneBayTuning(launchSeconds: 0.05, replacementDrones: new[] { "Combat Laser Drone" }));
        using var second = _service.Tune("boss-redemption", new DroneBayTuning(launchSeconds: 0.2));
        Assert.Equal(0.2, _runtime.LaunchSeconds(_bossBay));
        Assert.Equal("laser", ((Drone)_runtime.ReplacementPrefab(_bossBay, 0)!).DroneName);
        second.Dispose();
        Assert.Equal(0.05, _runtime.LaunchSeconds(_bossBay));
        Assert.Throws<ArgumentException>(() => new DroneBayTuning());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DroneBayTuning(launchSeconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DroneBayTuning(complement: 0));
        Assert.Throws<ArgumentException>(() => new DroneBayTuning(replacementDrones: Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => _service.Tune(" ", new DroneBayTuning(complement: 1)));
        Assert.Throws<ArgumentNullException>(() => _service.Tune("id", null!));
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(
            () => _service.Tune("id", new DroneBayTuning(complement: 1))));
        var declaration = _service.Tune("boss-redemption", new DroneBayTuning(complement: 1));
        _service.Dispose();
        Assert.Equal(ServiceUnavailableReason.ApiStopped, _service.Availability.Reason);
        Assert.Null(_service.EffectiveFor("boss-redemption"));
        Assert.Throws<ObjectDisposedException>(() => _service.Tune("id", new DroneBayTuning(complement: 1)));
        declaration.Dispose();
    }

    [Fact]
    public void PatchEntryPointsBracketOnlyDeclaredBaysAndFailOpenWithoutARuntime()
    {
        DroneBayPatches.Runtime = _runtime;
        using var declaration = _service.Tune("boss-redemption",
            new DroneBayTuning(launchSeconds: 0.05, replacementDrones: new[] { "Combat Laser Drone" }));
        var duration = 1.5f;
        Assert.False(DroneBayPatches.LaunchDuration.Prefix(_bossBay, ref duration));
        Assert.Equal(0.05f, duration);
        duration = 1.5f;
        Assert.True(DroneBayPatches.LaunchDuration.Prefix(_playerBay, ref duration));
        Assert.Equal(1.5f, duration);
        object? prefab = null;
        Assert.False(DroneBayPatches.Replacement.Prefix(_bossBay, 0, ref prefab));
        Assert.Equal("laser", ((Drone)prefab!).DroneName);
        prefab = null;
        Assert.True(DroneBayPatches.Replacement.Prefix(_playerBay, 0, ref prefab));
        Assert.Null(prefab);
        DroneBayPatches.Runtime = null;
        Assert.True(DroneBayPatches.LaunchDuration.Prefix(_bossBay, ref duration));
        Assert.True(DroneBayPatches.Replacement.Prefix(_bossBay, 0, ref prefab));
    }

    public void Dispose()
    {
        DroneBayPatches.Runtime = null;
        Drone.TestCatalog.Clear();
        _service.Dispose(); _hub.Dispose();
    }
}
