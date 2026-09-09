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
    private IEnumerable<object?> CheckPinProducers(Mouse mouse, bool multiple)
    {
        var ui = ModApi.ForgeUi!;
        var catalog = ModApi.Recipes!.Read();
        var quotes = ModApi.RecipeQuotes!;
        var station = quotes.CurrentStation!;
        var jobs = ModApi.CraftingJobs!.Read(station);
        Require(jobs.Status == CraftingJobQueryStatus.Available, "Producer test needs an available job query.");
        var discovery = new List<string> { "forgeRecipes=" + catalog.Recipes.Count(recipe => recipe.Process == RecipeProcess.Forge) };
        IReadOnlyList<RecipeSnapshot>? producers = null;
        var ingredientIndex = -1; var inputCount = 0;
        foreach (var recipe in catalog.Recipes.Where(recipe => recipe.Process == RecipeProcess.Forge))
        {
            if (jobs.Jobs.Any(job => job.Recipe.Equals(recipe.Id))) { discovery.Add(recipe.Id.LocalId + " skipped=job"); continue; }
            var navigation = ui.Open(recipe.Id);
            discovery.Add(recipe.Id.LocalId + " navigation=" + navigation);
            if (navigation != ForgeNavigationStatus.Selected) { yield return null; continue; }
            var quote = quotes.Quote(station, recipe.Id, 1);
            discovery.Add("quote=" + quote.Status + " batches=" + ui.Current?.Batches + " inputs=" + quote.Inputs.Count + " " +
                string.Join(";", quote.Inputs.Select(input => input.Resource.Kind + ":" + input.Resource.LocalId + " producers=" + catalog.FindProducers(input.Resource).Count)));
            if (quote.Status != RecipeQuoteStatus.Available || quote.Inputs.Count > 7 || ui.Current!.Batches != 1) { yield return null; continue; }
            for (var index = 0; index < quote.Inputs.Count; index++)
            {
                var candidates = catalog.FindProducers(quote.Inputs[index].Resource);
                if (multiple ? candidates.Count is < 2 or > 8 : candidates.Count != 1 || candidates[0].Process != RecipeProcess.Forge) continue;
                if (!multiple && (candidates[0].Id.Equals(recipe.Id) || ui.Open(candidates[0].Id) != ForgeNavigationStatus.Selected || ui.Open(recipe.Id) != ForgeNavigationStatus.Selected)) continue;
                producers = candidates; ingredientIndex = index; inputCount = quote.Inputs.Count; break;
            }
            if (producers != null) break;
            yield return null;
        }
        WriteAtomic(multiple ? "producer-discovery-alternative.txt" : "producer-discovery-single.txt", discovery.ToArray());
        Require(producers != null, "Fixture lacks selectable " + (multiple ? "alternative" : "single Forge") + " producer ingredients.");
        foreach (var frame in Wait(() => PinButton("Mod API Forge actions", "Pin") != null, "Producer test pin action")) yield return frame;
        foreach (var frame in ForgeClick(mouse, PinButton("Mod API Forge actions", "Pin")!.transform)) yield return frame;
        foreach (var frame in Wait(() => PinRows().Length == inputCount + 1 && PinRows().All(row => row.targetGraphic.depth >= 0), "Unallocated ingredient rows")) yield return frame;
        foreach (var frame in ForgeClick(mouse, PinRows()[ingredientIndex + 1].transform)) yield return frame;
        if (!multiple)
        {
            foreach (var frame in Wait(() => ui.Current?.SelectedRecipe.Equals(producers![0].Id) == true, "Single producer navigation")) yield return frame;
            Require(PinHudText("1 batches remaining"), "Producer navigation changed the target.");
        }
        else
        {
            foreach (var frame in Wait(() => PinHudText("Choose producer") && PinRows().Length == producers!.Count && PinRows().All(row => row.targetGraphic.depth >= 0), "Alternative producer choices")) yield return frame;
            var rows = PinRows();
            for (var index = 0; index < producers!.Count; index++)
            {
                Require(rows[index].GetComponentInChildren<TMP_Text>().text.Contains("producer" + index + " · " + producers[index].Process), "Producer choice identity/process missing.");
                Require(rows[index].GetComponentInChildren<TMP_Text>().text.Contains(" · " + producers[index].Id.LocalId), "Exact producer identity missing.");
                Require(rows[index].interactable == (producers[index].Process == RecipeProcess.Forge), "Unsupported refining route offered Forge navigation.");
            }
            foreach (var frame in CaptureForgeActions("blueprint-pin-producers")) yield return frame;
            // Back out of the chooser through the real close button; this must retain the pin.
            foreach (var frame in Wait(() => PinCloseButton() != null, "Producer chooser close")) yield return frame;
            foreach (var frame in ForgeClick(mouse, PinCloseButton()!.transform)) yield return frame;
            foreach (var frame in Wait(() => PinHudText("1 batches remaining"), "Producer chooser back preserves pin")) yield return frame;
        }
        foreach (var frame in Wait(() => PinCloseButton() != null, "Producer test panel close")) yield return frame;
        foreach (var frame in ForgeClick(mouse, PinCloseButton()!.transform)) yield return frame;
        foreach (var frame in Wait(() => GameObject.Find("Mod API shared HUD") == null, "Producer test pin cleared")) yield return frame;
    }
    private static Button[] PinRows()
    {
        var root = GameObject.Find("Mod API shared HUD");
        var content = root != null ? root.transform.Find("Panels/Content/Panel/Rows/Content") : null;
        return content != null ? content.GetComponentsInChildren<Button>() : Array.Empty<Button>();
    }
}
