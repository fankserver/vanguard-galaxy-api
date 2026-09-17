using System;
using System.Collections.Generic;
using System.Linq;

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
            new TestStep("the maker's tip renders once per author on the owned good's tooltip", "Tooltips.RegisterItem / UITooltip.SetContent", 30, MakerTipRenders),
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

    private static StepResult MakerTipRenders()
    {
        const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        object? Get(object o, string name) => o.GetType().GetProperty(name, All)?.GetValue(o) ?? o.GetType().GetField(name, All)?.GetValue(o);
        var a = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == "Assembly-CSharp");
        var itemType = a.GetType("Behaviour.Item.InventoryItemType", true)!;
        UnityEngine.Component? good = UnityEngine.Resources.FindObjectsOfTypeAll(itemType).OfType<UnityEngine.Component>()
            .FirstOrDefault(i => (string?)Get(i, "displayName") == "Silo Container");
        if (good == null)
        {
            // Same lazy path the game itself uses: a lookup of the owned identifier runs the API's
            // resolve prefix, which materializes the catalog entry.
            var definitions = ModApi.Services.Items.GetType().GetProperty("Definitions", All)!.GetValue(ModApi.Services.Items) as System.Collections.IEnumerable;
            var identity = definitions?.Cast<object>().FirstOrDefault(d => (string)d.GetType().GetProperty("Owner", All)!.GetValue(d)! == PluginIdA);
            if (identity == null) return StepResult.Wait("variant A definition not in the service yet");
            itemType.GetMethod("Get", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!.Invoke(null, new object[] { (string)identity.GetType().GetProperty("NativeId", All)!.GetValue(identity)! });
            good = UnityEngine.Resources.FindObjectsOfTypeAll(itemType).OfType<UnityEngine.Component>()
                .FirstOrDefault(i => (string?)Get(i, "displayName") == "Silo Container");
            if (good == null) return StepResult.Wait("owned good lookup did not materialize it");
        }
        var tooltipType = a.GetType("Behaviour.UI.UITooltip", true)!;
        var prefab = UnityEngine.Resources.FindObjectsOfTypeAll(tooltipType).OfType<UnityEngine.Component>().First(t => Get(t, "_textPrefab") != null);
        var tipGo = UnityEngine.Object.Instantiate(prefab.gameObject); tipGo.SetActive(false);
        var sourceGo = new UnityEngine.GameObject("commerce-e2e-item"); sourceGo.SetActive(false);
        try
        {
            var sourceType = a.GetType("Behaviour.UI.Tooltip.ItemTooltipSource", true)!;
            var source = sourceGo.AddComponent(sourceType);
            var context = Enum.Parse(a.GetType("Behaviour.UI.Tooltip.ItemTooltipContext", true)!, "InInventory");
            sourceType.GetMethod("SetItem", All)!.Invoke(source, new object?[] { good, 1, false, context, false, null });
            var tooltip = tipGo.GetComponent(tooltipType)!;
            tooltipType.GetProperty("Source", All)!.SetValue(tooltip, source, null); // Show() normally wires this before filling.
            tooltipType.GetMethod("SetContent", All)!.Invoke(tooltip, new object[] { source });
            var lines = ((System.Collections.IEnumerable)Get(tooltip, "_contentList")!).Cast<object>()
                .Select(c => c.GetType().GetProperty("Text", All)?.GetValue(c)).Where(t => t != null)
                .Select(t => (string)t!.GetType().GetProperty("text", All)!.GetValue(t)!).ToArray();
            foreach (var variant in new[] { "A", "B" })
                Require(lines.Count(t => t.Contains($"Handmade by Station Commerce {variant}") && t.Contains("<color=")) == 1,
                    $"variant {variant} maker tip missing, duplicated or unstyled: " + string.Join("|", lines.Where(t => t.Contains("Handmade"))));
            UnityEngine.Debug.Log("StationCommerce E2E: both independent authors tipped their own good's tooltip exactly once.");
            return StepResult.Pass("Maker's tip verified");
        }
        finally { UnityEngine.Object.Destroy(tipGo); UnityEngine.Object.Destroy(sourceGo); }
    }
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static bool RecipeResolves(string pluginId)
    {
        var p = NativeGameplay.PluginInstance(pluginId)!;
        var status = (string)NativeGameplay.GetField(p, "_recipeStatus")!;
        if (status is "Succeeded" or "PendingDependencies") return true;
        throw new InvalidOperationException(pluginId + " recipe failed to resolve: " + status);
    }
}
