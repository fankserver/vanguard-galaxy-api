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
            new TestStep("annotated module and mastery tooltips follow the typed setting", "Tooltips.RegisterShipModule / RegisterSkillTree / ConfigEntry", 30, VerifyTooltipAnnotations),
            TestStep.ActionThenWait("click launcher toggles the window closed", "UiSurfaces.ToggleWindow / GameObject.SetActive", 20,
                () => NativeGameplay.ClickHudRow("Example window") ? StepResult.Pass("Launcher invoked") : StepResult.Wait("Launcher not ready"),
                () => Window()?.activeSelf == false ? StepResult.Pass("Window hidden") : StepResult.Wait("Window still visible")),
        });
        return steps;
    }
    private static GameObject? Window() => NativeGameplay.GetField(NativeGameplay.PluginInstance(PluginId)!, "_window") as GameObject;
    private const BindingFlags All = Any | BindingFlags.Static;
    private static object? Prop(object o, string name) => o.GetType().GetProperty(name, Any)?.GetValue(o) ?? o.GetType().GetField(name, Any)?.GetValue(o);
    private static FieldInfo DeclaredField(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
            if (t.GetField(name, All | BindingFlags.DeclaredOnly) is FieldInfo f) return f;
        throw new MissingFieldException(type.FullName, name);
    }
    private static StepResult VerifyTooltipAnnotations()
    {
        var plugin = NativeGameplay.PluginInstance(PluginId)!;
        var annotate = NativeGameplay.GetField(plugin, "_annotate") ?? throw new InvalidOperationException("tooltip annotation setting unavailable");
        var valueProp = annotate.GetType().GetProperty("Value")!; var old = valueProp.GetValue(annotate);
        var a = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == "Assembly-CSharp");
        var moduleType = a.GetType("Behaviour.Equipment.Module.TractorModule", true)!;
        var module = Resources.FindObjectsOfTypeAll(moduleType).OfType<Component>().FirstOrDefault(m => moduleType.GetMethod("IsPlayer", All)!.Invoke(m, new object[] { true }) is true);
        if (module == null) return StepResult.Wait("player tractor module not ready");
        valueProp.SetValue(annotate, true);
        try
        {
            var statsField = DeclaredField(moduleType, "mainSubStats"); var oldStats = statsField.GetValue(module);
            try
            {
                string[] Build()
                {
                    statsField.SetValue(module, Activator.CreateInstance(statsField.FieldType));
                    moduleType.GetMethod("SetMainSubStats", All)!.Invoke(module, null);
                    return ((IEnumerable)Prop(statsField.GetValue(module)!, "subStatsList")!).Cast<object>().Select(x => (string)Prop(x, "mainSubStatName")!).ToArray();
                }
                Require(Build().Count(t => t.StartsWith("UiSurfaces: Tractor module")) == 1, "module annotation missing or duplicated");
                valueProp.SetValue(annotate, false);
                Require(!Build().Any(t => t.StartsWith("UiSurfaces:")), "module annotation shown while the setting is off");
            }
            finally { statsField.SetValue(module, oldStats); }
            var tree = ModApi.Services.SkillTrees.Get(CommanderSpecialization.Engineering) ?? throw new InvalidOperationException("engineering tree unavailable");
            var nativeTree = a.GetType("Behaviour.Crew.Skilltree", true)!.GetMethod("Get", All)!.Invoke(null, new object[] { tree.Identifier })!;
            var tooltipType = a.GetType("Behaviour.UI.UITooltip", true)!;
            var prefab = Resources.FindObjectsOfTypeAll(tooltipType).OfType<Component>().First(t => Prop(t, "_textPrefab") != null);
            var tipGo = UnityEngine.Object.Instantiate(prefab.gameObject); tipGo.SetActive(false);
            var badgeGo = new GameObject("ui-e2e-badge"); badgeGo.SetActive(false);
            try
            {
                var badgeType = a.GetType("Behaviour.UI.MasteryBadge", true)!;
                var badge = badgeGo.AddComponent(badgeType);
                DeclaredField(badgeType, "<skillTree>k__BackingField").SetValue(badge, nativeTree);
                valueProp.SetValue(annotate, true);
                var tooltip = tipGo.GetComponent(tooltipType)!;
                badgeType.GetMethod("AddTooltipCustomContent", All)!.Invoke(badge, new object[] { tooltip });
                var lines = ((IEnumerable)Prop(tooltip, "_contentList")!).Cast<object>().Select(c => c.GetType().GetProperty("Text", Any)?.GetValue(c)).Where(t => t != null).Select(t => (string)t!.GetType().GetProperty("text", Any)!.GetValue(t)!).ToArray();
                Require(lines.Count(t => t.Contains($"UiSurfaces: Engineering mastery {tree.MasteryLevel}/{tree.MaximumLevel}") && t.Contains("<color=")) == 1, "engineering badge annotation missing, duplicated or unstyled");
            }
            finally { UnityEngine.Object.Destroy(tipGo); UnityEngine.Object.Destroy(badgeGo); }
        }
        finally { valueProp.SetValue(annotate, old); }
        Debug.Log("UI Surfaces E2E: opt-in module and mastery annotations rendered exactly once through the example's own tooltip registrations.");
        return StepResult.Pass("Tooltip annotations verified");
    }
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static StepResult VerifySettings()
    {
        var plugin = (BaseUnityPlugin)NativeGameplay.PluginInstance(PluginId)!;
        if (NativeGameplay.GetField(plugin, "_settings") == null) return StepResult.Wait("Settings provider not acquired");
        var settings = ModApi.Services.Settings;
        var rows = ((IEnumerable)settings.GetType().GetMethod("Snapshot", Any)!.Invoke(settings, new object[] { PluginId })!).Cast<object>()
            .ToDictionary(row => ((ModSettingDefinition)row.GetType().GetProperty("Definition", Any)!.GetValue(row)!).LocalId);
        Require(rows.Count == 6, "Expected bool, integer, float, choice, behavior and tooltip settings");
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
        Debug.Log("UI Surfaces E2E: six typed settings, immediate window updates, config persistence, pause/resume count, and reset without losing progress passed.");
        return StepResult.Pass("Settings/config and window progress verified; no native disk saves attempted");
    }
}
