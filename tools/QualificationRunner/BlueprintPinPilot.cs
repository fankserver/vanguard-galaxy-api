using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckBlueprintPin()
    {
        WriteAtomic("blueprint-pin.txt", new[] { "INCOMPLETE" });
        var ui = ModApi.ForgeUi ?? throw new InvalidOperationException("Forge UI unavailable for Blueprint Pin.");
        // Native FadeOut disables only the background raycast; its label remains until destruction.
        var loadingScreen = NativeType("Behaviour.UI.Main_Menu.LoadingScreen");
        foreach (var frame in Wait(() => UnityEngine.Object.FindObjectsByType(loadingScreen, FindObjectsInactive.Exclude).Length == 0,
            "Native loading overlay destruction")) yield return frame;
        ForgeSelectionSnapshot? selection = null;
        foreach (var recipe in ModApi.Recipes!.Read().Recipes.Where(recipe => recipe.Process == RecipeProcess.Forge && recipe.ParentId != null))
        {
            if (ui.Open(recipe.Id) == ForgeNavigationStatus.Selected && ui.Current?.AvailableVariants.Count > 1)
            { selection = ui.Current; break; }
            yield return null;
        }
        Require(selection != null && selection.Batches == 1, "Blueprint Pin fixture needs a selectable one-batch variant group.");
        var selected = selection!.SelectedRecipe;
        var alternate = selection.AvailableVariants.First(recipe => !recipe.Equals(selected));
        var oldMouse = Mouse.current;
        Mouse? mouse = null;
        try
        {
            mouse = InputSystem.AddDevice<Mouse>();
            foreach (var frame in Wait(() => PinButton("Mod API Forge actions", "Pin") != null, "Real Blueprint Pin action")) yield return frame;
            CheckForgeBand();
            foreach (var frame in ForgeClick(mouse, PinButton("Mod API Forge actions", "Pin")!.transform)) yield return frame;
            foreach (var frame in Wait(() => PinHudText("1 batches remaining") && PinButton("Mod API shared HUD", "Open pinned recipe") != null, "Pinned batch target and HUD navigation")) yield return frame;
            foreach (var frame in CaptureForgeActions("blueprint-pin-view")) yield return frame;
            Require(ui.Open(alternate) == ForgeNavigationStatus.Selected && ui.Current!.SelectedRecipe.Equals(alternate), "Alternate recipe navigation failed.");
            foreach (var frame in ForgeClick(mouse, PinButton("Mod API shared HUD", "Open pinned recipe")!.transform)) yield return frame;
            foreach (var frame in Wait(() => ui.Current?.SelectedRecipe.Equals(selected) == true, "Pinned exact variant restored")) yield return frame;
            Require(PinHudText("1 batches remaining"), "Navigation changed the future-batch target.");
            foreach (var frame in Wait(() => PinCloseButton() != null, "Blueprint Pin panel close")) yield return frame;
            foreach (var frame in ForgeClick(mouse, PinCloseButton()!.transform)) yield return frame;
            foreach (var frame in Wait(() => GameObject.Find("Mod API shared HUD") == null && PinButton("Mod API Forge actions", "Pin") != null, "Closed pin and restored action")) yield return frame;
            foreach (var frame in CheckPinProducers(mouse, false)) yield return frame;
            foreach (var frame in CheckPinProducers(mouse, true)) yield return frame;
            WriteAtomic("blueprint-pin.txt", new[] { "PASS", "blueprint-pin-v2", "pin-batch-exact-variant-navigation-close-producer-routes" });
            Passed("Real Blueprint Pin pointer pinning, future-batch display, exact variant navigation, panel close, single producer navigation and alternative route choices");
        }
        finally { ProbeCleanup.Run(() => { if (mouse != null) InputSystem.RemoveDevice(mouse); }, () => oldMouse?.MakeCurrent()); }
    }
    private static Button? PinButton(string rootName, string label)
    {
        var root = GameObject.Find(rootName);
        if (root == null) return null;
        return root.GetComponentsInChildren<Button>().SingleOrDefault(button => button.IsActive() && button.IsInteractable()
            && button.targetGraphic != null && button.targetGraphic.depth >= 0 && button.GetComponentInChildren<TMP_Text>()?.text == label);
    }
    private static bool PinHudText(string text)
    {
        var root = GameObject.Find("Mod API shared HUD");
        return root != null && root.GetComponentsInChildren<TMP_Text>().Any(label => label.text.StartsWith(text + "  ", StringComparison.Ordinal) || label.text == text);
    }
    private static Button? PinCloseButton()
    {
        var root = GameObject.Find("Mod API shared HUD");
        return root == null ? null : root.GetComponentsInChildren<Button>().SingleOrDefault(button => button.name == "Close"
            && button.IsActive() && button.IsInteractable() && button.targetGraphic != null && button.targetGraphic.depth >= 0);
    }
}
