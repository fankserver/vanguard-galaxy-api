using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private string? _dungeonPointerDiagnostic;
    private readonly List<string> _dungeonPointerRecords = new();
    // A generated, scene-local installation exercises native panel lifetime without saving or starting combat.
    private IEnumerable<object?> CheckDungeonPanelLifetime()
    {
        _dungeonPointerDiagnostic = null; _dungeonPointerRecords.Clear();
        WriteAtomic("dungeon-panel.txt", new[] { "INCOMPLETE" });
        var boarding = ModApi.Services.Boarding;
        var panel = ModApi.Services.DungeonPanel;
        Require(boarding.Availability.IsAvailable && panel.Availability.IsAvailable, "Dungeon panel services unavailable.");
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
        IDisposable? section = null; IDisposable? action = null; IDisposable? enabledAction = null; IDisposable? peerAction = null;
        var oldMouse = Mouse.current; Mouse? mouse = null; var calls = 0; var disabledCalls = 0; var peerCalls = 0; var allowed = true;
        UnityEngine.Object? nativePanel = null;
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
            action = panel.RegisterAction(Id, "dungeon-disabled", _ => new DungeonPanelAction("Disabled dungeon probe", enabled: false), _ => disabledCalls++, -99);
            enabledAction = panel.RegisterAction(Id, "dungeon-enabled", _ => new DungeonPanelAction("Enabled dungeon probe", enabled: allowed), _ => calls++, -100);
            peerAction = panel.RegisterAction("vgmodapi.qualification.peer", "peer-action", _ => new DungeonPanelAction("Peer dungeon probe"), _ => peerCalls++, -98);
            Require(panel.Open(target) == DungeonPanelOpenStatus.Opened, "Generated target did not open native panel.");
            foreach (var frame in Wait(() => panel.Current?.Target.Handle.Equals(target) == true, "Native dungeon panel snapshot")) yield return frame;
            var view = panel.Current!.ViewId;
            var nativePanels = UnityEngine.Object.FindObjectsByType(NativeType("Behaviour.UI.Dungeon.DungeonPanel"), FindObjectsInactive.Exclude);
            Require(nativePanels.Length == 1, "Native dungeon panel is ambiguous.");
            nativePanel = nativePanels[0];
            mouse = InputSystem.AddDevice<Mouse>();
            foreach (var frame in Wait(() => GameObject.Find("Mod API dungeon contributions") != null || GameObject.Find("Dungeon mod actions toggle") != null, "Dungeon controls visible")) yield return frame;
            var toggle = GameObject.Find("Dungeon mod actions toggle");
            if (toggle && !GameObject.Find("Mod API dungeon contributions"))
                foreach (var frame in DungeonClick(mouse, toggle!.transform)) yield return frame;
            foreach (var frame in Wait(() => DungeonProbeButton("Enabled dungeon probe")?.GetComponent<Image>().depth >= 0, "Dungeon action graphics")) yield return frame;
            foreach (var frame in Wait(() => DungeonPointerReady(DungeonProbeButton("Enabled dungeon probe")!.transform), "Dungeon pointer readiness before capture")) yield return frame;
            yield return new WaitForEndOfFrame();
            var capture = Path.Combine(_root!, "dungeon-panel-actions.png");
            Require(!File.Exists(capture), "Refusing to overwrite dungeon screenshot.");
            ScreenCapture.CaptureScreenshot(capture);
            foreach (var frame in Wait(() => File.Exists(capture) && new FileInfo(capture).Length > 0, "Dungeon screenshot")) yield return frame;
            using (var hash = SHA256.Create()) WriteAtomic("dungeon-panel-actions.txt", new[] { "sha256=" + BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(capture))).Replace("-", "").ToLowerInvariant() });
            foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Enabled dungeon probe")!.transform)) yield return frame;
            Require(calls == 1, "Enabled dungeon action did not dispatch exactly once.");
            Require(DungeonProbeButton("Disabled dungeon probe")?.interactable == false, "Disabled dungeon action became enabled.");
            foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Disabled dungeon probe")!.transform)) yield return frame;
            Require(disabledCalls == 0 && calls == 1, "Disabled pointer click dispatched an action.");
            var cached = DungeonProbeButton("Enabled dungeon probe")!.onClick;
            allowed = false; cached.Invoke();
            Require(calls == 1, "Cached enabled state bypassed activation revalidation.");
            allowed = true;
            foreach (var frame in DungeonClick(mouse, DungeonProbeButton("Peer dungeon probe")!.transform)) yield return frame;
            Require(peerCalls == 1 && calls == 1, "Contributors shared dispatch state.");
            var retired = DungeonProbeButton("Peer dungeon probe")!.onClick;
            peerAction.Dispose(); peerAction = null; retired.Invoke();
            Require(peerCalls == 1, "Disposed contributor callback dispatched.");
            foreach (var frame in Wait(() => DungeonProbeButton("Peer dungeon probe") == null, "Disposed contributor removal")) yield return frame;
            foreach (var frame in CheckDungeonPanelNavigation(panel)) yield return frame;
            nativePanel.GetType().GetMethod("Close")!.Invoke(nativePanel, null);
            foreach (var frame in Wait(() => panel.Current == null, "Native dungeon panel close")) yield return frame;
            cached.Invoke(); Require(calls == 1, "Closed panel callback dispatched.");
            Require(panel.Open(target) == DungeonPanelOpenStatus.Opened, "Dungeon panel did not reopen.");
            Require(panel.Current != null && panel.Current.ViewId != view, "Dungeon view identity survived close/reopen.");
            cached.Invoke(); Require(calls == 1, "Previous view callback dispatched after reopen.");
            UnityEngine.Object.Destroy(root);
            foreach (var frame in Wait(() => panel.Current == null, "Destroyed dungeon target invalidates panel snapshot")) yield return frame;
            Require(panel.Open(target) == DungeonPanelOpenStatus.StaleTarget, "Destroyed target remained openable.");
            WriteAtomic("dungeon-panel.txt", new[] { "PASS", "dungeon-panel-v3", "generated-location-pointer-disabled-revalidate-contributors-dispose-stale-reopen-destroy-keyboard-controller" });
            Passed("Generated dungeon panel pointer and lifetime subset");
        }
        finally
        {
            try { if (nativePanel) nativePanel!.GetType().GetMethod("Close")!.Invoke(nativePanel, null); }
            finally
            {
                peerAction?.Dispose(); enabledAction?.Dispose(); action?.Dispose(); section?.Dispose();
                if (mouse != null) InputSystem.RemoveDevice(mouse);
                if (oldMouse != null && oldMouse.added) oldMouse.MakeCurrent();
                if (root) UnityEngine.Object.Destroy(root);
            }
        }
    }
    private static Button? DungeonProbeButton(string label)
    {
        var root = GameObject.Find("Mod API dungeon contributions");
        return root ? root!.GetComponentsInChildren<Button>().SingleOrDefault(button => button.GetComponentsInChildren<TMP_Text>().Any(text => text.text == label)) : null;
    }
    private bool DungeonPointerReady(Transform target)
    {
        Canvas.ForceUpdateCanvases();
        if (!target || !target.gameObject.activeInHierarchy || !EventSystem.current) return false;
        var canvas = target.GetComponentInParent<Canvas>()?.rootCanvas;
        if (!canvas) return false;
        var camera = canvas!.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        if (canvas.renderMode != RenderMode.ScreenSpaceOverlay && !camera) return false;
        var rect = (RectTransform)target;
        var point = RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
        var ready = hits.Count > 0 && (hits[0].gameObject.transform == target || hits[0].gameObject.transform.IsChildOf(target));
        if (_dungeonPointerRecords.Count < 12)
        {
            var image = target.GetComponent<Image>();
            var lines = new[]
            {
                "ready=" + ready, "hitCount=" + hits.Count,
                "raycastTarget=" + (image && image!.raycastTarget),
                "groups=" + string.Join("|", target.GetComponentsInParent<CanvasGroup>(true).Take(12).Select(group => group.name + ":blocks=" + group.blocksRaycasts + ",interactable=" + group.interactable + ",alpha=" + group.alpha)),
                "raycaster=" + (canvas.GetComponent<GraphicRaycaster>() is { } raycaster ? raycaster.enabled.ToString() : "none") + " canvasEnabled=" + canvas.enabled,
                "contains=" + RectTransformUtility.RectangleContainsScreenPoint(rect, point, camera),
                "target=" + target.name, "point=" + point, "rect=" + rect.rect, "scale=" + target.lossyScale,
                "screen=" + Screen.width + "x" + Screen.height, "canvas=" + canvas.renderMode,
                "depth=" + (image ? image!.depth.ToString() : "none"), "culled=" + (image && image!.canvasRenderer.cull),
                "ancestors=" + string.Join("|", target.GetComponentsInParent<RectTransform>().Take(12).Select(parent => parent.name + ":" + parent.rect)),
                "hits=" + string.Join("|", hits.Take(8).Select(hit => hit.gameObject.name + "@" + hit.gameObject.transform.parent?.name))
            };
            var diagnostic = string.Join("\n", lines);
            if (_dungeonPointerDiagnostic != diagnostic)
            {
                _dungeonPointerRecords.Add("frame=" + Time.frameCount + " time=" + Time.realtimeSinceStartup + "\n" + diagnostic);
                WriteAtomic("dungeon-pointer-diagnostic.txt", _dungeonPointerRecords);
                _dungeonPointerDiagnostic = diagnostic;
            }
        }
        return ready;
    }
    private IEnumerable<object?> DungeonClick(Mouse mouse, Transform target)
    {
        foreach (var frame in Wait(() => DungeonPointerReady(target), "Dungeon pointer raycast readiness (including loading overlay)")) yield return frame;
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
