using System;
using System.Collections.Generic;
using UnityEngine.UI;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckForgeCallbackIsolation()
    {
        var ui = ModApi.Services.ForgeUi;
        Require(TrySetPinFixtureBatchOne(), "Callback fixture cannot select one batch.");
        var native = SpGet(NativeType("Behaviour.UI.Forge.ForgeUI"), "current")!;
        var slider = (Slider)SpGet(SpGet(native, "tabContents")!, "countSlider")!;
        Require(slider.minValue <= 1 && slider.maxValue >= 2, "Callback fixture needs two selectable batches.");
        var thrown = 0; var following = 0;
        Action<ForgeSelectionChange> fault = change =>
        {
            if (change.Current?.Batches != 2 || thrown != 0) return;
            thrown++;
            throw new InvalidOperationException("Expected qualification-only Forge listener failure.");
        };
        Action<ForgeSelectionChange> observer = change =>
        {
            if (change.Current?.Batches == 2 && thrown == 1) following++;
        };
        ui.Changed += fault;
        try
        {
            ui.Changed += observer;
            try
            {
                slider.value = 2;
                foreach (var frame in Wait(() => thrown == 1 && following > 0 && ui.Current?.Batches == 2,
                    "Throwing Forge listener does not suppress subsequent delivery")) yield return frame;
            }
            finally { ui.Changed -= observer; }
        }
        finally { ui.Changed -= fault; slider.value = 1; }
        Require(ui.Current?.Batches == 1, "Callback probe did not restore selected batch count.");
        WriteAtomic("forge-callback-isolation.txt", new[] { "PASS", "throwing-listener-isolated", "following-listener-delivered" });
    }
}
