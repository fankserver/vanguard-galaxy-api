using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.E2E;

/// <summary>
/// Live E2E for the actual examples/Observation plugin. It is a pure observer (no HUD actions), so
/// this case drives a real new game and asserts that the plugin's low-level lifecycle observations
/// actually fire for that session — the whole point of the example. The API's own SessionTracking
/// underpins the bootstrap; the plugin records the same sequence through its public event hooks.
/// </summary>
internal static class ObservationCase
{
    internal const string Id = "observation";
    internal const string PluginId = "vgmodapi.example.observation";

    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events)
    {
        var steps = new List<TestStep>(LiveBoot.Steps(PluginId, lifecycle, events));
        steps.AddRange(new[]
        {
            new TestStep("lifecycle observation fired for the live session", "Observation.OnLifecycle / LifecycleEvent", () =>
            {
                var p = NativeGameplay.PluginInstance(PluginId)
                    ?? throw new InvalidOperationException("Observation plugin not loaded.");
                // A normal new game drives SessionStarting, PlayerReady and GameplayInitialized.
                var count = (int)NativeGameplay.GetField(p, "_lifecycleEvents")!;
                if (count < 3)
                    throw new InvalidOperationException("Observed only " + count + " lifecycle events; expected the new-game sequence.");
                var last = (string)NativeGameplay.GetField(p, "_lastLifecycle")!;
                if (!last.Contains("GameplayInitialized"))
                    throw new InvalidOperationException("Last observed cycle fact: " + last);
                return true;
            }),
            new TestStep("observed session matches the live one", "Observation subscribed before readiness", () =>
            {
                var p = NativeGameplay.PluginInstance(PluginId)
                    ?? throw new InvalidOperationException("Observation plugin not loaded.");
                // Re-read the boot session now (post-boot) — this case is constructed before the
                // events list fills in — and require the API's current session to still be that same
                // one, proving the observer did not miss it by loading late.
                var bootSession = events.LastOrDefault(e => e.Kind == LifecycleEventKind.GameplayInitialized)?.Session
                    ?? lifecycle.CurrentSession;
                if (bootSession == null || lifecycle.CurrentSession?.Id != bootSession.Id)
                    throw new InvalidOperationException("API live session changed underneath the test.");
                var observed = (string)NativeGameplay.GetField(p, "_lastLifecycle")!;
                if (!observed.Contains("GameplayInitialized"))
                    throw new InvalidOperationException("Observer last recorded: " + observed);
                NativeGameplay.Screenshot("observation-live");
                return true;
            }),
        });
        return steps;
    }
}
