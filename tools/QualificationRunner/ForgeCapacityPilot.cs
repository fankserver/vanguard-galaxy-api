using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private void CheckForgeQueueCapacity(RecipeStationHandle station, RecipeId recipe, object nativeRecipe)
    {
        var nativeStation = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        var forge = SpGet(nativeStation, "forge")!;
        var jobs = (IList)SpGet(forge, "jobs")!;
        var prior = jobs.Cast<object>().ToArray();
        var maximum = Convert.ToInt32(SpGet(forge, "maxJobs"));
        Require(maximum > jobs.Count && maximum - jobs.Count <= 8, "Capacity fixture needs one to eight free Forge slots.");
        var admitted = new List<CraftingJobHandle>();
        try
        {
            while (jobs.Count < maximum)
            {
                var count = jobs.Count;
                var result = ModApi.CraftingCommands!.Execute(CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station,
                    recipe, 1, CraftingProtectionPolicy.NativeConsumption));
                admitted.AddRange(result.Jobs);
                Require(result.Status == CraftingCommandStatus.Succeeded && result.Jobs.Count == 1 && jobs.Count == count + 1,
                    "Fixture cannot afford real jobs to fill Forge capacity.");
            }
            var before = ForgeInventoryCounts(nativeStation);
            var full = jobs.Cast<object>().ToArray();
            var credits = (long)SpGet(CurrentPlayer, "credits")!;
            var facts = new List<CraftingJobEvent>();
            using var observer = ModApi.CraftingJobs!.Subscribe(Id, facts.Add);
            var refused = ModApi.CraftingCommands!.Execute(CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station,
                recipe, 1, CraftingProtectionPolicy.NativeConsumption));
            Require(refused.Status == CraftingCommandStatus.QueueFull, "Public queue did not refuse full Forge capacity.");
            Require(!(bool)SpCall(forge, "TryStartJob", nativeRecipe, 1)!, "Native guarded start did not refuse full Forge capacity.");
            var after = ForgeInventoryCounts(nativeStation);
            Require(jobs.Cast<object>().SequenceEqual(full) && (long)SpGet(CurrentPlayer, "credits")! == credits
                && before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var amount) && amount == pair.Value)
                && facts.All(fact => fact.Kind != CraftingJobEventKind.Queued), "Full-queue refusal mutated inputs, jobs, credits or emitted admission.");
        }
        finally
        {
            foreach (var handle in admitted)
                Require(ModApi.CraftingCommands!.Execute(CraftingCommandRequest.Cancel(Id, Guid.NewGuid(), handle)).Status == CraftingCommandStatus.Succeeded,
                    "Could not cancel a capacity setup job.");
        }
        Require(jobs.Cast<object>().SequenceEqual(prior), "Capacity setup did not restore the original job list.");
        Passed("Real Forge queue capacity, public/native guarded refusal without mutation and setup cancellation");
    }
}
