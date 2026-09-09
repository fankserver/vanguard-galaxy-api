using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using TMPro;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // A generated, scene-local installation exercises native panel lifetime without saving or starting combat.
    private IEnumerable<object?> CheckDungeonPanelLifetime()
    {
        WriteAtomic("dungeon-panel.txt", new[] { "INCOMPLETE" });
        var boarding = ModApi.Boarding ?? throw new InvalidOperationException("Boarding observation unavailable.");
        var panel = ModApi.DungeonPanel ?? throw new InvalidOperationException("Dungeon panel unavailable.");
        var previous = boarding.GetTargets().Select(target => target.Handle).ToHashSet();
        var dataType = NativeType("Source.Data.Persistable.DungeonLocationData");
        var definitionType = NativeType("Behaviour.Dungeon.DungeonDefinition");
        var kind = dataType.GetField("dungeonType")!;
        var get = definitionType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { kind.FieldType }, null)!;
        object? selected = null;
        foreach (var value in Enum.GetValues(kind.FieldType))
            if (get.Invoke(null, new[] { value }) is UnityEngine.Object definition && definition) { selected = value; break; }
        Require(selected != null, "No native dungeon definition is loaded for the generated fixture.");
        var data = Activator.CreateInstance(dataType)!;
        kind.SetValue(data, selected);
        dataType.GetField("level")!.SetValue(data, 1);
        dataType.GetField("faction")!.SetValue(data, SpGet(NativeType("Source.Galaxy.Faction"), "player"));
        var root = new GameObject("Qualification-only dungeon location");
        IDisposable? section = null; IDisposable? action = null; IDisposable? enabledAction = null;
        var oldMouse = Mouse.current; Mouse? mouse = null; var calls = 0;
        try
        {
            var unitType = NativeType("Behaviour.Unit.DungeonLocationUnit");
            var unit = root.AddComponent(unitType);
            unitType.GetMethod("Init", new[] { dataType })!.Invoke(unit, new[] { data });
            foreach (var frame in Wait(() => boarding.GetTargets().Any(target => !previous.Contains(target.Handle)), "Generated dungeon target observation")) yield return frame;
            var targets = boarding.GetTargets().Where(target => !previous.Contains(target.Handle)).ToArray();
            Require(targets.Length == 1, "Generated dungeon target is ambiguous.");
            var target = targets[0].Handle;
            section = panel.RegisterSection(Id, "dungeon-status", _ => new DungeonPanelSection("Dungeon qualification", new string('W', 2048)));
            action = panel.RegisterAction(Id, "dungeon-disabled", _ => new DungeonPanelAction("Disabled dungeon probe", enabled: false), _ => throw new InvalidOperationException("Disabled action dispatched."), -99);
            enabledAction = panel.RegisterAction(Id, "dungeon-enabled", _ => new DungeonPanelAction("Enabled dungeon probe"), _ => calls++, -100);
            Require(panel.Open(target) == DungeonPanelOpenStatus.Opened, "Generated target did not open native panel.");
            foreach (var frame in Wait(() => panel.Current?.Target.Handle.Equals(target) == true, "Native dungeon panel snapshot")) yield return frame;
            var view = panel.Current!.ViewId;
            mouse = InputSystem.AddDevice<Mouse>();
            foreach (var frame in Wait(() => GameObject.Find("Mod API dungeon contributions") != null || GameObject.Find("Dungeon mod actions toggle") != null, "Dungeon controls visible")) yield return frame;
            var toggle = GameObject.Find("Dungeon mod actions toggle");
            if (toggle && !GameObject.Find("Mod API dungeon contributions"))
                foreach (var frame in DungeonClick(mouse, toggle!.transform)) yield return frame;
            foreach (var frame in Wait(() => DungeonProbeButton("Enabled dungeon probe")?.GetComponent<Image>().depth >= 0, "Dungeon action graphics")) yield return frame;
            foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Enabled dungeon probe")!.transform)) yield return frame;
            Require(calls == 1, "Enabled dungeon action did not dispatch exactly once.");
            Require(DungeonProbeButton("Disabled dungeon probe")?.interactable == false, "Disabled dungeon action became enabled.");
            var nativePanels = UnityEngine.Object.FindObjectsByType(NativeType("Behaviour.UI.Dungeon.DungeonPanel"), FindObjectsInactive.Exclude);
            Require(nativePanels.Length == 1, "Native dungeon panel is ambiguous.");
            nativePanels[0].GetType().GetMethod("Close")!.Invoke(nativePanels[0], null);
            foreach (var frame in Wait(() => panel.Current == null, "Native dungeon panel close")) yield return frame;
            Require(panel.Open(target) == DungeonPanelOpenStatus.Opened, "Dungeon panel did not reopen.");
            Require(panel.Current != null && panel.Current.ViewId != view, "Dungeon view identity survived close/reopen.");
            UnityEngine.Object.Destroy(root);
            foreach (var frame in Wait(() => panel.Current == null, "Destroyed dungeon target invalidates panel snapshot")) yield return frame;
            Require(panel.Open(target) == DungeonPanelOpenStatus.StaleTarget, "Destroyed target remained openable.");
            WriteAtomic("dungeon-panel.txt", new[] { "PASS", "dungeon-panel-v1", "generated-location-open-pointer-disabled-close-reopen-destroy" });
            Passed("Generated dungeon panel pointer and lifetime subset");
        }
        finally
        {
            enabledAction?.Dispose(); action?.Dispose(); section?.Dispose();
            if (mouse != null) InputSystem.RemoveDevice(mouse);
            if (oldMouse != null && oldMouse.added) oldMouse.MakeCurrent();
            if (root) UnityEngine.Object.Destroy(root);
        }
    }
    private static Button? DungeonProbeButton(string label)
    {
        var root = GameObject.Find("Mod API dungeon contributions");
        return root ? root!.GetComponentsInChildren<Button>().SingleOrDefault(button => button.GetComponentsInChildren<TMP_Text>().Any(text => text.text == label)) : null;
    }
    private IEnumerable<object?> DungeonClick(Mouse mouse, Transform target)
    {
        yield return null; yield return null;
        var point = ForgePointerPoint(target);
        InputSystem.QueueStateEvent(mouse, new MouseState { position = point });
        yield return null; yield return null;
        ForgePointerPoint(target, point);
        InputSystem.QueueStateEvent(mouse, new MouseState { position = point }.WithButton(MouseButton.Left));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = point });
        yield return null; yield return null;
    }
}
