using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VGModAPI.E2E;

// Exercises examples/TractorGuide end to end: the settings-driven equipment policy, and the
// example's own registrations on the ship-module, skill-tree and item tooltip families.
internal static class TractorGuideCase
{
    internal const string Id = "tractor-guide";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Any)?.GetValue(obj) ?? obj.GetType().GetField(name, Any)?.GetValue(obj);
    private static FieldInfo Field(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
            if (t.GetField(name, Any | BindingFlags.DeclaredOnly) is FieldInfo f) return f;
        throw new MissingFieldException(type.FullName, name);
    }
    private static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }

    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events)
    {
        var steps = new List<TestStep>(LiveBoot.Steps("vgmodapi.example.tractor-guide", lifecycle, events));
        steps.Add(new TestStep("guide policy, settings and tooltip contributions", "TractorGuide example", 30, Run));
        return steps;
    }

    private static StepResult Run()
    {
        Require(ModApi.Services.Equipment.Availability.IsAvailable && ModApi.Services.SkillTrees.Availability.IsAvailable && ModApi.Services.Tooltips.Availability.IsAvailable, "API services not bound");
        var a = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == "Assembly-CSharp");
        var moduleType = a.GetType("Behaviour.Equipment.Module.TractorModule", true)!;
        var module = Resources.FindObjectsOfTypeAll(moduleType).OfType<Component>().FirstOrDefault(m => moduleType.GetMethod("IsPlayer", Any)!.Invoke(m, new object[] { true }) is true);
        if (module == null) return StepResult.Wait("player tractor module not ready");
        var guide = NativeGameplay.PluginInstance("vgmodapi.example.tractor-guide")!;
        var extraEntry = Get(guide, "_extraAutoBeam")!; var extraValue = extraEntry.GetType().GetProperty("Value")!; var oldExtra = extraValue.GetValue(extraEntry);
        var highlightEntry = Get(guide, "_highlightItem")!; var highlightValue = highlightEntry.GetType().GetProperty("Value")!; var oldHighlight = highlightValue.GetValue(highlightEntry);
        var otherConsumer = NativeGameplay.PluginInstance("vgtractorauto");
        object? otherEnabled = otherConsumer == null ? null : Get(otherConsumer, "_enabled");
        PropertyInfo? otherEnabledValue = otherEnabled?.GetType().GetProperty("Value");
        object? oldOtherEnabled = otherEnabledValue == null ? null : otherEnabledValue.GetValue(otherEnabled);
        Require(otherConsumer == null || otherEnabled != null, "staged VGTractorAuto lacks the _enabled setting the isolation relies on");

        var manualField = Field(moduleType, "amountOfBonusBeams"); var oldManual = manualField.GetValue(module);
        var beams = (IList)Get(module, "tractorBeams")!; int originalBeamCount = beams.Count;
        var originals = beams.Cast<object>().ToDictionary(b => b, b => Get(b, "target"));
        var statsField = Field(moduleType, "mainSubStats"); var oldStats = statsField.GetValue(module);
        var temporary = new List<GameObject>();
        try
        {
            otherEnabledValue?.SetValue(otherEnabled, false); // isolate the example's policy from the sibling consumer
            moduleType.GetMethod("CreateTractorBeams", Any)!.Invoke(module, new object[] { 3, true });
            var all = beams.Cast<object>().ToArray();
            var auto = all.Where(b => Get(b, "bonusBeam") is false).ToArray();
            var manual = all.Where(b => Get(b, "bonusBeam") is true).ToArray();
            Require(auto.Length > 0 && manual.Length > 0, "fixture needs automatic and manual beams");
            var markerGo = new GameObject("guide-e2e-busy"); markerGo.SetActive(false); temporary.Add(markerGo);
            var marker = markerGo.AddComponent(a.GetType("Behaviour.Tractoring.TractorableItem", true)!);
            void Busy(object b, bool busy) => Field(b.GetType(), "target").SetValue(b, busy ? marker : null);
            object? Request(bool bonus) => moduleType.GetMethod("GetAvailableTractorBeam", Any)!.Invoke(module, new object[] { bonus });
            foreach (var b in all) Busy(b, false);
            foreach (var b in auto) Busy(b, true);

            // Settings-driven equipment policy: off abstains (vanilla cap), on borrows one free manual beam.
            extraValue.SetValue(extraEntry, false);
            Require(Request(false) == null, "example promoted beams with ExtraAutoBeam off");
            extraValue.SetValue(extraEntry, true);
            Require(manual.Contains(Request(false)!), "ExtraAutoBeam did not borrow a free manual beam");
            extraValue.SetValue(extraEntry, false);
            Require(Request(false) == null, "ExtraAutoBeam stayed active after being turned off");
            extraValue.SetValue(extraEntry, true);

            // Ship-module family through the example's own registration.
            var tree = ModApi.Services.SkillTrees.Get(CommanderSpecialization.Engineering);
            Require(tree != null && tree.Specialization == CommanderSpecialization.Engineering, "engineering tree not readable through SkillTrees");
            statsField.SetValue(module, Activator.CreateInstance(statsField.FieldType));
            moduleType.GetMethod("SetMainSubStats", Any)!.Invoke(module, null);
            var guideLines = ((IEnumerable)Get(statsField.GetValue(module)!, "subStatsList")!).Cast<object>()
                .Select(s => (string)Get(s, "mainSubStatName")!).Where(t => t.Contains("(TractorGuide)")).ToArray();
            Require(guideLines.Length == 1 && guideLines[0] == $"Autopilot mastery {tree!.MasteryLevel}/{tree.MaximumLevel} (TractorGuide)",
                "example module tooltip missing, duplicated or stale (" + string.Join("|", guideLines) + ")");

            // Skill-tree family through the example's own registration.
            var nativeTree = a.GetType("Behaviour.Crew.Skilltree", true)!.GetMethod("Get", Any)!.Invoke(null, new object[] { tree!.Identifier })!;
            var tooltipType = a.GetType("Behaviour.UI.UITooltip", true)!;
            var prefab = Resources.FindObjectsOfTypeAll(tooltipType).OfType<Component>().First(t => Get(t, "_textPrefab") != null);
            Component MakeTooltip(out GameObject go)
            {
                // A fresh tooltip object per fill: the fill dedupe claims each tooltip per frame,
                // and the native UI always rebuilds content on a new show.
                go = UnityEngine.Object.Instantiate(prefab.gameObject); go.SetActive(false); temporary.Add(go);
                return go.GetComponent(tooltipType)!;
            }
            string[] LinesOf(Component tooltip) => ((IEnumerable)Get(tooltip, "_contentList")!).Cast<object>().Select(c => c.GetType().GetProperty("Text", Any)?.GetValue(c)).Where(t => t != null).Select(t => (string)t!.GetType().GetProperty("text", Any)!.GetValue(t)!).ToArray();
            var badgeGo = new GameObject("guide-e2e-badge"); badgeGo.SetActive(false); temporary.Add(badgeGo);
            var badgeType = a.GetType("Behaviour.UI.MasteryBadge", true)!;
            var badge = badgeGo.AddComponent(badgeType);
            Field(badgeType, "<skillTree>k__BackingField").SetValue(badge, nativeTree);
            var badgeTooltip = MakeTooltip(out _);
            badgeType.GetMethod("AddTooltipCustomContent", Any)!.Invoke(badge, new object[] { badgeTooltip });
            Require(LinesOf(badgeTooltip).Count(t => t.Contains("Engineering also powers the tractor autopilot (TractorGuide)")) == 1, "example skill-tree tooltip missing or duplicated");

            // Item family: the opt-in identifier setting both gates and selects the contribution.
            var ordinary = ((IEnumerable)a.GetType("Behaviour.Item.InventoryItemType", true)!.GetProperty("all", Any)!.GetValue(null)!).Cast<Component>().First();
            var expectedId = (string)Get(ordinary, "identifier")!;
            var sourceGo = new GameObject("guide-e2e-item"); sourceGo.SetActive(false); temporary.Add(sourceGo);
            var sourceType = a.GetType("Behaviour.UI.Tooltip.ItemTooltipSource", true)!;
            var source = sourceGo.AddComponent(sourceType);
            var context = Enum.Parse(a.GetType("Behaviour.UI.Tooltip.ItemTooltipContext", true)!, "InInventory");
            string[] ShowItem()
            {
                var tooltip = MakeTooltip(out _);
                sourceType.GetMethod("SetItem", Any)!.Invoke(source, new object?[] { ordinary, 2, false, context, false, null });
                tooltipType.GetProperty("Source", Any)!.SetValue(tooltip, source, null);
                tooltipType.GetMethod("SetContent", Any)!.Invoke(tooltip, new object[] { source });
                return LinesOf(tooltip);
            }
            highlightValue.SetValue(highlightEntry, "");
            Require(!ShowItem().Any(t => t.Contains("Easy to ferry")), "item tip shown while HighlightItem is empty");
            highlightValue.SetValue(highlightEntry, expectedId);
            Require(ShowItem().Count(t => t.Contains("Easy to ferry with a tractor beam (TractorGuide)") && t.Contains("<color=")) == 1, "example item tooltip missing, duplicated or unstyled");
            Debug.Log("TractorGuide E2E passed: settings-gated beam borrowing, module/skill-tree/item contributions through the example's own registrations.");
            return StepResult.Pass("TractorGuide example behavior verified");
        }
        finally
        {
            extraValue.SetValue(extraEntry, oldExtra); highlightValue.SetValue(highlightEntry, oldHighlight);
            if (otherEnabledValue != null) otherEnabledValue.SetValue(otherEnabled, oldOtherEnabled);
            manualField.SetValue(module, oldManual); statsField.SetValue(module, oldStats);
            foreach (var entry in originals) Field(entry.Key.GetType(), "target").SetValue(entry.Key, entry.Value);
            foreach (var b in beams.Cast<object>().Except(originals.Keys).ToArray()) Field(b.GetType(), "target").SetValue(b, null);
            while (beams.Count > originalBeamCount)
            {
                var extra = (Component)beams[beams.Count - 1]!; beams.RemoveAt(beams.Count - 1); UnityEngine.Object.Destroy(extra.gameObject);
            }
            foreach (var go in temporary) UnityEngine.Object.Destroy(go);
        }
    }
}
