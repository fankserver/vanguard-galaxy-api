using System;
using VGModAPI;

namespace EWTest.Suites;

/// <summary>Asserts the core services report availability on the supported build, and records every
/// other service's observed availability so a cross-build diff is visible. The availability status is
/// the API's own binding-health signal: a service that flips to <see cref="ServiceUnavailableReason.BindingFailed"/>
/// or <see cref="ServiceUnavailableReason.UnsupportedGame"/> after a game update is a real break and fails,
/// while intentional configuration disables are recorded as skips.</summary>
public sealed class AvailabilitySuite
{
    private readonly Plugin _plugin;
    public AvailabilitySuite(Plugin plugin) => _plugin = plugin;

    public SuiteResult Run()
    {
        var suite = new SuiteResult { Id = "availability", Name = "Public service availability" };
        void Inspect(string name, IServiceStatus? status, string bindingHint)
        {
            if (status == null)
                suite.Results.Add(Check.Fail(name, "service reference is null",
                    "non-null reference",
                    "API did not publish the service; this is an API regression, not a game change."));
            else
                suite.Results.Add(Check.Availability(name, status.Availability, bindingHint));
        }

        if (_plugin.Lifecycle == null)
        {
            suite.Results.Add(Check.Fail("lifecycle", "lifecycle service unavailable at boot",
                "lifecycle service reachable", "API did not publish lifecycle in time; an API regression."));
            return suite;
        }

        var services = ModApi.Services;
        Inspect("Lifecycle.SessionTracking", services.Lifecycle.SessionTracking, "BindingCatalog.Session");
        Inspect("Lifecycle.SaveOutcomes", services.Lifecycle.SaveOutcomes, "BindingCatalog.Saves");
        Inspect("World", services.World, "world-content bindings");
        Inspect("SaveData", services.SaveData, "save-data bindings");
        Inspect("Travel", services.Travel, "TravelNativeAdapter + travel bindings");
        Inspect("Station", services.Station, "station bindings");
        Inspect("Missions", services.Missions, "mission bindings");
        Inspect("Dungeons", services.Dungeons, "dungeon bindings");
        Inspect("Story", services.Story, "story bindings");
        Inspect("Bars", services.Bars, "bar bindings");
        Inspect("Dialogue", services.Dialogue, "dialogue bindings");
        Inspect("Items", services.Items, "owned-item bindings");
        Inspect("RecipeRegistration", services.RecipeRegistration, "owned-recipe bindings");
        return suite;
    }
}
