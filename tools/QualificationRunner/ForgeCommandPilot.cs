using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private void CheckCraftingSettingCommands(RecipeStationHandle station)
    {
        var commands = ModApi.CraftingCommands ?? throw new InvalidOperationException("Crafting commands unavailable.");
        var before = commands.ReadSettings(station.SessionId, station);
        Require(before.Available && before.StationAutoRefine.HasValue && before.PlayerCargoDelivery.HasValue
            && before.EffectiveAutoSell.HasValue && before.StoredAutoSellPreference == before.EffectiveAutoSell,
            "Settings fixture must have complete, synchronized saved/runtime settings.");
        var nativeStation = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        foreach (var setting in new[] { CraftingSetting.StationAutoRefine, CraftingSetting.PlayerCargoDelivery, CraftingSetting.PlayerAutoSell })
        {
            var original = setting == CraftingSetting.StationAutoRefine ? before.StationAutoRefine!.Value
                : setting == CraftingSetting.PlayerCargoDelivery ? before.PlayerCargoDelivery!.Value : before.EffectiveAutoSell!.Value;
            var owner = setting == CraftingSetting.StationAutoRefine ? station : null;
            var request = CraftingCommandRequest.Configure(Id, Guid.NewGuid(), station.SessionId, setting, !original, owner);
            try
            {
                Require(commands.Execute(request).Status == CraftingCommandStatus.Succeeded, "Setting mutation failed.");
                var native = setting == CraftingSetting.StationAutoRefine ? SpGet(SpGet(nativeStation, "refinery")!, "autoRefine")
                    : setting == CraftingSetting.PlayerCargoDelivery ? SpGet(CurrentPlayer, "forgeDepositInCargo")
                    : SpGet(NativeType("Source.Mining.Refinery"), "autoSell");
                Require((bool)native! == !original, "Setting result differs from native field.");
                var conflict = CraftingCommandRequest.Configure(Id, request.RequestId, station.SessionId, setting, original, owner);
                Require(commands.Execute(conflict).Status == CraftingCommandStatus.RequestConflict, "Conflicting request was not refused.");
            }
            finally
            {
                Require(commands.Execute(CraftingCommandRequest.Configure(Id, Guid.NewGuid(), station.SessionId, setting, original, owner)).Status == CraftingCommandStatus.Succeeded,
                    "Could not restore sandbox setting.");
            }
            Require(commands.Execute(request).IsReplay, "Setting replay was not recognized.");
            var after = commands.ReadSettings(station.SessionId, station);
            Require(after.StationAutoRefine == before.StationAutoRefine && after.PlayerCargoDelivery == before.PlayerCargoDelivery
                && after.EffectiveAutoSell == before.EffectiveAutoSell && after.StoredAutoSellPreference == before.StoredAutoSellPreference,
                "Replay changed restored settings or mutation affected another setting.");
        }
        Passed("Native setting commands, request conflicts, replay non-mutation and restoration");
    }

    private void CheckCraftingQueueAndCancel(RecipeStationHandle station)
    {
        var commands = ModApi.CraftingCommands!;
        var observations = ModApi.CraftingJobs!;
        var nativeStation = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        var nativeForge = SpGet(nativeStation, "forge")!;
        var nativeJobs = (IList)SpGet(nativeForge, "jobs")!;
        if (nativeJobs.Count >= Convert.ToInt32(SpGet(nativeForge, "maxJobs")))
        {
            // Deliberate disposable-fixture setup via the real cancellation API, not a list clear.
            var oldJob = observations.Read(station).Jobs.First(job => job.Process == RecipeProcess.Forge);
            Require(commands.Execute(CraftingCommandRequest.Cancel(Id, Guid.NewGuid(), oldJob.Handle)).Status == CraftingCommandStatus.Succeeded,
                "Could not free one copied-fixture Forge slot.");
        }
        Require(nativeJobs.Count < Convert.ToInt32(SpGet(nativeForge, "maxJobs")), "Fixture requires a free Forge slot.");
        var catalog = ModApi.Recipes!.Read();
        var candidate = catalog.Recipes.Where(recipe => recipe.Process == RecipeProcess.Forge).FirstOrDefault(recipe =>
        {
            var quote = ModApi.RecipeQuotes!.Quote(station, recipe.Id, 2);
            return quote.Status == RecipeQuoteStatus.Available && quote.Blockers.All(blocker => blocker == RecipeBlocker.PricingUnavailable);
        });
        Require(candidate != null, "Fixture needs inputs and credits for a two-batch Forge recipe.");
        var priorJobs = nativeJobs.Cast<object>().ToArray();
        var count = nativeJobs.Count; var credits = (long)SpGet(CurrentPlayer, "credits")!;
        var facts = new List<CraftingJobEvent>(); using var observer = observations.Subscribe(Id, facts.Add);
        var request = CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station, candidate!.Id, 2, CraftingProtectionPolicy.NativeConsumption);
        var result = commands.Execute(request);
        Require(result.Status == CraftingCommandStatus.Succeeded && result.Jobs.Count == 1, "Two-batch queue command failed.");
        var queuedCredits = (long)SpGet(CurrentPlayer, "credits")!;
        var added = nativeJobs.Cast<object>().Where(job => !priorJobs.Any(prior => ReferenceEquals(prior, job))).Single();
        Require(Convert.ToInt32(SpGet(added, "initialAmount")) == 2 && Convert.ToInt32(SpGet(added, "remainingAmount")) == 2,
            "Native job did not retain requested batch count.");
        var nativePrice = Convert.ToInt64(SpGet(SpGet(added, "recipe")!, "craftingCost"));
        Require(queuedCredits - credits == -(long)(float)checked(nativePrice * 2), "Actual queue charge differs from inspected native price conversion.");
        Require(nativeJobs.Count == count + 1 && result.CreditDelta == queuedCredits - credits, "Queue receipt does not match actual native list/credits.");
        Require(facts.Count(fact => fact.Kind == CraftingJobEventKind.Queued && fact.Job.Handle.Equals(result.Jobs[0])) == 1, "Queue fact missing or duplicated.");
        Require(commands.Execute(request).IsReplay && nativeJobs.Count == count + 1 && (long)SpGet(CurrentPlayer, "credits")! == queuedCredits,
            "Queue replay repeated native effects.");
        var cancel = commands.Execute(CraftingCommandRequest.Cancel(Id, Guid.NewGuid(), result.Jobs[0]));
        Require(cancel.Status == CraftingCommandStatus.Succeeded && nativeJobs.Count == count, "Cancellation did not remove its owned native job.");
        Require(facts.Count(fact => fact.Kind == CraftingJobEventKind.Cancelled && fact.Job.Handle.Equals(result.Jobs[0])) == 1, "Cancellation fact missing or duplicated.");
        Passed("Two-batch native queue and immediate cancellation with actual list/credit receipts and replay guard");
        CheckForgeCreditRefusal(station, candidate.Id, SpGet(added, "recipe")!);
    }
}
