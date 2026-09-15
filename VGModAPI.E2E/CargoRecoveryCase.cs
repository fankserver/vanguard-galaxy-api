using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.E2E;

/// <summary>
/// Live E2E for the actual examples/CargoRecovery plugin. It drives the mod-authored lifecycle over
/// the real game: the HUD spawns an owned derelict station in the current system, the case routes
/// in-system to that station (RequestRoute) so a live boarding target exists and the cargo layout
/// auto-attaches through the retry-on-adoption path, then steps away and a HUD click removes the
/// derelict cleanly, resetting the attachment. Every assertion reads the plugin's live handles rather
/// than trusting the HUD label.
///
/// In-ship walking (airlock -> cargo hold -> control room) is player movement, not a ModAPI
/// operation, so this case exercises the full authored surface that is reachable through ModAPI;
/// the boarding choice itself is covered by the plugin's own manual run.
/// </summary>
internal static class CargoRecoveryCase
{
    internal const string Id = "cargo-recovery";
    internal const string PluginId = "vgmodapi.example.cargo";

    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events,
        List<TravelTransition> travelEvents)
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
        var originPoi = "";
        var originSystem = "";
        steps.AddRange(new[]
        {
            TestStep.ActionThenWait("click Spawn derelict", "CargoRecovery.OnHud(spawn) / WorldProvider.CreateResourceSite", 45,
                () =>
                {
                    // Capture a real origin before mutating so teardown can never wait on an empty target.
                    var location = ModApi.Services.Travel.CurrentLocation;
                    if (location?.PoiId == null)
                        return StepResult.Wait($"current location unavailable: {location?.SystemId ?? "null"}/null");
                    originPoi = location.PoiId;
                    originSystem = location.SystemId;
                    return NativeGameplay.ClickHudRow("Spawn derelict")
                        ? StepResult.Pass($"spawn clicked; origin={originSystem}/{originPoi}")
                        : StepResult.Wait("Spawn derelict HUD row is not clickable yet");
                },
                () =>
                {
                    var p = NativeGameplay.PluginInstance(PluginId);
                    var spawned = p != null && Spawned(p);
                    var stationPoi = p == null ? null : StationPoi(p);
                    return spawned && stationPoi != null
                        ? StepResult.Pass("spawned station=" + stationPoi)
                        : StepResult.Wait($"pluginLoaded={p != null}; spawned={spawned}; stationPoi={stationPoi ?? "null"}");
                }),
            // Adoption only fires once a live boarding target belongs to the authored station, which
            // needs the player present, so route in-system to the station to create that target.
            TravelStep("travel to the derelict station",
                () => NativeGameplay.PluginInstance(PluginId) is { } p ? StationPoi(p) : null,
                () => ModApi.Services.Travel.CurrentLocation?.SystemId ?? "", TravelMode.InSystem, travelEvents),
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
            TravelStep("return away from the derelict", () => originPoi, () => originSystem,
                TravelMode.InSystem, travelEvents),
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

    private static TestStep TravelStep(string name, Func<string?> poi, Func<string> system,
        TravelMode mode, List<TravelTransition> events)
    {
        var firstSequence = 0L;
        return TestStep.ActionThenWait(name, "ITravelService.RequestRoute / Transitioned", 180,
            () =>
            {
                NativeSession.RequireEphemeral();
                var targetPoi = poi();
                if (string.IsNullOrEmpty(targetPoi)) return StepResult.Wait("target POI has not been authored yet");
                firstSequence = events.Count == 0 ? 0 : events.Max(e => e.Sequence);
                var result = ModApi.Services.Travel.RequestRoute(targetPoi, 7f);
                return result.Accepted ? StepResult.Pass("route accepted")
                    : StepResult.Fail("Travel request refused: " + result.Status + " - " + result.Detail);
            },
            () =>
            {
                var targetPoi = poi()!;
                var targetSystem = system();
                var current = ModApi.Services.Travel.CurrentLocation;
                var leg = events.Where(e => e.Sequence > firstSequence).ToArray();
                var arrived = leg.Any(e => e.Kind == TravelTransitionKind.Arrived && e.Mode == mode
                    && e.ActualLocation?.SystemId == targetSystem && e.ActualLocation.PoiId == targetPoi);
                var completed = leg.Any(e => e.Kind == TravelTransitionKind.RouteCompleted && e.Mode == mode);
                var observation = $"current={current?.SystemId ?? "null"}/{current?.PoiId ?? "null"}; "
                    + $"target={targetSystem}/{targetPoi}; arrived={arrived}; completed={completed}; transitions={leg.Length}";
                if (current?.SystemId != targetSystem || current.PoiId != targetPoi || !arrived || !completed)
                    return StepResult.Wait(observation);
                NativeGameplay.Screenshot(name);
                return StepResult.Pass(observation);
            });
    }
}
