using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckForgeUiInput()
    {
        WriteAtomic("forge-ui.txt", new[] { "INCOMPLETE" });
        var ui = ModApi.ForgeUi ?? throw new InvalidOperationException("Forge UI service unavailable.");
        ForgeSelectionSnapshot? selection = null;
        foreach (var recipe in ModApi.Recipes!.Read().Recipes.Where(recipe => recipe.Process == RecipeProcess.Forge && recipe.ParentId != null))
        {
            if (ui.Open(recipe.Id) == ForgeNavigationStatus.Selected && ui.Current?.AvailableVariants.Count > 1)
            { selection = ui.Current; break; }
            yield return null;
        }
        Require(selection != null, "UI fixture needs a selectable native variant group.");
        var firstRecipe = selection!.SelectedRecipe;
        var otherRecipe = selection.AvailableVariants.First(recipe => !recipe.Equals(firstRecipe));
        var calls = new List<ForgeSelectionSnapshot>();
        using var first = ui.RegisterAction(Id, "forge-first", new ForgeActionPresentation("Forge probe first", "Exact selection probe", useSelectionIcon: true), calls.Add);
        using var second = ui.RegisterAction(Id, "forge-second", new ForgeActionPresentation("Forge probe second"), calls.Add);
        var oldMouse = Mouse.current;
        Mouse? mouse = null;
        try
        {
            mouse = InputSystem.AddDevice<Mouse>();
            foreach (var frame in Wait(() => GameObject.Find("Mod API Forge actions") != null, "Forge action rendering")) yield return frame;
            foreach (var frame in CaptureForgeActions()) yield return frame;
            var button = ForgeProbeButton("Forge probe first");
            Require(ForgeProbeButton("Forge probe second") != button, "Contributors did not render distinct buttons.");
            foreach (var frame in ForgeClick(mouse, button.transform)) yield return frame;
            Require(calls.Count == 1 && calls[0].SelectedRecipe.Equals(firstRecipe), "Pointer action did not carry the exact selected recipe.");
            first.Update(new ForgeActionPresentation("Forge probe first", enabled: false));
            foreach (var frame in Wait(() => !button.interactable, "Disabled Forge action")) yield return frame;
            foreach (var frame in ForgeClick(mouse, button.transform)) yield return frame;
            Require(calls.Count == 1, "Disabled Forge action dispatched.");
            first.Update(new ForgeActionPresentation("Forge probe first", "Exact selection probe", useSelectionIcon: true));
            foreach (var frame in Wait(() => button.interactable, "Enabled Forge action")) yield return frame;
            var point = ForgePointerPoint(button.transform);
            EventSystem.current.SetSelectedGameObject(null);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = point }.WithButton(MouseButton.Left));
            yield return null; yield return null;
            Require(EventSystem.current.currentSelectedGameObject == button.gameObject, "Held press did not reach the Forge button.");
            Require(ui.Open(otherRecipe) == ForgeNavigationStatus.Selected && ui.Current!.SelectedRecipe.Equals(otherRecipe), "Exact alternate variant navigation failed.");
            yield return null; yield return null;
            ForgePointerPoint(button.transform, point);
            InputSystem.QueueStateEvent(mouse, new MouseState { position = point });
            yield return null; yield return null;
            Require(calls.Count == 1, "Held pointer activated a replacement selection.");
            foreach (var frame in ForgeClick(mouse, ForgeProbeButton("Forge probe first").transform)) yield return frame;
            Require(calls.Count == 2 && calls[1].SelectedRecipe.Equals(otherRecipe), "Fresh pointer did not activate the alternate variant.");
            var oldView = ui.Current!.View;
            var interior = SpGet(NativeType("Behaviour.UI.Spacestation.SpaceStationInterior"), "instance")!;
            SpCall(interior, "GoToLocation", Enum.Parse(NativeType("Source.Galaxy.POI.SpaceStationFacility"), "Refinery"), true);
            foreach (var frame in Wait(() => ui.Current == null && GameObject.Find("Mod API Forge actions") == null, "Forge view teardown")) yield return frame;
            Require(ui.Open(firstRecipe) == ForgeNavigationStatus.Selected, "Forge reopening failed.");
            foreach (var frame in Wait(() => GameObject.Find("Mod API Forge actions") != null, "Forge action recreation")) yield return frame;
            Require(!ui.Current!.View.Equals(oldView), "Reopened Forge retained the old view handle.");
            foreach (var frame in ForgeClick(mouse, ForgeProbeButton("Forge probe second").transform)) yield return frame;
            Require(calls.Count == 3 && calls[2].SelectedRecipe.Equals(firstRecipe), "Registration did not survive native view replacement.");
            first.Dispose(); second.Dispose();
            foreach (var frame in Wait(() => GameObject.Find("Mod API Forge actions") == null, "Disposed Forge actions")) yield return frame;
            WriteAtomic("forge-ui.txt", new[] { "PASS", "forge-ui-v1", "variants-pointer-disabled-stale-reopen-dispose" });
            Passed("Native Forge variant navigation, pointer actions, disabled/stale input, view replacement and disposal");
        }
        finally
        {
            ProbeCleanup.Run(() => { if (mouse != null) InputSystem.RemoveDevice(mouse); }, () => oldMouse?.MakeCurrent());
        }
    }
    private IEnumerable<object?> CaptureForgeActions()
    {
        yield return null; yield return null;
        Require(GameObject.Find("Mod API Forge actions") != null, "Forge actions disappeared before capture.");
        var path = Path.Combine(_root!, "forge-ui-actions.png");
        Require(!File.Exists(path), "Refusing to overwrite Forge screenshot evidence.");
        ScreenCapture.CaptureScreenshot(path);
        foreach (var frame in Wait(() => File.Exists(path) && new FileInfo(path).Length > 0, "Forge screenshot")) yield return frame;
        using var hash = SHA256.Create();
        WriteAtomic("forge-ui-actions.txt", new[] { "sha256=" + BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant() });
    }
    private static Vector2 ForgePointerPoint(Transform target, Vector2? fixedPoint = null)
    {
        Canvas.ForceUpdateCanvases();
        var canvas = target.GetComponentInParent<Canvas>().rootCanvas;
        var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        Require(canvas.renderMode == RenderMode.ScreenSpaceOverlay || camera != null, "Forge canvas camera missing.");
        var rect = (RectTransform)target;
        var point = fixedPoint ?? RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
        var events = EventSystem.current;
        Require(events != null, "Forge EventSystem missing.");
        var hits = new List<RaycastResult>();
        events!.RaycastAll(new PointerEventData(events) { position = point }, hits);
        Require(hits.Count > 0 && (hits[0].gameObject.transform == target || hits[0].gameObject.transform.IsChildOf(target)),
            "Forge pointer point does not hit its intended button: point=" + point + " rect=" + rect.rect + " canvas=" + canvas.renderMode
            + " hits=" + string.Join("|", hits.Take(8).Select(hit => hit.gameObject.name + "@" + hit.gameObject.transform.parent?.name)));
        return point;
    }
    private static IEnumerable<object?> ForgeClick(Mouse mouse, Transform target)
    {
        var point = ForgePointerPoint(target);
        InputSystem.QueueStateEvent(mouse, new MouseState { position = point });
        yield return null; yield return null;
        ForgePointerPoint(target, point);
        InputSystem.QueueStateEvent(mouse, new MouseState { position = point }.WithButton(MouseButton.Left));
        yield return null; yield return null;
        InputSystem.QueueStateEvent(mouse, new MouseState { position = point });
        yield return null; yield return null;
    }
    private static Button ForgeProbeButton(string label)
    {
        var root = GameObject.Find("Mod API Forge actions");
        Require(root != null, "Forge action root absent.");
        return root!.GetComponentsInChildren<Button>().Single(button => button.GetComponentsInChildren<TMP_Text>().Any(text => text.text == label));
    }
}
