using System;
using VGModAPI;

namespace EWTest.Suites;

/// <summary>
/// Live world-authoring round trip against the running game: register declarations, create an
/// owned pocket + wormhole pair + resource site anchored to the player's current system, verify
/// keyed reconciliation returns the same object, then remove each and confirm the removal was
/// applied. This exercises the deepest native integration (system/POI creation, ownership,
/// persistence, teardown) and is the most likely live surface a game update could break.
/// Rendered against a disposable save via `make e2e`.
/// </summary>
public sealed class WorldAuthoringSuite
{
    private readonly Plugin _plugin;
    public WorldAuthoringSuite(Plugin plugin) => _plugin = plugin;

    // Author-local, provider-agnostic identity namespace. The API owns every native identity.
    private const string ProviderPrefix = "ewtest";

    public SuiteResult Run()
    {
        var suite = new SuiteResult { Id = "world-authoring", Name = "Live world authoring round trip" };

        IWorldProvider? world = null;
        try
        {
            world = ModApi.Services.World.AcquireProvider(_plugin);
            if (world == null)
            {
                suite.Results.Add(Check.Skip("world provider", "world authoring unavailable in this session"));
                return suite;
            }

            var anchor = ModApi.Services.Travel.CurrentLocation?.SystemId;
            if (string.IsNullOrEmpty(anchor))
            {
                suite.Results.Add(Check.Skip("anchor system", "no current travel location; authoring requires a live session"));
                return suite;
            }

            // --- declarations -------------------------------------------------
            suite.Results.Add(Check.Run("register pocket", "Succeeded", () =>
            {
                var status = world.RegisterPocketSystem(new PocketSystemDefinition(
                    ProviderPrefix + "-pocket", 1, "E2E Pocket", PocketSystemPlacement.OffMap,
                    factionId: null, sectorName: "E2E Pocket Sector", quiet: true));
                if (status != WorldContentStatus.Succeeded) throw new InvalidOperationException("register pocket: " + status);
            }, "Re-inspect pocket-system registration + BindingCatalog.Pocket bindings."));

            suite.Results.Add(Check.Run("register wormhole pair", "Succeeded", () =>
            {
                var status = world.RegisterWormholePair(new WormholePairDefinition(
                    ProviderPrefix + "-hole", 1, "E2E Rift", quiet: true));
                if (status != WorldContentStatus.Succeeded) throw new InvalidOperationException("register wormhole pair: " + status);
            }, "Re-inspect wormhole-pair registration + wormhole bindings."));

            suite.Results.Add(Check.Run("register resource site", "Succeeded", () =>
            {
                var status = world.RegisterResourceSite(
                    ResourceSiteDefinition.MiningField(ProviderPrefix + "-mine", 1, "E2E Field", 12, 8));
                if (status != WorldContentStatus.Succeeded) throw new InvalidOperationException("register resource site: " + status);
            }, "Re-inspect resource-site registration + resource-site bindings."));

            // --- create + keyed reconciliation ------------------------------
            IPocketSystem? pocket = null;
            IWormholePair? hole = null;
            IResourceSite? mine = null;
            suite.Results.Add(Check.Run("create pocket + wormhole + site", "non-null objects", () =>
            {
                pocket = world.CreatePocketSystem(ProviderPrefix + "-pocket", ProviderPrefix + "-pocket-poi", anchor);
                hole = world.CreateWormholePair(ProviderPrefix + "-hole", ProviderPrefix + "-hole-poi", anchor, anchor);
                mine = world.CreateResourceSite(ProviderPrefix + "-mine", ProviderPrefix + "-mine-poi", pocket?.SystemId ?? anchor, 0, 0);
                if (pocket == null || hole == null || mine == null)
                    throw new InvalidOperationException("create returned null (authoring not yet ready)");
            }, "Re-inspect live creation: world must be authorable at the current session boundary."));

            suite.Results.Add(Check.Run("keyed reconciliation is stable identity", "same object instance", () =>
            {
                if (pocket == null || hole == null || mine == null) throw new InvalidOperationException("creation did not complete");
                if (!ReferenceEquals(world.GetPocketSystem(ProviderPrefix + "-pocket", ProviderPrefix + "-pocket-poi"), pocket))
                    throw new InvalidOperationException("pocket not same instance");
                if (!ReferenceEquals(world.GetWormholePair(ProviderPrefix + "-hole", ProviderPrefix + "-hole-poi"), hole))
                    throw new InvalidOperationException("wormhole pair not same instance");
                if (!ReferenceEquals(world.GetResourceSite(ProviderPrefix + "-mine", ProviderPrefix + "-mine-poi"), mine))
                    throw new InvalidOperationException("resource site not same instance");
            }, "Re-inspect the world-content keyed reconciliation cache in the updated build."));

            // Reconstruction is asynchronous; record observed state (informational) rather than
            // asserting instant readiness we cannot claim reliably.
            if (pocket != null)
                suite.Results.Add(Check.Pass("pocket reconstruction state", pocket.State.Status + (pocket.State.SystemId != null ? " (" + pocket.State.SystemId + ")" : "")));

            // --- teardown -----------------------------------------------------
            suite.Results.Add(RemoveCheck("remove resource site", () => mine?.Remove(), "resource site bindings"));
            suite.Results.Add(RemoveCheck("remove wormhole pair", () => hole?.Remove(), "wormhole-pair Remove()/dissolve bindings"));
            suite.Results.Add(RemoveCheck("remove pocket", () => pocket?.Remove(), "pocket Remove() teardown bindings"));
        }
        catch (Exception ex)
        {
            suite.Results.Add(Check.Fail("world-authoring suite", ex.GetType().Name + ": " + ex.Message,
                "no exception", "Unexpected live failure; inspect the updated game build's world-content integration."));
        }
        finally
        {
            try { world?.Dispose(); } catch { }
        }
        return suite;
    }

    private static CheckResult RemoveCheck(string name, Func<WorldContentResult?> remove, string bindingHint)
        => Check.Run(name, "Succeeded", () =>
        {
            var result = remove();
            if (result == null || !result.Succeeded)
                throw new InvalidOperationException((result?.Status.ToString() ?? "no object") + ": " + (result?.Detail ?? ""));
        }, "Re-inspect " + bindingHint + " in the updated Assembly-CSharp.dll.");
}
