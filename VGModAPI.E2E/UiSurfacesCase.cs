using System;
using System.Collections.Generic;
using UnityEngine;

namespace VGModAPI.E2E;

/// <summary>
/// Live E2E for the actual examples/UiSurfaces plugin. Once gameplay UI becomes available the plugin
/// creates its consumer-owned window container and a shared HUD launcher button; this case waits for
/// that container, then clicks the button to create and toggle the owned window and asserts the
/// window's live visibility. The Forge inspector is availability-dependent (it needs forge services),
/// so it is not gate on this fresh-spaceflight case.
/// </summary>
internal static class UiSurfacesCase
{
    internal const string Id = "ui-surfaces";
    internal const string PluginId = "vgmodapi.example.ui-surfaces";

    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events)
    {
        var steps = new List<TestStep>(LiveBoot.Steps(PluginId, lifecycle, events));
        steps.AddRange(new[]
        {
            new TestStep("consumer-owned window container is created", "UiSurfaces.Attach / GameplayUiService.CreateContainer", () =>
            {
                var p = NativeGameplay.PluginInstance(PluginId)
                    ?? throw new InvalidOperationException("UiSurfaces plugin not loaded.");
                return NativeGameplay.GetField(p, "_container") != null;
            }),
            new TestStep("click launcher creates the owned window", "HudButton / UiSurfaces.ToggleWindow", () =>
            {
                var p = NativeGameplay.PluginInstance(PluginId)!;
                if (!NativeGameplay.ClickHudRow("Example window"))
                { if (NativeGameplay.GetField(p, "_window") != null) return StartVisible(p); return false; }
                if (NativeGameplay.GetField(p, "_window") == null) return false;
                return StartVisible(p);
            }),
            new TestStep("click launcher toggles the window closed", "UiSurfaces.ToggleWindow / GameObject.SetActive", () =>
            {
                var p = NativeGameplay.PluginInstance(PluginId)!;
                if (!NativeGameplay.ClickHudRow("Example window")) return false;
                var window = NativeGameplay.GetField(p, "_window");
                return window is GameObject go && !go.activeSelf;
            }),
        });
        return steps;
    }

    private static bool StartVisible(object plugin)
    {
        var window = NativeGameplay.GetField(plugin, "_window");
        return window is GameObject go && go.activeSelf;
    }
}
