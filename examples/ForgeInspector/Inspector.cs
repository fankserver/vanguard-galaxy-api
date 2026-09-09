using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VGModAPI;

namespace ForgeInspector;

/// <summary>Independent, Unity-free author example. A host owns and disposes this main-thread registration scope.</summary>
public sealed class Inspector : IDisposable
{
    private readonly List<IDisposable> _owned = new();
    private readonly IForgeUi _forge;
    private readonly IRecipeService _catalog;
    private readonly IRecipeQuoteService _quotes;
    private readonly IHudRegistration _hud;
    private readonly ILifecycleService _lifecycle;
    private RecipeId? _recipe;
    public Inspector(string pluginId, ILifecycleService lifecycle, IForgeUi forge, IRecipeService catalog, IRecipeQuoteService quotes, IHudService hud)
    {
        _forge = forge; _catalog = catalog; _quotes = quotes; _lifecycle = lifecycle;
        try
        {
            _hud = hud.Register(pluginId, "inspector", interaction =>
            {
                if (interaction.Kind == HudInteractionKind.ClosePanel) { _recipe = null; _hud!.Update(null, null); }
                else if (interaction.Kind == HudInteractionKind.Button && _recipe != null)
                {
                    var result = _forge.Open(_recipe);
                    // Navigation is revalidated by the API and never substitutes an arbitrary variant.
                    if (result != ForgeNavigationStatus.Selected) Show("Navigation unavailable", new[] { new HudRow("navigation", result.ToString()) });
                }
            });
            _owned.Add(_hud);
            _owned.Add(forge.RegisterAction(pluginId, "inspect", new("Inspect", "Copy the selected variant's requirements and output previews"), Capture));
            lifecycle.Changed += OnLifecycle;
        }
        catch { Dispose(); throw; }
    }
    private void OnLifecycle(LifecycleEvent fact)
    {
        if (fact.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
        { _recipe = null; _hud.Update(null, null); }
    }
    private void Capture(ForgeSelectionSnapshot selection)
    {
        _recipe = selection.SelectedRecipe;
        var quote = _quotes.Quote(selection.Station, _recipe, selection.Batches);
        var catalog = _catalog.Read();
        Show(selection.Presentation.DisplayName, Describe(quote, catalog));
    }
    public static IReadOnlyList<HudRow> Describe(RecipeQuote quote, RecipeCatalogSnapshot catalog)
    {
        var rows = new List<HudRow> { new("status", "Snapshot, not a reservation", quote.Status.ToString()) };
        if (quote.Status != RecipeQuoteStatus.Available) return rows;
        rows.Add(new("batches", quote.Batches + " batches (not output units)", "Refresh with Inspect; this example does not track a target"));
        foreach (var input in quote.Inputs)
        {
            var producers = catalog.Status == RecipeCatalogStatus.Available ? catalog.FindProducers(input.Resource).Count.ToString(CultureInfo.InvariantCulture) : "unknown";
            rows.Add(new("input" + rows.Count, Short(input.Resource.LocalId),
                Short($"need {Number(input.Required)}; accessible {Number(input.Available)}; producers {producers}"),
                input.Inventories.Any(value => !value.Accessible) ? "Inaccessible inventories excluded; producer count includes alternative variants" : "Producer count includes alternative variants"));
        }
        foreach (var output in quote.Outputs)
            rows.Add(new("output" + rows.Count, Short(output.Resource?.LocalId ?? "Generated output identity"),
                "If every batch yields it: " + Number(output.Amount), "Chance per batch: " + Number(output.ProbabilityPerBatch) + "; preview only, not delivered"));
        if (rows.Count <= 32) return rows;
        var hidden = rows.Count - 31;
        return rows.Take(31).Concat(new[] { new HudRow("overflow", hidden + " additional rows not shown", "Read the complete public quote in your own consumer") }).ToArray();
    }
    private void Show(string title, IEnumerable<HudRow> rows) => _hud.Update(new("Open exact variant", "Navigate at the current station"), new(Short(title), rows));
    private static string Number(double? value) => value?.ToString("G9", CultureInfo.InvariantCulture) ?? "unknown";
    private static string Short(string value) => value.Length <= 256 ? value : value.Substring(0, 253) + "...";
    public void Dispose()
    {
        _lifecycle.Changed -= OnLifecycle;
        _recipe = null;
        for (var index = _owned.Count - 1; index >= 0; index--) _owned[index].Dispose();
        _owned.Clear();
    }
}
