using System;
using System.Collections.Generic;

namespace VGModAPI.E2E;

/// <summary>
/// Live E2E for the actual examples/CargoRecovery plugin. It drives the mod-authored lifecycle over
/// the real game: the HUD spawns an owned derelict station in the current system, the cargo layout
/// must auto-attach once a live boarding target belongs to it (the retry-on-adoption path), and a
/// HUD click removes the derelict cleanly, resetting the attachment. Every assertion reads the
/// plugin's live handles rather than trusting the HUD label.
///
/// In-ship walking (airlock -> cargo hold -> control room) is player movement, not a ModAPI
/// operation, so this case exercises the full authored surface that is reachable through ModAPI;
/// the boarding choice itself is covered by the plugin's own manual run.
/// </summary>
internal static class CargoRecoveryCase
{
    internal const string Id = "cargo-recovery";
    internal const string PluginId = "vgmodapi.example.cargo";

    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events)
    {
        bool Spawned(object plugin)
        {
            var derelict = NativeGameplay.GetField(plugin, "_derelict");
            return derelict != null && (bool)NativeGameplay.Prop(derelict, "Exists")!;
        }
        bool Attached(object plugin)
        {
            var session = NativeGameplay.GetField(plugin, "_session");
            return session != null && (bool)NativeGameplay.Prop(session, "Attached")!;
        }
        string? StationPoi(object plugin)
        {
            var derelict = NativeGameplay.GetField(plugin, "_derelict");
            return derelict?.GetType().GetProperty("StationPoiId")?.GetValue(derelict) as string;
        }

        var steps = new List<TestStep>(LiveBoot.Steps(PluginId, lifecycle, events));
        steps.AddRange(new[]
        {
            new TestStep("click Spawn derelict", "CargoRecovery.OnHud(spawn) / WorldProvider.CreateResourceSite", () =>
            {
                if (!NativeGameplay.ClickHudRow("Spawn derelict")) return false;
                var p = NativeGameplay.PluginInstance(PluginId);
                return p != null && Spawned(p) && StationPoi(p) != null;
            }),
            new TestStep("cargo layout auto-attaches to the authored station", "CargoAuthorSession.TryAdoptOwnStation / IDungeonService", () =>
            {
                var p = NativeGameplay.PluginInstance(PluginId)
                    ?? throw new InvalidOperationException("Cargo plugin not loaded.");
                // Adoption retries on a cheap tick until the mutation gate opens; we wait for it.
                if (!Attached(p)) return false;
                if (StationPoi(p) == null) throw new InvalidOperationException("Attached without a station POI.");
                NativeGameplay.Screenshot("cargo-attached");
                return true;
            }),
            new TestStep("Remove derelict cleans up the site and attachment", "CargoRecovery.OnHud(remove) / ResourceSite.Remove / ResetAdoption", () =>
            {
                var p = NativeGameplay.PluginInstance(PluginId)
                    ?? throw new InvalidOperationException("Cargo plugin not loaded.");
                if (!NativeGameplay.ClickHudRow("Remove derelict")) { /* not yet clickable */ }
                if (Spawned(p)) return false;
                if (Attached(p)) throw new InvalidOperationException("Remove did not reset the cargo attachment.");
                NativeGameplay.Screenshot("cargo-removed");
                return true;
            }),
        });
        return steps;
    }
}
