using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private void CheckRefineryFavouriteProtection(RecipeStationHandle station, RecipeId recipe, object item)
    {
        Require(ModApi.Services.RecipeQuotes!.Quote(station, recipe, 1).RequirementsMet, "Favourite fixture requires affordable remaining ore.");
        var nativeStation = SpGet(CurrentPlayer, "currentPointOfInterest")!;
        var jobs = (IList)SpGet(SpGet(nativeStation, "refinery")!, "jobs")!;
        var priorJobs = jobs.Cast<object>().ToArray();
        var inventories = new[] { SpGet(nativeStation, "materialStorage")!, SpGet(SpGet(CurrentPlayer, "currentSpaceShip")!, "cargo")! };
        var rows = inventories.Distinct().SelectMany(inventory => ((IEnumerable)SpGet(inventory, "items")!).Cast<object>())
            .Where(row => ReferenceEquals(SpGet(row, "item"), item) && Convert.ToInt32(SpGet(row, "count")) > 0)
            .Select(row => (Row: row, Favourite: (bool)SpGet(row, "favourite")!)).ToArray();
        Require(rows.Length > 0, "Favourite fixture has no exact ore rows.");
        var before = ForgeInventoryCounts(nativeStation);
        var credits = (long)SpGet(CurrentPlayer, "credits")!;
        var facts = new List<CraftingJobEvent>();
        using var observer = new CraftingJobProbeSubscription(ModApi.Services.CraftingJobs, facts.Add);
        try
        {
            foreach (var row in rows) AccessTools.Field(row.Row.GetType(), "favourite").SetValue(row.Row, true);
            foreach (var policy in new[] { CraftingProtectionPolicy.ProtectFavourites, CraftingProtectionPolicy.ProtectFavouritesAndMissionItems })
            {
                var result = ModApi.Services.CraftingCommands!.Execute(CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station, recipe, 1, policy));
                Require(result.Status == CraftingCommandStatus.ProtectedInputs, "Favourite-only ore was not refused by a protecting policy.");
            }
            var after = ForgeInventoryCounts(nativeStation);
            Require((long)SpGet(CurrentPlayer, "credits")! == credits && jobs.Cast<object>().SequenceEqual(priorJobs)
                && before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var amount) && amount == pair.Value)
                && facts.All(fact => fact.Kind != CraftingJobEventKind.Queued), "Protected refusal mutated inputs, jobs, credits or emitted admission.");
        }
        finally
        {
            foreach (var row in rows) AccessTools.Field(row.Row.GetType(), "favourite").SetValue(row.Row, row.Favourite);
        }
        Require(rows.All(row => (bool)SpGet(row.Row, "favourite")! == row.Favourite), "Copied favourite flags were not restored.");
        Passed("Refinery favourite-only inputs refused by protecting policies without mutation; copied flags restored");
    }
}
