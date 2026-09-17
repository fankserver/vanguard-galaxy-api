using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VGModAPI.E2E;

// Exercises examples/TractorGuide: the one gameplay decision the example owns. Its settings
// toggle alone decides whether the autopilot may borrow one free manual beam.
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
        steps.Add(new TestStep("settings-gated beam borrowing", "TractorGuide example", 30, Run));
        return steps;
    }

    private static StepResult Run()
    {
        Require(ModApi.Services.Equipment.Availability.IsAvailable, "equipment service not bound");
        var a = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == "Assembly-CSharp");
        var moduleType = a.GetType("Behaviour.Equipment.Module.TractorModule", true)!;
        var module = Resources.FindObjectsOfTypeAll(moduleType).OfType<Component>().FirstOrDefault(m => moduleType.GetMethod("IsPlayer", Any)!.Invoke(m, new object[] { true }) is true);
        if (module == null) return StepResult.Wait("player tractor module not ready");
        var guide = NativeGameplay.PluginInstance("vgmodapi.example.tractor-guide")!;
        var extraEntry = Get(guide, "_extraAutoBeam")!; var extraValue = extraEntry.GetType().GetProperty("Value")!; var oldExtra = extraValue.GetValue(extraEntry);
        var otherConsumer = NativeGameplay.PluginInstance("vgtractorauto");
        object? otherEnabled = otherConsumer == null ? null : Get(otherConsumer, "_enabled");
        PropertyInfo? otherEnabledValue = otherEnabled?.GetType().GetProperty("Value");
        object? oldOtherEnabled = otherEnabledValue == null ? null : otherEnabledValue.GetValue(otherEnabled);
        Require(otherConsumer == null || otherEnabled != null, "staged VGTractorAuto lacks the _enabled setting the isolation relies on");

        var beams = (IList)Get(module, "tractorBeams")!; int originalBeamCount = beams.Count;
        var originals = beams.Cast<object>().ToDictionary(b => b, b => Get(b, "target"));
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
            Debug.Log("TractorGuide E2E passed: the ExtraAutoBeam setting alone gates borrowing one free manual beam.");
            return StepResult.Pass("TractorGuide example behavior verified");
        }
        finally
        {
            extraValue.SetValue(extraEntry, oldExtra);
            if (otherEnabledValue != null) otherEnabledValue.SetValue(otherEnabled, oldOtherEnabled);
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
