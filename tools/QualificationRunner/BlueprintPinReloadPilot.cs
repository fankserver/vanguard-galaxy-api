using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckPinReload(Mouse mouse)
    {
        var services = ModApi.Services;
        var selection = services.ForgeUi.Current ?? throw new InvalidOperationException("Pin reload requires a Forge selection.");
        var recipe = selection.SelectedRecipe;
        Require(TrySetPinFixtureBatchOne(), "Reload fixture cannot select one future batch.");
        foreach (var frame in Wait(() => PinButton("Mod API Forge actions", "Pin") != null, "Pin action before save")) yield return frame;
        foreach (var frame in ForgeClick(mouse, PinButton("Mod API Forge actions", "Pin")!.transform)) yield return frame;
        foreach (var frame in Wait(() => PinHudText("1 batches remaining"), "Pin before save")) yield return frame;
        Save("qa-pin-roundtrip", LifecycleEventKind.SaveSucceeded);
        Require(PinHudText("1 batches remaining"), "Saving unexpectedly cleared the session pin.");
        foreach (var frame in LoadReady("qa-pin-roundtrip")) yield return frame;
        foreach (var frame in WaitForPinReload(recipe, selection.Station)) yield return frame;
        // Prove the same consumer can register/use the new view after its session callback cleared the old pin.
        Require(TrySetPinFixtureBatchOne(), "Reloaded fixture cannot select one future batch.");
        foreach (var frame in Wait(() => PinButton("Mod API Forge actions", "Pin") != null, "Reloaded Pin action")) yield return frame;
        foreach (var frame in ForgeClick(mouse, PinButton("Mod API Forge actions", "Pin")!.transform)) yield return frame;
        foreach (var frame in Wait(() => PinHudText("1 batches remaining"), "New session pin")) yield return frame;
        var secondStation = services.RecipeQuotes.CurrentStation ?? throw new InvalidOperationException("Reloaded station unavailable.");
        Save("qa-pin-save-as", LifecycleEventKind.SaveSucceeded);
        Require(PinHudText("1 batches remaining"), "Save-as unexpectedly cleared the session pin.");
        foreach (var frame in LoadReady("fixture-b")) yield return frame;
        foreach (var frame in Wait(() => GameObject.Find("Mod API shared HUD") == null, "Slot switch clears session pin")) yield return frame;
        foreach (var frame in LoadReady("qa-pin-save-as")) yield return frame;
        foreach (var frame in WaitForPinReload(recipe, secondStation)) yield return frame;
    }
    private IEnumerable<object?> WaitForPinReload(RecipeId recipe, RecipeStationHandle oldStation)
    {
        var services = ModApi.Services;
        var loadingScreen = NativeType("Behaviour.UI.Main_Menu.LoadingScreen");
        foreach (var frame in Wait(() => UnityEngine.Object.FindObjectsByType(loadingScreen, FindObjectsInactive.Exclude).Length == 0
            && services.RecipeQuotes.CurrentStation != null, "Reload station and loading overlay readiness")) yield return frame;
        Require(services.RecipeQuotes.CurrentStation!.SessionId != oldStation.SessionId, "Reload retained session identity.");
        Require(services.CraftingJobs.Read(oldStation).Status == CraftingJobQueryStatus.StaleHandle, "Old pin station remains usable after reload.");
        Require(GameObject.Find("Mod API shared HUD") == null, "Session pin was restored unexpectedly.");
        Require(services.ForgeUi.Open(recipe) == ForgeNavigationStatus.Selected, "Exact recipe cannot reopen after reload.");
        Require(TrySetPinFixtureBatchOne(), "Reopened recipe cannot select one future batch.");
        foreach (var frame in Wait(() => PinButton("Mod API Forge actions", "Pin") != null, "New view has unpinned action")) yield return frame;
        Require(services.ForgeUi.Current?.SelectedRecipe.Equals(recipe) == true, "Reload opened a different recipe.");
    }
}
