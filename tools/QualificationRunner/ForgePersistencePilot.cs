using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private static bool _pauseCraftingProgress;
    private static bool AllowCraftingProgress() => !_pauseCraftingProgress;

    // Paused native progression isolates serialization from natural completion. It does not qualify timing or delivery.
    private IEnumerable<object?> CheckCraftingPersistence()
    {
        var harmony = new Harmony(Id + ".crafting-persistence");
        var jobs = ModApi.CraftingJobs ?? throw new InvalidOperationException("Crafting jobs unavailable.");
        var quotes = ModApi.Services.RecipeQuotes!;
        var commands = ModApi.CraftingCommands!;
        var facts = new List<CraftingJobEvent>();
        using var subscription = jobs.Subscribe(Id, facts.Add);
        try
        {
            foreach (var type in new[] { "Source.Mining.Forge", "Source.Mining.Refinery" })
                harmony.Patch(AccessTools.Method(NativeType(type), "ProgressJobs", new[] { typeof(float) }),
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(AllowCraftingProgress)));
            _pauseCraftingProgress = true;
            var station = quotes.CurrentStation ?? throw new InvalidOperationException("Persistence fixture requires station.");
            var baseline = jobs.Read(station);
            Require(baseline.Status == CraftingJobQueryStatus.Available && baseline.Jobs.Count > 0, "Persistence fixture must retain native jobs.");
            var signature = CraftingSaveSignature(baseline);
            var settings = commands.ReadSettings(station.SessionId, station);
            Require(settings.Available && settings.PlayerCargoDelivery.HasValue, "Persistence settings unavailable.");
            Require(settings.StationAutoRefine.HasValue && settings.StoredAutoSellPreference.HasValue, "Persistence settings incomplete.");
            var cargo = !settings.PlayerCargoDelivery!.Value;
            var autoRefine = !settings.StationAutoRefine!.Value;
            var autoSell = !settings.StoredAutoSellPreference!.Value;
            Require(commands.Execute(CraftingCommandRequest.Configure(Id, Guid.NewGuid(), station.SessionId, CraftingSetting.StationAutoRefine, autoRefine, station)).Status == CraftingCommandStatus.Succeeded,
                "Could not configure copied station setting.");
            Require(commands.Execute(CraftingCommandRequest.Configure(Id, Guid.NewGuid(), station.SessionId, CraftingSetting.PlayerAutoSell, autoSell)).Status == CraftingCommandStatus.Succeeded,
                "Could not configure copied auto-sell setting.");
            Require(commands.Execute(CraftingCommandRequest.Configure(Id, Guid.NewGuid(), station.SessionId, CraftingSetting.PlayerCargoDelivery, cargo)).Status == CraftingCommandStatus.Succeeded,
                "Could not configure copied save setting.");
            Save("qa-crafting-roundtrip", LifecycleEventKind.SaveSucceeded);
            facts.Clear();
            foreach (var frame in LoadReady("qa-crafting-roundtrip")) yield return frame;
            var restoredStation = quotes.CurrentStation ?? throw new InvalidOperationException("Restored station unavailable.");
            Require(restoredStation.SessionId != station.SessionId, "Reload retained runtime session identity.");
            Require(jobs.Read(station).Status == CraftingJobQueryStatus.StaleHandle, "Old job query handle survived reload.");
            Require(CraftingSaveSignature(jobs.Read(restoredStation)).SequenceEqual(signature), "Native saved jobs changed across reload.");
            Require(!facts.Any(fact => fact.Kind == CraftingJobEventKind.Queued), "Reload replayed restored jobs as queue admissions.");
            CheckRestoredCraftingSettings(commands.ReadSettings(restoredStation.SessionId, restoredStation), cargo, autoRefine, autoSell);
            Save("qa-crafting-save-as", LifecycleEventKind.SaveSucceeded);
            foreach (var frame in LoadReady("fixture-b")) yield return frame;
            foreach (var frame in LoadReady("qa-crafting-save-as")) yield return frame;
            restoredStation = quotes.CurrentStation ?? throw new InvalidOperationException("Save-as station unavailable.");
            Require(CraftingSaveSignature(jobs.Read(restoredStation)).SequenceEqual(signature), "Slot switch/save-as changed native jobs.");
            CheckRestoredCraftingSettings(commands.ReadSettings(restoredStation.SessionId, restoredStation), cargo, autoRefine, autoSell);
            Require(!facts.Any(fact => fact.Kind == CraftingJobEventKind.Queued), "Slot switch/save-as replayed restored jobs as queue admissions.");
            Passed("Paused native job save/reload/save-as/slot-switch with stale handles and no queue replay");
        }
        finally { _pauseCraftingProgress = false; harmony.UnpatchSelf(); }
    }
    private static void CheckRestoredCraftingSettings(CraftingSettingsSnapshot settings, bool cargo, bool autoRefine, bool autoSell) =>
        Require(settings.Available && settings.PlayerCargoDelivery == cargo && settings.StationAutoRefine == autoRefine
            && settings.StoredAutoSellPreference == autoSell && settings.EffectiveAutoSell == autoSell, "Native crafting settings did not survive save restoration.");
    private static string[] CraftingSaveSignature(CraftingJobListSnapshot snapshot)
    {
        Require(snapshot.Status == CraftingJobQueryStatus.Available, "Job persistence query unavailable.");
        return snapshot.Jobs.Select(job => job.Recipe.ProviderId + ":" + job.Recipe.LocalId + "|" + job.Process + "|" + job.InitialBatches + "|" + job.RemainingBatches + "|" + job.CraftedLevel)
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }
}
