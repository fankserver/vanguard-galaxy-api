using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private void CheckRefineryMultipleBatches(RecipeStationHandle station)
    {
        var nativeStation = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        var jobs = (IList)SpGet(SpGet(nativeStation, "refinery")!, "jobs")!;
        var recipe = ModApi.Recipes!.Read().Recipes.FirstOrDefault(candidate => candidate.Process == RecipeProcess.Refining
            && candidate.Outputs.Any(output => output.Amount != Math.Truncate(output.Amount))
            && ModApi.RecipeQuotes!.Quote(station, candidate.Id, 2) is var quote && quote.Status == RecipeQuoteStatus.Available
            && quote.Blockers.All(blocker => blocker == RecipeBlocker.PricingUnavailable));
        Require(recipe != null, "Fixture needs two further affordable fractional ore batches.");
        var oldJobs = jobs.Cast<object>().ToArray();
        var queued = ModApi.CraftingCommands!.Execute(CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station,
            recipe!.Id, 2, CraftingProtectionPolicy.NativeConsumption));
        Require(queued.Status == CraftingCommandStatus.Succeeded && queued.Jobs.Count == 1, "Multiple-batch refinery queue failed.");
        var job = jobs.Cast<object>().Single(candidate => !oldJobs.Any(old => ReferenceEquals(old, candidate)));
        var ore = SpGet(job, "ore")!;
        var bonusAllowed = !(bool)SpGet(ore, "ignoreExtraRewards")!
            && (bool)SpGet(SpGet(NativeType("Behaviour.Crew.SkilltreeNode"), "industrialRefBonusCraft1")!, "isActive")!;
        var before = ForgeInventoryCounts(nativeStation);
        var outcomes = new List<Dictionary<(RecipeInventoryKind, string, int, string), double>>
        { before.Where(pair => pair.Key.Item1 == RecipeInventoryKind.PlayerRefinedMaterials).ToDictionary(pair => pair.Key, pair => pair.Value) };
        for (var batch = 0; batch < 2; batch++)
        {
            var next = new List<Dictionary<(RecipeInventoryKind, string, int, string), double>>();
            foreach (var prior in outcomes)
            foreach (var multiplier in bonusAllowed ? new[] { 1f, 2f } : new[] { 1f })
            {
                var expected = prior.ToDictionary(pair => pair.Key, pair => pair.Value);
                foreach (var content in ((IEnumerable)SpGet(ore, "contents")!).Cast<object>())
                {
                    var key = (RecipeInventoryKind.PlayerRefinedMaterials, SpGet(content, "product")!.ToString()!, 0, "");
                    expected[key] = (double)(float)((float)expected[key] + Convert.ToSingle(SpGet(content, "yield")) * multiplier);
                }
                next.Add(expected);
            }
            outcomes = next;
        }
        var facts = new List<CraftingJobEvent>();
        using var observer = ModApi.CraftingJobs!.Subscribe(Id, facts.Add);
        SpCall(job, "ProgressJob", Convert.ToSingle(SpGet(job, "refineTime")) * 2f);
        var batches = facts.Where(fact => fact.Kind == CraftingJobEventKind.BatchObserved && fact.Job.Handle.Equals(queued.Jobs[0])).ToArray();
        Require(batches.Length == 2 && batches.All(batch => batch.DeliveryStatus == CraftingDeliveryStatus.Verified)
            && batches[0].Job.RemainingBatches == 1 && batches[1].Job.RemainingBatches == 0,
            "One native refinery progress call did not produce two verified ordered batches.");
        var after = ForgeInventoryCounts(nativeStation);
        Require(outcomes.Any(expected => expected.All(pair => after[pair.Key] == pair.Value)), "Multiple refinery yields differ from native contents and eligible bonus sequences.");
        CheckRefineryReceipts(before, after, batches.SelectMany(batch => batch.Deliveries));
        SpCall(SpGet(nativeStation, "refinery")!, "ProgressJobs", 0f);
        Require(!jobs.Contains(job) && facts.Count(fact => fact.Kind == CraftingJobEventKind.Finished && fact.Job.Handle.Equals(queued.Jobs[0])) == 1,
            "Completed refinery parent did not retire the job exactly once.");
        Passed("Two fractional refinery batches in one native progress call, native yield sequences, delivery receipts and retirement");
    }
}
