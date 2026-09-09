using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private void CheckRefineryDelivery(RecipeStationHandle station)
    {
        var commands = ModApi.Services.CraftingCommands!; var observations = ModApi.Services.CraftingJobs!;
        var nativeStation = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        var refinery = SpGet(nativeStation, "refinery")!;
        var nativeJobs = (IList)SpGet(refinery, "jobs")!;
        if (nativeJobs.Count >= Convert.ToInt32(SpGet(refinery, "maxJobs")))
        {
            var existing = observations.Read(station).Jobs.First(job => job.Process == RecipeProcess.Refining);
            Require(commands.Execute(CraftingCommandRequest.Cancel(Id, Guid.NewGuid(), existing.Handle)).Status == CraftingCommandStatus.Succeeded, "Could not free copied refinery slot.");
        }
        var recipe = ModApi.Services.Recipes!.Read().Recipes.FirstOrDefault(candidate => candidate.Process == RecipeProcess.Refining
            && candidate.Outputs.Any(output => output.Amount != Math.Truncate(output.Amount))
            && ModApi.Services.RecipeQuotes!.Quote(station, candidate.Id, 2) is var quote && quote.Status == RecipeQuoteStatus.Available
            && quote.Blockers.All(blocker => blocker == RecipeBlocker.PricingUnavailable));
        Require(recipe != null, "Fixture needs affordable fractional-yield ore and a free refinery slot.");
        var oldJobs = nativeJobs.Cast<object>().ToArray();
        var queued = commands.Execute(CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station, recipe!.Id, 2, CraftingProtectionPolicy.NativeConsumption));
        Require(queued.Status == CraftingCommandStatus.Succeeded && queued.Jobs.Count == 1, "Refinery queue failed.");
        var nativeJob = nativeJobs.Cast<object>().Single(job => !oldJobs.Any(old => ReferenceEquals(old, job)));
        var facts = new List<CraftingJobEvent>(); using var observer = new CraftingJobProbeSubscription(observations, facts.Add);
        var before = ForgeInventoryCounts(nativeStation);
        var nativeOre = SpGet(nativeJob, "ore")!;
        var bonusAllowed = !(bool)SpGet(nativeOre, "ignoreExtraRewards")!
            && (bool)SpGet(SpGet(NativeType("Behaviour.Crew.SkilltreeNode"), "industrialRefBonusCraft1")!, "isActive")!;
        var expectedMaterials = new List<Dictionary<(RecipeInventoryKind, string, int, string), double>>();
        foreach (var multiplier in bonusAllowed ? new[] { 1f, 2f } : new[] { 1f })
        {
            var expected = before.Where(pair => pair.Key.Item1 == RecipeInventoryKind.PlayerRefinedMaterials).ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var content in ((IEnumerable)SpGet(nativeOre, "contents")!).Cast<object>())
            {
                var key = (RecipeInventoryKind.PlayerRefinedMaterials, SpGet(content, "product")!.ToString()!, 0, "");
                expected[key] = (double)(float)((float)expected[key] + Convert.ToSingle(SpGet(content, "yield")) * multiplier);
            }
            expectedMaterials.Add(expected);
        }
        SpCall(nativeJob, "ProgressJob", Convert.ToSingle(SpGet(nativeJob, "refineTime")));
        var batch = facts.Single(fact => fact.Kind == CraftingJobEventKind.BatchObserved && fact.Job.Handle.Equals(queued.Jobs[0]));
        WriteAtomic("refinery-batch-diagnostic.txt", new[] { "status=" + batch.DeliveryStatus, "remaining=" + SpGet(nativeJob, "remainingAmount") }
            .Concat(batch.Deliveries.Select(delivery => delivery.Resource?.LocalId + " requested=" + delivery.RequestedAmount.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                + " actual=" + delivery.VerifiedAmount?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " status=" + delivery.Status)));
        Require(batch.DeliveryStatus == CraftingDeliveryStatus.Verified && Convert.ToInt32(SpGet(nativeJob, "remainingAmount")) == 1, "Refining partial batch not verified.");
        var deliveredInventory = ForgeInventoryCounts(nativeStation);
        Require(expectedMaterials.Any(expected => expected.All(pair => deliveredInventory[pair.Key] == pair.Value)), "Refinery fractional material yields differ from native contents and eligible bonus multiplier.");
        CheckRefineryReceipts(before, deliveredInventory, batch.Deliveries);
        var refundBefore = ForgeInventoryCounts(nativeStation);
        var credits = (long)SpGet(CurrentPlayer, "credits")!;
        var ore = SpGet(nativeJob, "ore")!; var item = SpGet(ore, "item")!;
        var refundKey = (RecipeInventoryKind.StationMaterials, (string)SpGet(item, "identifier")!, Convert.ToInt32(SpGet(item, "itemLevel")), SpGet(item, "rarity")!.ToString()!);
        var price = Convert.ToInt64(SpGet(ore, "refinementCost"));
        var cancelled = commands.Execute(CraftingCommandRequest.Cancel(Id, Guid.NewGuid(), queued.Jobs[0]));
        Require(cancelled.Status == CraftingCommandStatus.Succeeded && !nativeJobs.Contains(nativeJob), "Refinery partial cancellation failed.");
        var refundAfter = ForgeInventoryCounts(nativeStation);
        refundBefore.TryGetValue(refundKey, out var oldCount); refundAfter.TryGetValue(refundKey, out var newCount);
        Require(newCount - oldCount == 1 && (long)SpGet(CurrentPlayer, "credits")! - credits == price, "Refinery partial refund did not match one remaining ore and its native price.");
        CheckRefineryReceipts(refundBefore, refundAfter, cancelled.Deliveries);
        CheckRefineryFavouriteProtection(station, recipe.Id, item);
        Passed("Driven fractional refinery delivery and partial ore/credit cancellation with native inventory receipts");
    }
    private void CheckMaterialExtraction(RecipeStationHandle station)
    {
        var nativeStation = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        var commands = ModApi.Services.CraftingCommands!;
        RecipeResourceId? selected = null;
        foreach (var material in Enum.GetValues(NativeType("Source.Item.RefinedMaterial")))
        {
            var id = new RecipeResourceId("vanilla", material.ToString()!, RecipeResourceKind.RefinedMaterial);
            if (ModApi.Services.RecipeQuotes!.QuoteMaterialExtraction(station, id, 1).RequirementsMet) { selected = id; break; }
        }
        Require(selected != null, "Fixture lacks extractable material, credits or cargo space.");
        var before = ForgeInventoryCounts(nativeStation); var credits = (long)SpGet(CurrentPlayer, "credits")!;
        var value = Enum.Parse(NativeType("Source.Item.RefinedMaterial"), selected!.LocalId);
        var nativePrice = Convert.ToInt64(SpCall(SpGet(nativeStation, "refinery")!, "GetExtractCost", value, 1));
        var request = CraftingCommandRequest.Extract(Id, Guid.NewGuid(), station, selected, 1);
        var result = commands.Execute(request);
        Require(result.Status == CraftingCommandStatus.Succeeded, "Material extraction failed.");
        var after = ForgeInventoryCounts(nativeStation);
        var materialKey = (RecipeInventoryKind.PlayerRefinedMaterials, selected.LocalId, 0, "");
        Require(before[materialKey] - after[materialKey] == 1 && credits - (long)SpGet(CurrentPlayer, "credits")! == nativePrice, "Extraction debit differs from native material/credit cost.");
        before[materialKey] -= 1;
        CheckRefineryReceipts(before, after, result.Deliveries);
        Require(result.Deliveries.Count == 1 && result.Deliveries[0].Destination == RecipeInventoryKind.ShipCargo
            && result.Deliveries[0].Resource!.LocalId == "Canister" + selected.LocalId && result.Deliveries[0].VerifiedAmount == 1, "Extraction did not deliver one native canister to cargo.");
        Require(commands.Execute(request).IsReplay && (long)SpGet(CurrentPlayer, "credits")! == credits - nativePrice, "Extraction replay repeated charge.");
        var replay = ForgeInventoryCounts(nativeStation);
        Require(after.Count == replay.Count && after.All(pair => replay.TryGetValue(pair.Key, out var amount) && amount == pair.Value), "Extraction replay changed inventories.");
        Passed("Material extraction native debit/cargo receipt and replay non-mutation");
    }
    private static void CheckRefineryReceipts(Dictionary<(RecipeInventoryKind, string, int, string), double> before,
        Dictionary<(RecipeInventoryKind, string, int, string), double> after, IEnumerable<CraftingDeliverySnapshot> deliveries)
    {
        var list = deliveries.ToArray();
        Require(list.All(delivery => delivery.Status == CraftingDeliveryStatus.Verified), "Unresolved native transfer.");
        var amounts = list.GroupBy(delivery => (delivery.Destination!.Value, delivery.Resource!.LocalId, delivery.ItemLevel.GetValueOrDefault(), delivery.Rarity ?? ""))
            .ToDictionary(group => group.Key, group => group.Sum(delivery => delivery.VerifiedAmount!.Value));
        foreach (var key in before.Keys.Concat(after.Keys).Concat(amounts.Keys).Distinct())
        {
            before.TryGetValue(key, out var old); after.TryGetValue(key, out var current); amounts.TryGetValue(key, out var actual);
            Require(current - old == actual, "Native transfer receipt differs from sampled inventory changes.");
        }
    }
}
