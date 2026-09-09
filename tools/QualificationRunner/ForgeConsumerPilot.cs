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
    private IEnumerable<object?> CheckForgeConsumers(Mouse mouse)
    {
        var ui = ModApi.Services.ForgeUi;
        var recipe = ui.Current?.SelectedRecipe ?? throw new InvalidOperationException("Consumer fixture lacks a selected recipe.");
        Require(TrySetPinFixtureBatchOne(), "Consumer fixture cannot select one batch.");
        foreach (var frame in Wait(() => PinButton("Mod API Forge actions", "Inspect") != null, "Independent Inspector action")) yield return frame;
        foreach (var frame in ForgeClick(mouse, PinButton("Mod API Forge actions", "Inspect")!.transform)) yield return frame;
        foreach (var frame in Wait(() => ConsumerPanel("Snapshot, not a reservation") != null, "Independent quote snapshot")) yield return frame;
        Require(PinHudText("1 batches (not output units)"), "Inspector did not capture the selected batch count.");
        foreach (var frame in Wait(() => PinButton("Mod API Forge actions", "Pin") != null, "Coexisting Pin action")) yield return frame;
        foreach (var frame in ForgeClick(mouse, PinButton("Mod API Forge actions", "Pin")!.transform)) yield return frame;
        foreach (var frame in Wait(() => ConsumerPanel("1 batches remaining") != null && ConsumerPanel("Snapshot, not a reservation") != null, "Two independently owned panels")) yield return frame;
        Require(ConsumerPanel("1 batches remaining") != ConsumerPanel("Snapshot, not a reservation"), "Consumer models share one panel.");
        var navigation = PinButton("Mod API shared HUD", "Open exact variant");
        Require(navigation != null, "Inspector navigation button absent.");
        foreach (var frame in ForgeClick(mouse, navigation!.transform)) yield return frame;
        Require(ui.Current?.SelectedRecipe.Equals(recipe) == true, "Inspector navigation changed exact recipe identity.");
        Require(PinHudText("1 batches remaining"), "Inspector navigation cleared the pin.");
        var inspector = ConsumerPanel("Snapshot, not a reservation")!;
        var scroll = inspector.parent.parent.GetComponent<ScrollRect>();
        Require(scroll != null && scroll.horizontal, "Shared panel horizontal viewport missing.");
        scroll!.horizontalNormalizedPosition = inspector.GetSiblingIndex() == 0 ? 0 : 1;
        foreach (var frame in Wait(() => inspector.Find("Close").GetComponent<Image>().depth >= 0
            && !inspector.Find("Close").GetComponent<Image>().canvasRenderer.cull, "Inspector close after horizontal scroll")) yield return frame;
        foreach (var frame in CaptureForgeActions("forge-consumers")) yield return frame;
        foreach (var frame in ForgeClick(mouse, inspector.Find("Close"))) yield return frame;
        foreach (var frame in Wait(() => ConsumerPanel("Snapshot, not a reservation") == null && PinHudText("1 batches remaining"), "Inspector close preserves independent pin")) yield return frame;
        foreach (var frame in Wait(() => PinCloseButton() != null, "Remaining pin close control")) yield return frame;
        foreach (var frame in ForgeClick(mouse, PinCloseButton()!.transform)) yield return frame;
        foreach (var frame in Wait(() => GameObject.Find("Mod API shared HUD") == null, "Both consumers independently closed")) yield return frame;
    }
    private static Transform? ConsumerPanel(string rowLabel)
    {
        var root = GameObject.Find("Mod API shared HUD");
        var content = root != null ? root.transform.Find("Panels/Content") : null;
        return content == null ? null : content.Cast<Transform>().SingleOrDefault(panel => panel.GetComponentsInChildren<TMP_Text>()
            .Any(text => text.text == rowLabel || text.text.StartsWith(rowLabel + "  ", StringComparison.Ordinal)));
    }
}
