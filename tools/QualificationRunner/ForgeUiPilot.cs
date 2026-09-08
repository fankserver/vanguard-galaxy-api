using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
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
            var button = ForgeProbeButton("Forge probe first");
            Require(ForgeProbeButton("Forge probe second") != button, "Contributors did not render distinct buttons.");
            foreach (var frame in MenuClick(mouse, button.transform)) yield return frame;
            Require(calls.Count == 1 && calls[0].SelectedRecipe.Equals(firstRecipe), "Pointer action did not carry the exact selected recipe.");
            first.Update(new ForgeActionPresentation("Forge probe first", enabled: false));
            foreach (var frame in Wait(() => !button.interactable, "Disabled Forge action")) yield return frame;
            foreach (var frame in MenuClick(mouse, button.transform)) yield return frame;
            Require(calls.Count == 1, "Disabled Forge action dispatched.");
            first.Update(new ForgeActionPresentation("Forge probe first", "Exact selection probe", useSelectionIcon: true));
            foreach (var frame in Wait(() => button.interactable, "Enabled Forge action")) yield return frame;
            var rect = (RectTransform)button.transform;
            var point = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
            InputSystem.QueueStateEvent(mouse, new MouseState { position = point }.WithButton(MouseButton.Left));
            yield return null; yield return null;
            Require(ui.Open(otherRecipe) == ForgeNavigationStatus.Selected && ui.Current!.SelectedRecipe.Equals(otherRecipe), "Exact alternate variant navigation failed.");
            yield return null; yield return null;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = point });
            yield return null; yield return null;
            Require(calls.Count == 1, "Held pointer activated a replacement selection.");
            foreach (var frame in MenuClick(mouse, ForgeProbeButton("Forge probe first").transform)) yield return frame;
            Require(calls.Count == 2 && calls[1].SelectedRecipe.Equals(otherRecipe), "Fresh pointer did not activate the alternate variant.");
            var evidence = new StringBuilder("Forge UI native input probe\n");
            foreach (var frame in CaptureMenu("forge-ui-actions.png", evidence)) yield return frame;
            var oldView = ui.Current!.View;
            var interior = SpGet(NativeType("Behaviour.UI.Spacestation.SpaceStationInterior"), "instance")!;
            SpCall(interior, "GoToLocation", Enum.Parse(NativeType("Source.Galaxy.POI.SpaceStationFacility"), "Refinery"), true);
            foreach (var frame in Wait(() => ui.Current == null && GameObject.Find("Mod API Forge actions") == null, "Forge view teardown")) yield return frame;
            Require(ui.Open(firstRecipe) == ForgeNavigationStatus.Selected, "Forge reopening failed.");
            foreach (var frame in Wait(() => GameObject.Find("Mod API Forge actions") != null, "Forge action recreation")) yield return frame;
            Require(!ui.Current!.View.Equals(oldView), "Reopened Forge retained the old view handle.");
            foreach (var frame in MenuClick(mouse, ForgeProbeButton("Forge probe second").transform)) yield return frame;
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
    private static Button ForgeProbeButton(string label)
    {
        var root = GameObject.Find("Mod API Forge actions");
        Require(root != null, "Forge action root absent.");
        return root!.GetComponentsInChildren<Button>().Single(button => button.GetComponentsInChildren<TMP_Text>().Any(text => text.text == label));
    }
}
