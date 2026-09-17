using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace VGModAPI.E2E;

/// <summary>Exercises the real example window and config-backed settings. Save/load is covered by
/// host tests using the actual persistence store; this live case keeps the player ephemeral.</summary>
internal static class UiSurfacesCase
{
    internal const string Id = "ui-surfaces";
    internal const string PluginId = "vgmodapi.example.ui-surfaces";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events)
    {
        var steps = new List<TestStep>(LiveBoot.Steps(PluginId, lifecycle, events));
        steps.AddRange(new[]
        {
            new TestStep("consumer-owned window container is created", "UiSurfaces.Attach / GameplayUiService.CreateContainer", () =>
                NativeGameplay.PluginInstance(PluginId) is object plugin && NativeGameplay.GetField(plugin, "_container") != null),
            TestStep.ActionThenWait("click launcher creates the owned window", "HudButton / UiSurfaces.ToggleWindow", 20,
                () => NativeGameplay.ClickHudRow("Example window") ? StepResult.Pass("Launcher invoked") : StepResult.Wait("Launcher not ready"),
                () => Window()?.activeSelf == true ? StepResult.Pass("Window visible") : StepResult.Wait("Window not visible")),
            new TestStep("typed settings change the live window and persist global config", "ModSettingsService / ConfigEntry / UiSurfaces", 20, VerifySettings),
            TestStep.ActionThenWait("click launcher toggles the window closed", "UiSurfaces.ToggleWindow / GameObject.SetActive", 20,
                () => NativeGameplay.ClickHudRow("Example window") ? StepResult.Pass("Launcher invoked") : StepResult.Wait("Launcher not ready"),
                () => Window()?.activeSelf == false ? StepResult.Pass("Window hidden") : StepResult.Wait("Window still visible")),
        });
        return steps;
    }
    private static GameObject? Window() => NativeGameplay.GetField(NativeGameplay.PluginInstance(PluginId)!, "_window") as GameObject;
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static StepResult VerifySettings()
    {
        var plugin = (BaseUnityPlugin)NativeGameplay.PluginInstance(PluginId)!;
        if (NativeGameplay.GetField(plugin, "_settings") == null) return StepResult.Wait("Settings provider not acquired");
        var settings = ModApi.Services.Settings;
        var rows = ((IEnumerable)settings.GetType().GetMethod("Snapshot", Any)!.Invoke(settings, new object[] { PluginId })!).Cast<object>()
            .ToDictionary(row => ((ModSettingDefinition)row.GetType().GetProperty("Definition", Any)!.GetValue(row)!).LocalId);
        Require(rows.Count == 4, "Expected bool, integer, float and choice settings");
        var write = settings.GetType().GetMethod("TryWrite", Any)!;
        void Set(string id, object value) => Require(write.Invoke(settings, new[] { rows[id], value }) is true, "Setting write refused: " + id);
        var counter = NativeGameplay.GetField(plugin, "_visits") ?? throw new InvalidOperationException("Save-data model unavailable");
        int Count() => (int)(counter.GetType().GetProperty("Count", Any)!.GetValue(counter) ?? throw new InvalidOperationException("Counter unreadable"));
        int before = Count();
        Require(before == 1, "First window open must record exactly once");
        Set("count-opens", false); Set("goal", 23); Set("opacity", .5f); Set("theme", "amber");
        var imageType = Type.GetType("UnityEngine.UI.Image, UnityEngine.UI", true)!;
        var image = Window()!.GetComponent(imageType);
        var color = (Color)imageType.GetProperty("color")!.GetValue(image)!;
        Require(Math.Abs(color.a - .5f) < .001f && Math.Abs(color.r - .20f) < .001f, "Opacity/theme not applied immediately");
        var label = NativeGameplay.GetField(plugin, "_windowLabel")!;
        Require(((string)label.GetType().GetProperty("text")!.GetValue(label)!).Contains("1 / 23"), "Goal not reflected immediately");
        var config = File.ReadAllText(plugin.Config.ConfigFilePath);
        Require(config.Contains("CountOpens = false") && config.Contains("VisitGoal = 23") && config.Contains("Theme = amber") && config.Contains("Opacity = 0.5"), "Settings were not persisted by BepInEx config");
        Require(NativeGameplay.ClickHudRow("Example window") && NativeGameplay.ClickHudRow("Example window"), "Unable to reopen window");
        Require(Count() == before, "Paused counting changed per-save data");
        Set("count-opens", true);
        Require(NativeGameplay.ClickHudRow("Example window") && NativeGameplay.ClickHudRow("Example window"), "Unable to reopen window after enabling");
        Require(Count() == before + 1, "Enabled counting did not record open");
        foreach (var row in rows.Values)
            Require(settings.GetType().GetMethod("TryReset", Any)!.Invoke(settings, new[] { row }) is true, "Setting reset refused");
        Require(Count() == before + 1, "Resetting global preferences erased per-save progress");
        Debug.Log("UI Surfaces E2E: four typed settings, immediate window updates, config persistence, pause/resume count, and reset without losing progress passed.");
        return StepResult.Pass("Settings/config and window progress verified; no native disk saves attempted");
    }
}
