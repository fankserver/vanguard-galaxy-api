using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private void CheckForgeCreditRefusal(RecipeStationHandle station, RecipeId recipe, object nativeRecipe)
    {
        Require(Convert.ToInt64(SpGet(nativeRecipe, "craftingCost")) > 0, "Refusal fixture needs a priced recipe.");
        var player = CurrentPlayer;
        var nativeStation = SpGet(player, "currentPointOfInterest")!;
        var forge = SpGet(nativeStation, "forge")!;
        var jobs = (IList)SpGet(forge, "jobs")!;
        var priorJobs = jobs.Cast<object>().ToArray();
        var before = ForgeInventoryCounts(nativeStation);
        var credits = (long)SpGet(player, "credits")!;
        var property = AccessTools.Property(player.GetType(), "credits");
        var facts = new List<CraftingJobEvent>();
        using var observer = new CraftingJobProbeSubscription(ModApi.Services.CraftingJobs, facts.Add);
        try
        {
            property.SetValue(player, 0L);
            var refused = ModApi.Services.CraftingCommands!.Execute(CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station,
                recipe, 1, CraftingProtectionPolicy.NativeConsumption));
            Require(refused.Status == CraftingCommandStatus.InsufficientCredits, "Public queue did not refuse zero credits.");
            Require(!(bool)SpCall(forge, "StartJob", nativeRecipe, 1)!, "Native direct start did not refuse zero credits.");
            var after = ForgeInventoryCounts(nativeStation);
            Require((long)SpGet(player, "credits")! == 0 && jobs.Cast<object>().SequenceEqual(priorJobs)
                && before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var amount) && amount == pair.Value)
                && facts.All(fact => fact.Kind != CraftingJobEventKind.Queued), "Refused queue changed inputs, credits, jobs or emitted an admitted queue.");
        }
        finally { property.SetValue(player, credits); }
        Require((bool)SpCall(forge, "StartJob", nativeRecipe, 1)!, "Affordable native direct start failed.");
        var admitted = facts.Where(fact => fact.Kind == CraftingJobEventKind.Queued).ToArray();
        Require(admitted.Length == 1 && jobs.Count == priorJobs.Length + 1 && admitted[0].Job.RemainingBatches == 1,
            "Native direct start was not observed exactly once.");
        var cancelled = ModApi.Services.CraftingCommands!.Execute(CraftingCommandRequest.Cancel(Id, Guid.NewGuid(), admitted[0].Job.Handle));
        Require(cancelled.Status == CraftingCommandStatus.Succeeded && jobs.Cast<object>().SequenceEqual(priorJobs),
            "Public cancellation could not remove the observed direct-start job.");
        Passed("Zero-credit public/native refusal without input mutation and observed native direct-start cancellation");
    }
}
