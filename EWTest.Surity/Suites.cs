using System;
using System.Linq;
using Surity;
using VGModAPI;

namespace EWTest.Surity;

/// <summary>
/// Surity-based in-game E2E suites for VGModAPI. Surity drives discovery, runs them inside
/// the running game when launched by the Surity CLI (-runSurityTests handshake), streams
/// results back, and exits the game. These tests target the same live behavior as the
/// self-contained EWTest harness, but depend on Surity for the runner plumbing.
///
/// Like EWTest, these are correct-by-inspection against the public Abstractions contract and
/// must be validated on a machine with the game (`make e2e-surity`). Surity's `IEnumerator`
/// test/coroutine support is available for the async gameplay flows documented as follow-ups.
/// </summary>
[TestClass(Only = false, Skip = false)]
public sealed class AvailabilityTests
{
    private static (string, ServiceAvailability)[] _services = Array.Empty<(string, ServiceAvailability)>();

    [BeforeAll]
    public void Capture()
    {
        var s = ModApi.Services;
        _services = new (string, ServiceAvailability)[]
        {
            ("Lifecycle.SessionTracking", s.Lifecycle.SessionTracking.Availability),
            ("Lifecycle.SaveOutcomes", s.Lifecycle.SaveOutcomes.Availability),
            ("World", s.World.Availability),
            ("SaveData", s.SaveData.Availability),
            ("Travel", s.Travel.Availability),
            ("Station", s.Station.Availability),
            ("Missions", s.Missions.Availability),
            ("Dungeons", s.Dungeons.Availability),
            ("Story", s.Story.Availability),
            ("Bars", s.Bars.Availability),
            ("Dialogue", s.Dialogue.Availability),
            ("Game", s.Game.Availability),
            ("Items", s.Items.Availability),
            ("RecipeRegistration", s.RecipeRegistration.Availability),
        };
    }

    [Test]
    public void CoreServicesAreAvailable()
    {
        foreach (var (name, availability) in _services)
        {
            // A service that flips to BindingFailed/UnsupportedGame after a game update fails.
            if (availability.Reason != ServiceUnavailableReason.None &&
                availability.Reason != ServiceUnavailableReason.Disabled)
                throw new InvalidOperationException(
                    $"Service '{name}' unavailable ({availability.Reason}: {availability.Detail}) - " +
                    $"re-inspect its binding in the updated Assembly-CSharp.dll and reconcile BindingCatalog.");
        }
    }
}

[TestClass(Only = false, Skip = false)]
public sealed class WorldAuthoringTests
{
    private const string Prefix = "ewtest-surity";

    private static IWorldProvider? _world;
    private static string? _anchor;

    [BeforeAll]
    public void Setup()
    {
        _world = ModApi.Services.World.AcquireProvider(Plugin.Instance!);
        _anchor = ModApi.Services.Travel.CurrentLocation?.SystemId;
    }

    [AfterAll]
    public void Teardown()
    {
        try { if (_world != null) { _world.Dispose(); _world = null; } } catch { }
    }

    [Test]
    public void RegisterCreateReconcileAndRemove()
    {
        if (_world == null || string.IsNullOrEmpty(_anchor))
            throw new InvalidOperationException("world authoring unavailable / no session; requires a live gameplay session");

        // Declarations
        AssertSucceeded(_world.RegisterPocketSystem(new PocketSystemDefinition(
            Prefix + "-pocket", 1, "E2E Pocket", PocketSystemPlacement.OffMap, null, "E2E Pocket Sector", quiet: true)));
        AssertSucceeded(_world.RegisterWormholePair(new WormholePairDefinition(Prefix + "-hole", 1, "E2E Rift", quiet: true)));
        AssertSucceeded(_world.RegisterResourceSite(ResourceSiteDefinition.MiningField(Prefix + "-mine", 1, "E2E Field", 12, 8)));

        // Create + keyed reconciliation
        var pocket = _world.CreatePocketSystem(Prefix + "-pocket", Prefix + "-pocket-poi", _anchor);
        var hole = _world.CreateWormholePair(Prefix + "-hole", Prefix + "-hole-poi", _anchor, _anchor);
        var mine = _world.CreateResourceSite(Prefix + "-mine", Prefix + "-mine-poi", pocket?.SystemId ?? _anchor, 0, 0);
        if (pocket == null || hole == null || mine == null)
            throw new InvalidOperationException("create returned null (authoring not yet ready)");
        if (!ReferenceEquals(_world.GetPocketSystem(Prefix + "-pocket", Prefix + "-pocket-poi"), pocket))
            throw new InvalidOperationException("pocket not same reconciled instance");

        // Teardown (wormhole first, then thread-backed site/pocket) and confirm removal.
        AssertSucceeded(mine.Remove());
        AssertSucceeded(hole.Remove());
        AssertSucceeded(pocket.Remove());
    }

    private static void AssertSucceeded(WorldContentResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Status + ": " + result.Detail +
                " - re-inspect the world-content binding in the updated Assembly-CSharp.dll.");
    }
}

[TestClass(Only = false, Skip = false)]
public sealed class DungeonTests
{
    [Test]
    public void RegisterMinimalAuthoredDungeon()
    {
        IDungeonProvider? provider = null;
        try
        {
            provider = ModApi.Services.Dungeons.AcquireProvider("ewtest-surity");
            if (provider == null) throw new InvalidOperationException("dungeon service unavailable in this session");
            var airlock = new DungeonCompartmentDefinition("airlock", CompartmentType.Airlock, new[] { "corridor" });
            var corridor = new DungeonCompartmentDefinition("corridor", CompartmentType.Corridor, new[] { "airlock" });
            var layout = new DungeonLayout(new[] { airlock, corridor });
            var ev = new DungeonEventDefinition("reveal", "corridor", "Corridor",
                new[] { new DungeonChoiceDefinition("proceed", "Proceed") });
            var definition = new DungeonDefinition(1, "E2E Dungeon", layout, events: new[] { ev });
            using var registration = provider.Register("ewtest-surity-dungeon", definition, allowChoice: null);
            if (registration == null) throw new InvalidOperationException("Register returned null");
        }
        finally
        {
            try { provider?.Dispose(); } catch { }
        }
    }
}

[TestClass(Only = false, Skip = false)]
public sealed class LifecycleTests
{
    [Test]
    public void SessionWasReached()
    {
        // PlayerReady/GameplayInitialized come from the API lifecycle service mirroring the current
        // game. A session must be entered for the gameplay suites; reaching it is the scripted-entry
        // control point documented in docs/development/e2e-tests.md.
        var lifecycle = ModApi.Services.Lifecycle;
        if (lifecycle.CurrentSession?.Phase != SessionPhase.GameplayInitialized)
            throw new InvalidOperationException(
                "no gameplay session reached. End-to-end gameplay suites require session-entry " +
                "automation (launching into / auto-continuing a disposable save).");
    }
}
