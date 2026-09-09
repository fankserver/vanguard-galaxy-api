using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckGeneratedForgeDelivery()
    {
        var quotes = ModApi.Services.RecipeQuotes;
        var station = quotes.CurrentStation!;
        var nativeStation = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        var forge = SpGet(nativeStation, "forge")!;
        var nativeJobs = (IList)SpGet(forge, "jobs")!;
        RecipeSnapshot? selected = null;
        RecipeQuote? quote = null;
        var discovery = new List<string>();
        List<RecipeQuote>? preparation = null;
        foreach (var recipe in ModApi.Services.Recipes.Read().Recipes.Where(r => r.Outputs.Count == 1 && r.Outputs[0].Resource.Kind == RecipeResourceKind.EquipmentTemplate))
        {
            quote = quotes.Quote(station, recipe.Id, 1);
            discovery.Add(recipe.Id + " status=" + quote.Status + " blockers=" + string.Join(",", quote.Blockers)
                + " missing=" + string.Join(";", quote.Inputs.Select(i => i.Resource.LocalId + ":" + i.Missing)));
            preparation = GeneratedIngredientPlan(station, quote);
            if (quote.OutputLevel > 0 && preparation != null)
            { selected = recipe; discovery.Add("prepare=" + string.Join(";", preparation.Select(p => p.Recipe + "x" + p.Batches))); break; }
            yield return null;
        }
        WriteAtomic("forge-generated-discovery.txt", discovery.ToArray());
        Require(selected != null, "Fixture needs an affordable generated-equipment recipe.");
        Require(nativeJobs.Count < Convert.ToInt32(SpGet(forge, "maxJobs")), "Generated fixture needs a free Forge slot.");
        PrepareGeneratedIngredients(station, forge, nativeJobs, preparation!);
        quote = quotes.Quote(station, selected!.Id, 1);
        Require(quote.Status == RecipeQuoteStatus.Available && quote.Blockers.All(b => b == RecipeBlocker.PricingUnavailable), "Prepared equipment remains unavailable.");
        var oldJobs = nativeJobs.Cast<object>().ToArray();
        var commands = ModApi.Services.CraftingCommands;
        var facts = new List<CraftingJobEvent>();
        using var observer = new CraftingJobProbeSubscription(ModApi.Services.CraftingJobs, facts.Add);
        var queued = commands.Execute(CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station, selected!.Id, 1, CraftingProtectionPolicy.NativeConsumption));
        Require(queued.Status == CraftingCommandStatus.Succeeded && queued.Jobs.Count == 1, "Generated equipment queue failed.");
        var job = nativeJobs.Cast<object>().Single(j => !oldJobs.Any(old => ReferenceEquals(old, j)));
        Require(Convert.ToInt32(SpGet(job, "craftedLevel")) == quote!.OutputLevel, "Generated job level differs from quote.");
        var before = ForgeInventoryCounts(nativeStation);
        var duration = Convert.ToSingle(SpGet(job, "craftingTime"));
        Require(duration > 0 && !float.IsNaN(duration) && !float.IsInfinity(duration), "Generated job duration invalid.");
        Require("forge/" + (string)SpGet(SpGet(job, "recipe")!, "identifier")! == selected.Id.LocalId, "Queued native recipe differs from generated fixture.");
        SpCall(job, "ProgressJob", duration);
        var delivered = facts.Where(f => f.Kind == CraftingJobEventKind.BatchObserved && f.Job.Handle.Equals(queued.Jobs[0])).ToArray();
        WriteAtomic("forge-generated-delivery-diagnostic.txt", new[] {
            "recipe=" + selected.Id + " level=" + quote.OutputLevel + " duration=" + duration + " remaining=" + SpGet(job, "remainingAmount"),
            "facts=" + facts.Count + " ownedBatches=" + delivered.Length
        }.Concat(facts.Select(f => f.Kind + " status=" + f.DeliveryStatus + " remaining=" + f.Job.RemainingBatches
            + " deliveries=" + string.Join(";", f.Deliveries.Select(d => d.Resource + " requested=" + d.RequestedAmount + " verified=" + d.VerifiedAmount
                + " status=" + d.Status + " level=" + d.ItemLevel + " rarity=" + d.Rarity + " detail=" + d.Detail)))).ToArray());
        Require(delivered.Length == 1 && delivered[0].DeliveryStatus == CraftingDeliveryStatus.Verified, "Generated delivery was not verified exactly once.");
        Require(delivered[0].Deliveries.Any(d => d.Resource?.Kind == RecipeResourceKind.Item && d.ItemLevel == quote.OutputLevel
            && d.Rarity == selected.Rarity && d.VerifiedAmount >= selected.Outputs[0].Amount), "Generated item level/rarity/quantity not observed in delivery.");
        var after = ForgeInventoryCounts(nativeStation);
        var reported = delivered.SelectMany(f => f.Deliveries).GroupBy(d => (d.Destination!.Value, d.Resource!.LocalId, d.ItemLevel.GetValueOrDefault(), d.Rarity ?? ""))
            .ToDictionary(g => g.Key, g => g.Sum(d => d.VerifiedAmount!.Value));
        foreach (var key in before.Keys.Concat(after.Keys).Concat(reported.Keys).Distinct())
        {
            before.TryGetValue(key, out var oldCount); after.TryGetValue(key, out var count); reported.TryGetValue(key, out var amount);
            Require(count - oldCount == amount, "Generated delivery differs from native inventory destination/identity/level/rarity delta.");
        }
        SpCall(forge, "ProgressJobs", 0f);
        Require(!nativeJobs.Contains(job), "Generated job was not retired.");
        WriteAtomic("forge-generated.txt", new[] { "PASS", "generated-equipment-delivered", "level-and-inventory-reconciled" });
    }
}
