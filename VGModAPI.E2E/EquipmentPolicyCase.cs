using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VGModAPI.E2E;

/// <summary>
/// Exercises the equipment-targeting surface with API-owned registrations only: capacity
/// semantics, first-non-null-wins between two rules, the occupied-beam guard and teardown
/// back to vanilla. All registrations are made and disposed by this case; policy formulas here
/// exist to make the resolution observable, mirroring how any consumer mod uses the contract.
/// </summary>
internal static class EquipmentPolicyCase
{
    internal const string Id = "equipment-policy";
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
        var steps = new List<TestStep>(LiveBoot.Steps(ModApi.PluginId, lifecycle, events));
        steps.Add(new TestStep("capacity, precedence and occupied-beam guards through registered rules", "EquipmentService.Resolve / TractorBeamRuntime", 30, Run));
        return steps;
    }

    private static StepResult Run()
    {
        Require(ModApi.Services.Equipment.Availability.IsAvailable, "equipment service not bound");
        var a = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == "Assembly-CSharp");
        var moduleType = a.GetType("Behaviour.Equipment.Module.TractorModule", true)!;
        var module = Resources.FindObjectsOfTypeAll(moduleType).OfType<Component>().FirstOrDefault(m => moduleType.GetMethod("IsPlayer", Any)!.Invoke(m, new object[] { true }) is true);
        if (module == null) return StepResult.Wait("player tractor module not ready");
        var manualField = Field(moduleType, "amountOfBonusBeams"); var oldManual = manualField.GetValue(module);
        var beams = (IList)Get(module, "tractorBeams")!; int originalBeamCount = beams.Count;
        var originals = beams.Cast<object>().ToDictionary(b => b, b => Get(b, "target"));
        var temporary = new List<GameObject>();
        List<IDisposable> rules = new();
        try
        {
            moduleType.GetMethod("CreateTractorBeams", Any)!.Invoke(module, new object[] { 3, true });
            manualField.SetValue(module, (int)oldManual! + 3); // the field, not the list, bounds native capacity
            var all = beams.Cast<object>().ToArray();
            var auto = all.Where(b => Get(b, "bonusBeam") is false).ToArray();
            var manual = all.Where(b => Get(b, "bonusBeam") is true).ToArray();
            Require(auto.Length > 0 && manual.Length >= 2, "fixture needs automatic beams and two manual beams");
            var markerGo = new GameObject("policy-e2e-busy"); markerGo.SetActive(false); temporary.Add(markerGo);
            var marker = markerGo.AddComponent(a.GetType("Behaviour.Tractoring.TractorableItem", true)!);
            void Busy(object b, bool busy) => Field(b.GetType(), "target").SetValue(b, busy ? marker : null);
            object? Request(bool bonus) => moduleType.GetMethod("GetAvailableTractorBeam", Any)!.Invoke(module, new object[] { bonus });
            void AddRule(string local, int extra) => rules.Add(ModApi.Services.Equipment.ConfigurePlayerTractorModules("vgmodapi.e2e." + local,
                _ => new TractorTargeting(auto.Length + extra)));
            foreach (var b in all) Busy(b, false);
            foreach (var b in auto) Busy(b, true);

            // No rules: every registered owner absent means vanilla capacity, no borrowing.
            Require(Request(false) == null, "unconfigured module borrowed a manual beam");

            // One rule at +2: borrows the first and second free manual beam, never a third.
            AddRule("wide", 2);
            Require(Request(false) != null, "rule at +2 did not borrow the first manual beam");
            Busy(manual[0], true);
            Require(Request(false) != null, "rule at +2 did not borrow the second manual beam");
            Busy(manual[1], true);
            Require(Request(false) == null, "rule at +2 exceeded its capacity");

            Busy(manual[1], false); // back to exactly one manual beam busy for the precedence check
            // A later rule stays invisible while an earlier rule answers (first non-null wins).
            AddRule("narrow", 1);
            Require(Request(false) != null, "a later rule preempted the earlier one");

            // Removing the earlier rule hands the decision to the remaining one.
            rules[0].Dispose(); rules.RemoveAt(0);
            Require(Request(false) == null, "the narrow rule did not take effect after the wide rule was disposed");

            // Occupied-beam protection: nothing is handed out while every beam is busy.
            foreach (var b in manual) Busy(b, true);
            Require(Request(false) == null && Request(true) == null, "occupied beams were handed out");

            // Teardown to vanilla: no rule, free manual beams, automatic still busy => no borrowing.
            rules[0].Dispose(); rules.Clear();
            foreach (var b in manual) Busy(b, false);
            Require(Request(false) == null, "vanilla cap not restored after disposal");
            Debug.Log("EquipmentPolicy E2E passed: abstain-by-absence, capacity, first-non-null precedence, occupied beams, disposal to vanilla.");
            return StepResult.Pass("Equipment targeting policy verified through registered rules");
        }
        finally
        {
            foreach (var rule in rules) rule.Dispose();
            manualField.SetValue(module, oldManual);
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
