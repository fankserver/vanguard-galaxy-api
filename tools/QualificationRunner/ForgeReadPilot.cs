using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // Read-only phase; successful reads do not qualify crafting mutations or physical UI input.
    private IEnumerable<object?> CheckForgeReads()
    {
        WriteAtomic("forge-reads.txt", new[] { "INCOMPLETE" });
        foreach (var frame in LoadReady("fixture-a")) yield return frame;
        foreach (var frame in Wait(NativeTravelReady, "Forge fixture readiness")) yield return frame;
        var catalog = ModApi.Recipes ?? throw new InvalidOperationException("Recipe catalog unavailable.");
        var quotes = ModApi.RecipeQuotes ?? throw new InvalidOperationException("Recipe quotes unavailable.");
        var jobs = ModApi.CraftingJobs ?? throw new InvalidOperationException("Crafting jobs unavailable.");
        var station = quotes.CurrentStation ?? throw new InvalidOperationException("Fixture must be at a station.");
        var nativeStation = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        Require(NativeType("Source.Galaxy.POI.SpaceStation").IsInstanceOfType(nativeStation), "Fixture is not a native station.");
        var forge = SpGet(nativeStation, "forge")!;
        var refinery = SpGet(nativeStation, "refinery")!;
        // No yield between before/after: natural game ticks cannot mask read-side effects.
        var nativeRecipes = ForgeReadRegistry.Expand(
            ((IEnumerable)SpGet(NativeType("Behaviour.Crafting.CraftingRecipe"), "all")!).Cast<object>(),
            recipe => ((IEnumerable)SpGet(recipe, "subRecipes")!).Cast<object>());
        var costs = nativeRecipes.Select(recipe => (int)SpGet(recipe, "dynamicCost")!).ToArray();
        var credits = (long)SpGet(CurrentPlayer, "credits")!;
        var forgeJobs = ((IEnumerable)SpGet(forge, "jobs")!).Cast<object>().ToArray();
        var refineryJobs = ((IEnumerable)SpGet(refinery, "jobs")!).Cast<object>().ToArray();
        var snapshot = catalog.Read(true);
        Require(snapshot.Status == RecipeCatalogStatus.Available && snapshot.Recipes.Count > 0, "Catalog failed or fixture has no recipes.");
        Require(snapshot.SessionId == _api!.CurrentSession!.Id, "Catalog session mismatch.");
        var ids = nativeRecipes.Select(recipe => "forge/" + (string)SpGet(recipe, "identifier")!).ToHashSet(StringComparer.Ordinal);
        foreach (var recipe in snapshot.Recipes.Where(recipe => recipe.Process == RecipeProcess.Forge))
            Require(ids.Contains(recipe.Id.LocalId), "Catalog Forge identity does not exist in native registry.");
        var availableQuotes = 0;
        foreach (var recipe in snapshot.Recipes)
        {
            var quote = quotes.Quote(station, recipe.Id, 2);
            if (quote.Status != RecipeQuoteStatus.Available) { Require(quote.Detail.Length > 0, "Refusal lacks detail."); continue; }
            availableQuotes++;
            Require(quote.CreditsAvailable == credits && quote.Station!.Equals(station), "Quote context differs from native player/station.");
            Require(quote.Inputs.SelectMany(input => input.Inventories).All(balance => balance.Accessible || balance.Amount == null), "Inaccessible stock masquerades as usable balance.");
        }
        Require(availableQuotes > 0, "No supported recipe quotes exercised.");
        var restored = jobs.Read(station);
        Require(restored.Status == CraftingJobQueryStatus.Available, "Restored job query unavailable.");
        Require(restored.Jobs.Count == forgeJobs.Length + refineryJobs.Length, "Public restored job count differs from native lists.");
        Require((long)SpGet(CurrentPlayer, "credits")! == credits, "Read changed credits.");
        Require(nativeRecipes.Select(recipe => (int)SpGet(recipe, "dynamicCost")!).SequenceEqual(costs), "Read warmed native recipe pricing.");
        Require(((IEnumerable)SpGet(forge, "jobs")!).Cast<object>().SequenceEqual(forgeJobs)
            && ((IEnumerable)SpGet(refinery, "jobs")!).Cast<object>().SequenceEqual(refineryJobs), "Read mutated native jobs.");
        WriteAtomic("forge-reads.txt", new[] { "PASS", "forge-reads-v1", "catalog=" + snapshot.Recipes.Count, "quotes=" + availableQuotes, "restored=" + restored.Jobs.Count });
        using (var sha = System.Security.Cryptography.SHA256.Create())
            WriteAtomic("forge-reads.receipt", new[] { "PASS", "forge-reads-v1", "sha256=" + BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(_root!, "forge-reads.txt")))).Replace("-", "").ToLowerInvariant() });
        Passed("Forge/refinery read-only native context and no pricing/job/credit mutation");
    }
}
