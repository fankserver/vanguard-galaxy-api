using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // Runner-only scheduling control; the real consumer receives real native job observations.
    private IEnumerable<object?> CheckPinJobs(Mouse mouse)
    {
        var services = ModApi.Services;
        var station = services.RecipeQuotes.CurrentStation ?? throw new InvalidOperationException("Pin jobs require a station.");
        var nativeStation = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        var forge = SpGet(nativeStation, "forge")!;
        var nativeJobs = (IList)SpGet(forge, "jobs")!;
        var facts = new List<CraftingJobEvent>();
        using var observer = new CraftingJobProbeSubscription(services.CraftingJobs, facts.Add);
        var harmony = new Harmony(Id + ".pin-jobs");
        try
        {
            foreach (var type in new[] { "Source.Mining.Forge", "Source.Mining.Refinery" })
                harmony.Patch(AccessTools.Method(NativeType(type), "ProgressJobs", new[] { typeof(float) }),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(AllowCraftingProgress)));
            _pauseCraftingProgress = true;
            var existing = services.CraftingJobs.Read(station);
            Require(existing.Status == CraftingJobQueryStatus.Available, "Pin job baseline unavailable.");
            var occupied = new HashSet<RecipeId>(existing.Jobs.Select(job => job.Recipe));
            RecipeSnapshot? recipe = null;
            var discovery = new List<string>();
            foreach (var candidate in services.Recipes.Read().Recipes.Where(candidate => candidate.Process == RecipeProcess.Forge && !occupied.Contains(candidate.Id)))
            {
                var quote = services.RecipeQuotes.Quote(station, candidate.Id, 2);
                discovery.Add(candidate.Id.LocalId + " outputs=" + string.Join(",", candidate.Outputs.Select(output => output.Resource.LocalId + ":" + output.Amount)) + " quote=" + quote.Status + " blockers=" + string.Join(",", quote.Blockers));
                if (candidate.Outputs.Sum(output => output.Amount) <= 1 || quote.Status != RecipeQuoteStatus.Available
                    || quote.Blockers.Any(blocker => blocker != RecipeBlocker.PricingUnavailable)) continue;
                if (services.ForgeUi.Open(candidate.Id) != ForgeNavigationStatus.Selected) continue;
                var nativeUi = SpGet(NativeType("Behaviour.UI.Forge.ForgeUI"), "current")!;
                var slider = (UnityEngine.UI.Slider)SpGet(SpGet(nativeUi, "tabContents")!, "countSlider")!;
                if (slider == null || slider.minValue > 2 || slider.maxValue < 2) continue;
                slider.value = 2;
                if (services.ForgeUi.Current?.Batches != 2) continue;
                recipe = candidate; break;
            }
            WriteAtomic("blueprint-pin-jobs-discovery.txt", discovery);
            Require(recipe != null, "Pin job fixture lacks an affordable unoccupied multi-unit Forge batch.");
            foreach (var frame in Wait(() => PinButton("Mod API Forge actions", "Pin") != null, "Pin job action")) yield return frame;
            foreach (var frame in ForgeClick(mouse, PinButton("Mod API Forge actions", "Pin")!.transform)) yield return frame;
            foreach (var frame in Wait(() => PinHudText("2 batches remaining  0 allocated in queue"), "Two future batches before queue")) yield return frame;
            var beforeJobs = nativeJobs.Cast<object>().ToArray();
            var queued = services.CraftingCommands.Execute(CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station, recipe!.Id, 2, CraftingProtectionPolicy.NativeConsumption));
            Require(queued.Status == CraftingCommandStatus.Succeeded && queued.Jobs.Count == 1, "Pin two-batch queue failed.");
            var nativeJob = nativeJobs.Cast<object>().Single(job => !beforeJobs.Any(old => ReferenceEquals(old, job)));
            foreach (var frame in Wait(() => PinHudText("2 batches remaining  2 allocated in queue"), "Queue allocation without target decrement")) yield return frame;
            CheckPinBatch(nativeStation, nativeJob, queued.Jobs[0], facts);
            Require(Convert.ToInt32(SpGet(nativeJob, "remainingAmount")) == 1, "Partial native batch count differs.");
            foreach (var frame in Wait(() => PinHudText("1 batches remaining  1 allocated in queue"), "One verified batch, not output-unit decrement")) yield return frame;
            foreach (var frame in CaptureForgeActions("blueprint-pin-partial")) yield return frame;
            var cancelled = services.CraftingCommands.Execute(CraftingCommandRequest.Cancel(Id, Guid.NewGuid(), queued.Jobs[0]));
            Require(cancelled.Status == CraftingCommandStatus.Succeeded && !nativeJobs.Contains(nativeJob), "Pin partial cancellation failed.");
            Require(facts.Count(fact => fact.Kind == CraftingJobEventKind.Cancelled && fact.Job.Handle.Equals(queued.Jobs[0]) && fact.Job.RemainingBatches == 1) == 1, "Pin cancellation observation missing or duplicated.");
            foreach (var frame in Wait(() => PinHudText("1 batches remaining  0 allocated in queue"), "Cancellation releases allocation without fulfilling target")) yield return frame;
            foreach (var frame in CaptureForgeActions("blueprint-pin-cancelled")) yield return frame;
            beforeJobs = nativeJobs.Cast<object>().ToArray();
            queued = services.CraftingCommands.Execute(CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station, recipe.Id, 1, CraftingProtectionPolicy.NativeConsumption));
            Require(queued.Status == CraftingCommandStatus.Succeeded && queued.Jobs.Count == 1, "Pin replacement queue failed.");
            nativeJob = nativeJobs.Cast<object>().Single(job => !beforeJobs.Any(old => ReferenceEquals(old, job)));
            foreach (var frame in Wait(() => PinHudText("1 batches remaining  1 allocated in queue"), "Replacement allocation")) yield return frame;
            CheckPinBatch(nativeStation, nativeJob, queued.Jobs[0], facts);
            Require(Convert.ToInt32(SpGet(nativeJob, "remainingAmount")) == 0, "Replacement batch did not finish.");
            _pauseCraftingProgress = false;
            SpCall(forge, "ProgressJobs", 0f);
            _pauseCraftingProgress = true;
            Require(!nativeJobs.Contains(nativeJob), "Finished replacement job not retired.");
            foreach (var frame in Wait(() => GameObject.Find("Mod API shared HUD") == null && PinButton("Mod API Forge actions", "Pin") != null, "Verified target completion clears pin")) yield return frame;
        }
        finally { _pauseCraftingProgress = false; harmony.UnpatchSelf(); }
    }

    private void CheckPinBatch(object station, object nativeJob, CraftingJobHandle handle, List<CraftingJobEvent> facts)
    {
        var duration = Convert.ToSingle(SpGet(nativeJob, "craftingTime"));
        Require(duration > 0 && !float.IsNaN(duration) && !float.IsInfinity(duration), "Pin batch duration invalid.");
        var before = ForgeInventoryCounts(station);
        var start = facts.Count;
        SpCall(nativeJob, "ProgressJob", duration);
        var observed = facts.Skip(start).Where(fact => fact.Kind == CraftingJobEventKind.BatchObserved && fact.Job.Handle.Equals(handle)).ToArray();
        Require(observed.Length == 1 && observed[0].DeliveryStatus == CraftingDeliveryStatus.Verified, "Pin requires one verified native batch.");
        Require(observed[0].Deliveries.Sum(delivery => delivery.VerifiedAmount ?? 0) > 1, "Fixture did not deliver multiple units per batch.");
        var after = ForgeInventoryCounts(station);
        var reported = observed[0].Deliveries.GroupBy(delivery => (delivery.Destination!.Value, delivery.Resource!.LocalId, delivery.ItemLevel.GetValueOrDefault(), delivery.Rarity ?? ""))
            .ToDictionary(group => group.Key, group => group.Sum(delivery => delivery.VerifiedAmount!.Value));
        foreach (var key in before.Keys.Concat(after.Keys).Concat(reported.Keys).Distinct())
        {
            before.TryGetValue(key, out var oldCount); after.TryGetValue(key, out var newCount); reported.TryGetValue(key, out var delivered);
            Require(newCount - oldCount == delivered, "Pin verified delivery differs from actual inventory change.");
        }
    }
}
