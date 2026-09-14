using System;
using System.Collections.Generic;

namespace VGModAPI.E2E;

/// <summary>
/// Live E2E for the actual examples/StationCommerce plugin. The example is built twice as variants A
/// and B that register the SAME author-local IDs through separately authenticated providers, so this
/// case asserts the composition property that is the whole demonstration: both variants register
/// their trade good, recipe and story errand independently, neither clobbering the other, and the
/// recipe-before-item declaration resolves (PendingDependencies may resolve, never a hard failure).
/// Bar-contact placement additionally needs a visited station, so it is not part of this fresh-game
/// case.
/// </summary>
internal static class StationCommerceCase
{
    internal const string Id = "station-commerce";
    internal const string PluginIdA = "vgmodapi.example.station-commerce-a";
    internal const string PluginIdB = "vgmodapi.example.station-commerce-b";

    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events)
    {
        var steps = new List<TestStep>(LiveBoot.Steps(PluginIdA, lifecycle, events));
        steps.AddRange(new[]
        {
            new TestStep("both independent author variants are loaded", "Chainloader.PluginInfos[station-commerce-*]", () =>
                NativeGameplay.PluginInstance(PluginIdA) != null && NativeGameplay.PluginInstance(PluginIdB) != null),
            new TestStep("variant A registers its owned good and errand", "StationCommerceA.Start / OwnedItemProvider.Register", () =>
                RegisteredGood(PluginIdA) && RegisteredErrand(PluginIdA)),
            new TestStep("variant B registers the SAME author-local IDs without collision", "StationCommerceB.Start / OwnedItemProvider.Register", () =>
                RegisteredGood(PluginIdB) && RegisteredErrand(PluginIdB)),
            new TestStep("recipe-before-item declaration resolves, never a hard failure", "OwnedRecipeProvider.Register / owned dependency", () =>
                RecipeResolves(PluginIdA) && RecipeResolves(PluginIdB)),
        });
        return steps;
    }

    private static bool RegisteredGood(string pluginId)
    {
        var p = NativeGameplay.PluginInstance(pluginId)
            ?? throw new InvalidOperationException(pluginId + " not loaded.");
        // OwnedItemStatus.Succeeded is the deterministic success; a Duplicate here would mean the
        // two variants' identical local ids collided, which is the failure mode we are asserting away.
        return (string)NativeGameplay.GetField(p, "_itemStatus")! == "Succeeded";
    }

    private static bool RegisteredErrand(string pluginId)
    {
        var p = NativeGameplay.PluginInstance(pluginId)!;
        return NativeGameplay.GetField(p, "_errand") != null;
    }

    private static bool RecipeResolves(string pluginId)
    {
        var p = NativeGameplay.PluginInstance(pluginId)!;
        var status = (string)NativeGameplay.GetField(p, "_recipeStatus")!;
        if (status is "Succeeded" or "PendingDependencies") return true;
        throw new InvalidOperationException(pluginId + " recipe failed to resolve: " + status);
    }
}
