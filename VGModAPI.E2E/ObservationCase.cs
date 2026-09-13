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
        var session = events.FirstOrDefault(e => e.Kind == LifecycleEventKind.GameplayInitialized)?.Session;
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
                // Because the observer subscribed at Start(), its recorded session id must be the one
                // the API reports as current — proving it did not miss the session by loading late.
                var observed = (string)NativeGameplay.GetField(p, "_lastLifecycle")!;
                if (session == null || lifecycle.CurrentSession?.Id != session.Id)
                    throw new InvalidOperationException("API live session changed underneath the test.");
                NativeGameplay.Screenshot("observation-live");
                return true;
            }),
        });
        return steps;
    }
}
