using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // Explicit delta-time driving exercises the real native completion loop, not wall-clock timing.
    private void CheckForgeDeliveries(RecipeStationHandle station)
    {
        var commands = ModApi.CraftingCommands!; var jobs = ModApi.CraftingJobs!;
        var nativeStation = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        var nativeForge = SpGet(nativeStation, "forge")!;
        var nativeJobs = (IList)SpGet(nativeForge, "jobs")!;
        var facts = new List<CraftingJobEvent>(); using var observer = jobs.Subscribe(Id, facts.Add);
        foreach (var partial in new[] { true, false })
        {
            Require(nativeJobs.Count < Convert.ToInt32(SpGet(nativeForge, "maxJobs")), "Delivery fixture needs a free Forge slot.");
            var recipe = ModApi.Services.Recipes!.Read().Recipes.FirstOrDefault(candidate => candidate.Process == RecipeProcess.Forge &&
            ModApi.Services.RecipeQuotes!.Quote(station, candidate.Id, 2) is var quote && quote.Status == RecipeQuoteStatus.Available
                && quote.Blockers.All(blocker => blocker == RecipeBlocker.PricingUnavailable));
            Require(recipe != null, "Delivery fixture lacks a supported affordable recipe.");
            var beforeJobs = nativeJobs.Cast<object>().ToArray();
            var queued = commands.Execute(CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station, recipe!.Id, 2, CraftingProtectionPolicy.NativeConsumption));
            Require(queued.Status == CraftingCommandStatus.Succeeded && queued.Jobs.Count == 1, "Delivery setup queue failed.");
            var nativeJob = nativeJobs.Cast<object>().Single(job => !beforeJobs.Any(old => ReferenceEquals(old, job)));
            var duration = Convert.ToSingle(SpGet(nativeJob, "craftingTime"));
            Require(duration > 0 && !float.IsNaN(duration) && !float.IsInfinity(duration), "Invalid native duration.");
            var before = ForgeInventoryCounts(nativeStation); facts.Clear();
            SpCall(nativeJob, "ProgressJob", duration * (partial ? 1f : 2f));
            var observed = facts.Where(fact => fact.Kind == CraftingJobEventKind.BatchObserved && fact.Job.Handle.Equals(queued.Jobs[0])).ToArray();
            Require(observed.Length == (partial ? 1 : 2), "Native loop batch event count differs from driven completions.");
            Require(Convert.ToInt32(SpGet(nativeJob, "remainingAmount")) == (partial ? 1 : 0), "Native remaining count differs from completed batches.");
            Require(observed.All(fact => fact.DeliveryStatus == CraftingDeliveryStatus.Verified), "Delivery receipt unresolved.");
            var after = ForgeInventoryCounts(nativeStation);
            var reported = observed.SelectMany(fact => fact.Deliveries).GroupBy(delivery =>
                (delivery.Destination!.Value, delivery.Resource!.LocalId, delivery.ItemLevel.GetValueOrDefault(), delivery.Rarity ?? ""))
                .ToDictionary(group => group.Key, group => group.Sum(delivery => delivery.VerifiedAmount!.Value));
            foreach (var key in before.Keys.Concat(after.Keys).Concat(reported.Keys).Distinct())
            {
                before.TryGetValue(key, out var oldCount); after.TryGetValue(key, out var newCount); reported.TryGetValue(key, out var delivered);
                Require(newCount - oldCount == delivered, "Delivery differs from actual native destination/resource/level/rarity inventory change.");
            }
            if (partial)
            {
                var refundBefore = ForgeInventoryCounts(nativeStation);
                var expectedRefund = new Dictionary<(RecipeInventoryKind, string, int, string), double>(refundBefore);
                var nativeRecipe = SpGet(nativeJob, "recipe")!; var level = Convert.ToInt32(SpGet(nativeJob, "craftedLevel"));
                foreach (var row in ((IEnumerable)SpCall(nativeRecipe, "GetIngredientMaterials", level)).Cast<object>())
                {
                    var key = (RecipeInventoryKind.PlayerRefinedMaterials, SpGet(row, "Item1")!.ToString()!, 0, "");
                    expectedRefund[key] = (double)(float)((float)expectedRefund[key] + Convert.ToSingle(SpGet(row, "Item2")));
                }
                foreach (var row in ((IEnumerable)SpCall(nativeRecipe, "GetIngredientItems", level)).Cast<object>())
                {
                    var item = SpGet(row, "Item1")!;
                    var key = (RecipeInventoryKind.StationMaterials, (string)SpGet(item, "identifier")!, Convert.ToInt32(SpGet(item, "itemLevel")), SpGet(item, "rarity")!.ToString()!);
                    expectedRefund.TryGetValue(key, out var existing); expectedRefund[key] = existing + Convert.ToInt32(SpGet(row, "Item2"));
                }
                var refundCredits = (long)SpGet(CurrentPlayer, "credits")!;
                var expectedCredits = Convert.ToInt64(SpGet(nativeRecipe, "craftingCost"));
                var result = commands.Execute(CraftingCommandRequest.Cancel(Id, Guid.NewGuid(), queued.Jobs[0]));
                var refundAfter = ForgeInventoryCounts(nativeStation);
                Require((long)SpGet(CurrentPlayer, "credits")! - refundCredits == expectedCredits, "Partial credit refund differs from one remaining native batch.");
                foreach (var key in expectedRefund.Keys.Concat(refundAfter.Keys).Distinct())
                {
                    expectedRefund.TryGetValue(key, out var expected); refundAfter.TryGetValue(key, out var actual);
                    Require(expected == actual, "Partial refund differs from native one-batch ingredient quantities/routes.");
                }
                Require(result.Deliveries.All(delivery => delivery.Status == CraftingDeliveryStatus.Verified), "Refund receipt unresolved.");
                var refunds = result.Deliveries.GroupBy(delivery => (delivery.Destination!.Value, delivery.Resource!.LocalId, delivery.ItemLevel.GetValueOrDefault(), delivery.Rarity ?? ""))
                    .ToDictionary(group => group.Key, group => group.Sum(delivery => delivery.VerifiedAmount!.Value));
                foreach (var key in refundBefore.Keys.Concat(refundAfter.Keys).Concat(refunds.Keys).Distinct())
                {
                    refundBefore.TryGetValue(key, out var oldAmount); refundAfter.TryGetValue(key, out var newAmount); refunds.TryGetValue(key, out var returned);
                    Require(newAmount - oldAmount == returned, "Partial cancellation refund differs from native resource change.");
                }
                Require(result.Status == CraftingCommandStatus.Succeeded && !nativeJobs.Contains(nativeJob), "Partial cancellation did not remove owned job.");
                Require(facts.Any(fact => fact.Kind == CraftingJobEventKind.Cancelled && fact.Job.Handle.Equals(queued.Jobs[0]) && fact.Job.RemainingBatches == 1), "Partial cancellation remaining count missing.");
            }
            else
            {
                SpCall(nativeForge, "ProgressJobs", 0f);
                Require(!nativeJobs.Contains(nativeJob), "Native finished job was not retired.");
                Require(facts.Count(fact => fact.Kind == CraftingJobEventKind.Finished && fact.Job.Handle.Equals(queued.Jobs[0])) == 1, "Finished event missing or duplicated.");
            }
        }
        Passed("Driven native Forge partial completion/cancellation and multiple batches with observed inventory delivery totals");
    }
    private Dictionary<(RecipeInventoryKind, string, int, string), double> ForgeInventoryCounts(object station)
    {
        var result = new Dictionary<(RecipeInventoryKind, string, int, string), double>();
        var inventories = new Dictionary<RecipeInventoryKind, object>
        {
            [RecipeInventoryKind.PlayerArmory] = SpGet(CurrentPlayer, "globalInventory")!,
            [RecipeInventoryKind.PlayerData] = SpGet(CurrentPlayer, "dataInventory")!,
            [RecipeInventoryKind.ShipCargo] = SpGet(SpGet(CurrentPlayer, "currentSpaceShip")!, "cargo")!,
            [RecipeInventoryKind.StationMaterials] = SpGet(station, "materialStorage")!
        };
        foreach (var inventory in inventories)
            foreach (var row in ((IEnumerable)SpGet(inventory.Value, "items")!).Cast<object>())
            {
                var item = SpGet(row, "item")!;
                var key = (inventory.Key, (string)SpGet(item, "identifier")!, Convert.ToInt32(SpGet(item, "itemLevel")), SpGet(item, "rarity")!.ToString()!);
                result.TryGetValue(key, out var amount); result[key] = checked(amount + Convert.ToInt64(SpGet(row, "count")));
            }
        foreach (var material in Enum.GetValues(NativeType("Source.Item.RefinedMaterial")))
            result[(RecipeInventoryKind.PlayerRefinedMaterials, material.ToString()!, 0, "")] = Convert.ToDouble(SpCall(CurrentPlayer, "CountRefinedMaterial", material));
        return result;
    }
}
