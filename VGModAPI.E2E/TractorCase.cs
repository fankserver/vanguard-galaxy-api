using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VGModAPI.E2E;

internal static class TractorCase
{
    internal const string Id = "tractor-module";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Any)?.GetValue(obj) ?? Field(obj.GetType(), name).GetValue(obj);
    private static FieldInfo Field(Type type, string name)
    {
        for (Type? t = type; t != null; t = t.BaseType)
            if (t.GetField(name, Any | BindingFlags.DeclaredOnly) is FieldInfo f) return f;
        throw new MissingFieldException(type.FullName, name);
    }
    private static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    internal static IReadOnlyList<TestStep> Steps(ILifecycleService lifecycle, List<LifecycleEvent> events)
    {
        var steps = new List<TestStep>(LiveBoot.Steps("vgtractorauto", lifecycle, events));
        steps.Add(new TestStep("tractor targeting, tooltips and disabled behavior", "TractorModule / Skilltree / MasteryBadge", 30, Run));
        return steps;
    }
    private static StepResult Run()
    {
        Require(ModApi.Services.Equipment.Availability.IsAvailable && ModApi.Services.SkillTrees.Availability.IsAvailable && ModApi.Services.Tooltips.Availability.IsAvailable, "API services not bound");
        var a = AppDomain.CurrentDomain.GetAssemblies().Single(x => x.GetName().Name == "Assembly-CSharp");
        var moduleType = a.GetType("Behaviour.Equipment.Module.TractorModule", true)!;
        var module = Resources.FindObjectsOfTypeAll(moduleType).OfType<Component>().FirstOrDefault(m => moduleType.GetMethod("IsPlayer", Any)!.Invoke(m, new object[] { true }) is true);
        if (module == null) return StepResult.Wait("player tractor module not ready");
        var plugin = NativeGameplay.PluginInstance("vgtractorauto")!;
        var enabled = Get(plugin, "_enabled")!;
        var enabledValue = enabled.GetType().GetProperty("Value")!;
        var oldEnabled = enabledValue.GetValue(enabled);
        var tree = a.GetType("Behaviour.Crew.Skilltree", true)!.GetMethod("Get", Any)!.Invoke(null, new object[] { ModApi.Services.SkillTrees.Get(CommanderSpecialization.Engineering)!.Identifier })!;
        var player = a.GetType("Source.Player.GamePlayer", true)!.GetField("current", Any)!.GetValue(null)!;
        var commander = Get(player, "commander")!;
        var data = commander.GetType().GetMethod("GetSkillTreeData", Any)!.Invoke(commander, new object[] { tree, (object)true })!;
        var level = Field(data.GetType(), "masteryLevel"); var oldLevel = level.GetValue(data);
        var manualField = Field(moduleType, "amountOfBonusBeams"); var oldManual = manualField.GetValue(module);
        var beams = (IList)Get(module, "tractorBeams")!; int originalBeamCount = beams.Count;
        var statsField = Field(moduleType, "mainSubStats"); var oldStats = statsField.GetValue(module);
        var filtered = (IList)Get(module, "filteredTargets")!; var oldFiltered = filtered.Cast<object>().ToArray();
        var originals = beams.Cast<object>().ToDictionary(b => b, b => Get(b, "target"));
        var temporary = new List<GameObject>();
        try
        {
            // Ensure enough physical manual beams to exercise partial mastery deterministically.
            moduleType.GetMethod("CreateTractorBeams", Any)!.Invoke(module, new object[] { 3, true });
            manualField.SetValue(module, (int)oldManual! + 3);
            var itemType = a.GetType("Behaviour.Tractoring.TractorableItem", true)!;
            var markerGo = new GameObject("tractor-e2e-busy"); markerGo.SetActive(false); temporary.Add(markerGo);
            var marker = markerGo.AddComponent(itemType);
            var all = beams.Cast<object>().ToArray();
            var auto = all.Where(b => Get(b, "bonusBeam") is false).ToArray();
            var manual = all.Where(b => Get(b, "bonusBeam") is true).ToArray();
            Require(auto.Length > 0, "fixture needs automatic beam");
            var request = moduleType.GetMethod("GetAvailableTractorBeam", Any)!;
            void Busy(object b, bool busy) => Field(b.GetType(), "target").SetValue(b, busy ? marker : null);
            object? Request(bool bonus) => request.Invoke(module, new object[] { bonus });
            foreach (var b in all) Busy(b, false);
            foreach (var b in auto) Busy(b, true);
            enabledValue.SetValue(enabled, true); level.SetValue(data, 0);
            Require(Request(false) == null, "zero mastery promoted manual beam");
            Require(manual.Contains(Request(true)), "vanilla manual pool changed");
            int cap = ModApi.Services.SkillTrees.Get(CommanderSpecialization.Engineering)!.MaximumLevel;
            level.SetValue(data, cap);
            Require(manual.Contains(Request(false)), "max mastery did not borrow free manual beam");
            enabledValue.SetValue(enabled, false);
            Require(Request(false) == null, "disabled mod promoted manual beam");
            foreach (var b in all) Busy(b, true);
            Busy(auto[0], false);
            Require(Request(true) == null, "disabled mod borrowed automatic beam");
            enabledValue.SetValue(enabled, true); level.SetValue(data, 0);
            Require(ReferenceEquals(auto[0], Request(true)), "manual fallback failed at zero mastery");
            foreach (var b in all) Busy(b, true);
            Require(Request(true) == null && Request(false) == null, "occupied beam stolen");
            foreach (var b in manual) Busy(b, false);
            level.SetValue(data, cap / 2);
            int promoted = (int)Math.Floor((float)(cap / 2) / cap * manual.Length);
            for (int i = 0; i < promoted; i++) Busy(manual[i], true);
            Require(Request(false) == null, "auto exceeded conservative busy-beam cap");
            Busy(manual[promoted - 1], false);
            Require(Request(false) != null, "auto did not fill remaining allowance");

            // Actual native target filtering on controlled inactive loot components.
            level.SetValue(data, cap);
            var inventoryType = a.GetType("Behaviour.Item.InventoryItemType", true)!;
            var crewPodType = a.GetType("Behaviour.Item.Usable.CrewPodItem", true)!;
            var ordinary = ((IEnumerable)inventoryType.GetProperty("all", Any)!.GetValue(null)!).Cast<Component>().First(i => i.GetComponent(crewPodType) == null);
            var lootDataType = a.GetType("Source.Data.Persistable.TractorableItemData", true)!;
            var targetType = a.GetType("Behaviour.Weapons.TargetableUnit", true)!;
            var candidates = Array.CreateInstance(targetType, all.Length + 3);
            for (int i = 0; i < candidates.Length; i++)
            {
                var go = new GameObject("tractor-e2e-candidate"); go.SetActive(false); temporary.Add(go);
                var candidate = go.AddComponent(itemType);
                var loot = Activator.CreateInstance(lootDataType)!;
                lootDataType.GetProperty("itemType")!.SetValue(loot, ordinary);
                if (i == 0) lootDataType.GetProperty("jettisoned")!.SetValue(loot, true);
                itemType.GetProperty("data", Any)!.SetValue(candidate, loot);
                if (i == 1) itemType.GetProperty("isTractored", Any)!.SetValue(candidate, true);
                candidates.SetValue(candidate, i);
            }
            var updateTargets = moduleType.GetMethod("UpdateAvailableTargets", Any)!;
            updateTargets.Invoke(module, new object[] { candidates });
            Require(filtered.Count == all.Length, "candidate cap did not include promoted beams");
            Require(!filtered.Contains(candidates.GetValue(0)) && !filtered.Contains(candidates.GetValue(1)), "ineligible candidate added");
            for (int i = 0; i < filtered.Count; i++) Require(ReferenceEquals(filtered[i], candidates.GetValue(i + 2)), "candidate ordering changed");
            enabledValue.SetValue(enabled, false);
            updateTargets.Invoke(module, new object[] { candidates });
            Require(filtered.Count == auto.Length, "disabled candidate cap differs from vanilla");

            // Bake real module stats, preserving the native caching boundary.
            enabledValue.SetValue(enabled, true);
            statsField.SetValue(module, Activator.CreateInstance(statsField.FieldType));
            moduleType.GetMethod("SetMainSubStats", Any)!.Invoke(module, null);
            var stats = ((IEnumerable)Get(statsField.GetValue(module)!, "subStatsList")!).Cast<object>().ToArray();
            Require(stats.Any(s => ((string)Get(s, "mainSubStatName")!).Contains("VGTractorAuto") && (string)Get(s, "mainSubStatAmount")! == ""), "module tooltip missing or numeric artifact");
            enabledValue.SetValue(enabled, false);
            statsField.SetValue(module, Activator.CreateInstance(statsField.FieldType));
            moduleType.GetMethod("SetMainSubStats", Any)!.Invoke(module, null);
            Require(!((IEnumerable)Get(statsField.GetValue(module)!, "subStatsList")!).Cast<object>().Any(s => ((string)Get(s, "mainSubStatName")!).Contains("VGTractorAuto")), "disabled module tooltip contributed text");

            // Generalized module family: an API-owned registration must reach a non-tractor
            // equipment module's own stat builder with vanilla StatLines as context.
            var equipmentType = a.GetType("Behaviour.Equipment.AbstractEquipment", true)!;
            var candidatesForFamily = Resources.FindObjectsOfTypeAll(equipmentType).OfType<Component>()
                .Where(c => c.GetType() != moduleType && equipmentType.GetMethod("IsPlayer", Any)!.Invoke(c, new object[] { true }) is true
                    && c.GetType().GetMethod("SetMainSubStats", Any, null, Type.EmptyTypes, null) != null).ToArray();
            // Probe first: pick a module whose native builder really renders vanilla lines, so the
            // registration under test always has content to append to.
            Component? otherModule = null; FieldInfo? otherStats = null; int vanillaLineCount = 0;
            foreach (var candidate in candidatesForFamily)
            {
                var probeStats = Field(candidate.GetType(), "mainSubStats");
                var keep = probeStats.GetValue(candidate);
                try
                {
                    probeStats.SetValue(candidate, Activator.CreateInstance(probeStats.FieldType));
                    candidate.GetType().GetMethod("SetMainSubStats", Any)!.Invoke(candidate, null);
                    var count = ((IEnumerable)Get(probeStats.GetValue(candidate)!, "subStatsList")!).Cast<object>().Count();
                    if (count > 0) { otherModule = candidate; otherStats = probeStats; vanillaLineCount = count; break; }
                }
                finally { probeStats.SetValue(candidate, keep); }
            }
            Require(otherModule != null && otherStats != null, "no player equipment module renders vanilla stat lines for the family check");
            ShipModule? familySeen = null;
            using (var lease = ModApi.Services.Tooltips.RegisterShipModule("vgmodapi.e2e", (m, tooltip) =>
            {
                familySeen = m;
                tooltip.AddLine("e2e-family " + m.Kind);
            }))
            {
                otherStats!.SetValue(otherModule!, Activator.CreateInstance(otherStats.FieldType));
                otherModule!.GetType().GetMethod("SetMainSubStats", Any)!.Invoke(otherModule, null);
                Require(((IEnumerable)Get(otherStats.GetValue(otherModule)!, "subStatsList")!).Cast<object>()
                    .Any(s => ((string)Get(s, "mainSubStatName")!).Contains("e2e-family")),
                    "family registration missed a non-tractor module (candidate=" + otherModule!.GetType().Name + " saw=" + (familySeen?.Kind.ToString() ?? "nothing") + " vanilla=" + vanillaLineCount + ")");
            }
            Require(familySeen != null && familySeen.Kind != ShipModuleKind.Tractor && familySeen.Tractor == null,
                "non-tractor module received a tractor snapshot or no payload");
            Require(familySeen!.StatLines.Count > 0 && familySeen.DisplayName.Length > 0 && familySeen.QualityLevel >= 0,
                "module snapshot missing vanilla stat lines or identity (vanilla built " + vanillaLineCount + " lines)");
            otherStats.SetValue(otherModule, Activator.CreateInstance(otherStats.FieldType));

            // Item family: fill a real tooltip from an item source without desktop interaction.
            var tooltipType = a.GetType("Behaviour.UI.UITooltip", true)!;
            var prefab = Resources.FindObjectsOfTypeAll(tooltipType).OfType<Component>().First(t => Get(t, "_textPrefab") != null);
            var tooltipObject = UnityEngine.Object.Instantiate(prefab.gameObject); temporary.Add(tooltipObject);
            var tooltip = tooltipObject.GetComponent(tooltipType)!;
            var sourceType = a.GetType("Behaviour.UI.Tooltip.ItemTooltipSource", true)!;
            var sourceGo = new GameObject("tractor-e2e-item"); sourceGo.SetActive(false); temporary.Add(sourceGo);
            var source = sourceGo.AddComponent(sourceType);
            var expectedId = (string)Get(ordinary, "identifier")!;
            ItemInfo? itemSeen = null;
            using (var lease = ModApi.Services.Tooltips.RegisterItem("vgmodapi.e2e", (item, tip) =>
            {
                itemSeen = item;
                if (item.Identifier == expectedId) tip.AddLine("e2e-item " + item.DisplayName, TooltipTextStyle.Bonus);
            }))
            {
                var context = Enum.Parse(a.GetType("Behaviour.UI.Tooltip.ItemTooltipContext", true)!, "InInventory");
                sourceType.GetMethod("SetItem", Any)!.Invoke(source, new object?[] { ordinary, 7, false, context, false, null });
                tooltipType.GetProperty("Source", Any)!.SetValue(tooltip, source, null); // Show() normally wires this before filling.
                tooltipType.GetMethod("SetContent", Any)!.Invoke(tooltip, new object[] { source });
                string[] Lines() => ((IEnumerable)Get(tooltip, "_contentList")!).Cast<object>().Select(c => c.GetType().GetProperty("Text", Any)?.GetValue(c)).Where(t => t != null).Select(t => (string)t!.GetType().GetProperty("text", Any)!.GetValue(t)!).ToArray();
                Require(Lines().Any(t => t.Contains("e2e-item") && t.Contains("<color=")), "item tooltip contribution missing or unstyled");
                Require(itemSeen != null && itemSeen.Identifier == expectedId && itemSeen.DisplayName.Length > 0 && itemSeen.Count == 7,
                    "item snapshot identity/stack mismatch (saw " + (itemSeen == null ? "nothing" : itemSeen.Identifier + " x" + itemSeen.Count) + ")");
            }

            // Build a native mastery tooltip from a real UI prefab, without desktop interaction.
            var badgeGo = new GameObject("tractor-e2e-mastery"); badgeGo.SetActive(false); temporary.Add(badgeGo);
            var badgeType = a.GetType("Behaviour.UI.MasteryBadge", true)!;
            var badge = badgeGo.AddComponent(badgeType);
            Field(badgeType, "<skillTree>k__BackingField").SetValue(badge, tree);
            enabledValue.SetValue(enabled, true); level.SetValue(data, 0);
            badgeType.GetMethod("AddTooltipCustomContent", Any)!.Invoke(badge, new object[] { tooltip });
            string[] MasteryLines() => ((IEnumerable)Get(tooltip, "_contentList")!).Cast<object>().Select(c => c.GetType().GetProperty("Text", Any)?.GetValue(c)).Where(t => t != null).Select(t => (string)t!.GetType().GetProperty("text", Any)!.GetValue(t)!).ToArray();
            Require(MasteryLines().Any(t => t.Contains("automatic: 0%") && t.Contains("VGTractorAuto") && t.Contains("<color=")), "live zero-mastery styled tooltip missing");
            int before = MasteryLines().Length;
            enabledValue.SetValue(enabled, false);
            badgeType.GetMethod("AddTooltipCustomContent", Any)!.Invoke(badge, new object[] { tooltip });
            Require(!MasteryLines().Skip(before).Any(t => t.Contains("VGTractorAuto")), "disabled mastery tooltip contributed text");
            Debug.Log("Tractor E2E passed: zero/half/max mastery, manual borrowing, occupied beams, candidate ordering/eligibility/caps, module family + item + mastery tooltips, disabled behavior.");
            return StepResult.Pass("Tractor behavior and tooltip checks passed");
        }
        finally
        {
            enabledValue.SetValue(enabled, oldEnabled); level.SetValue(data, oldLevel);
            manualField.SetValue(module, oldManual); statsField.SetValue(module, oldStats);
            filtered.Clear(); foreach (var value in oldFiltered) filtered.Add(value);
            foreach (var entry in originals) Field(entry.Key.GetType(), "target").SetValue(entry.Key, entry.Value);
            while (beams.Count > originalBeamCount)
            {
                var extra = (Component)beams[beams.Count - 1]!; beams.RemoveAt(beams.Count - 1); UnityEngine.Object.Destroy(extra.gameObject);
            }
            foreach (var go in temporary) UnityEngine.Object.Destroy(go);
        }
    }
}
