using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class RecipeCatalogNativeSource
{
    internal ICraftingJobs? CommandJobEvents { get; set; }
    private CraftingCommandResult CancelNative(CraftingCommandRequest request, object player, object station)
    {
        if (CommandJobEvents == null || !_jobObjects.TryGetValue(request.Job!, out var job))
            return CommandResult(request, CraftingCommandStatus.StaleHandle, "Job handle is unavailable.");
        var parent = Get(job, "parent");
        if (parent == null || !NativeJobs(parent).Any(candidate => ReferenceEquals(candidate, job)))
            return CommandResult(request, CraftingCommandStatus.StaleHandle, "Job no longer belongs to its queue.");
        var forge = ReferenceEquals(Get(station, "forge"), parent);
        if (!forge && !ReferenceEquals(Get(station, "refinery"), parent)) return CommandResult(request, CraftingCommandStatus.StaleHandle, "Job owning instance was replaced.");
        var before = SnapshotJob(request.Station!, parent, job, forge ? RecipeProcess.Forge : RecipeProcess.Refining, request.Job);
        var definition = Get(job, forge ? "recipe" : "ore")!;
        var currentCost = Convert.ToInt64(Get(definition, forge ? "craftingCost" : "refinementCost"));
        if (!CommandLive(request.SessionId, player) || !ReferenceEquals(ResolveStation(request.Station!), station) || !NativeJobs(parent).Contains(job))
            return CommandResult(request, CraftingCommandStatus.StaleHandle, "Cancellation context changed during price preparation.", true);
        before = SnapshotJob(request.Station!, parent, job, forge ? RecipeProcess.Forge : RecipeProcess.Refining, request.Job);
        if (currentCost < 0) return CommandResult(request, CraftingCommandStatus.Unsupported, "Invalid native refund cost.", true);
        var refund = checked(currentCost * before.RemainingBatches);
        var credits = Convert.ToInt64(Get(player, "credits"));
        if (credits > long.MaxValue - refund) return CommandResult(request, CraftingCommandStatus.InvalidRequest, "Credit refund would overflow.", true);
        var expected = new Dictionary<RecipeResourceId, double>();
        if (forge)
        {
            var balances = new Dictionary<RecipeResourceId, (double Before, float After)>();
            foreach (var row in Enumerate(Call(definition, "GetIngredientMaterials", before.CraftedLevel!.Value)))
            {
                var material = Get(row, "Item1")!; var id = Resource(material.ToString()!, RecipeResourceKind.RefinedMaterial);
                var addition = Convert.ToSingle(Get(row, "Item2")) * before.RemainingBatches;
                if (!Finite(addition) || addition < 0) return CommandResult(request, CraftingCommandStatus.Unsupported, "Invalid material refund.", true);
                if (!balances.TryGetValue(id, out var balance))
                {
                    var value = Convert.ToSingle(Call(player, "CountRefinedMaterial", material)); balance = (value, value);
                }
                var after = balance.After + addition;
                if (!Finite(after)) return CommandResult(request, CraftingCommandStatus.Unsupported, "Material refund would overflow.", true);
                balances[id] = (balance.Before, after);
            }
            foreach (var pair in balances) expected.Add(pair.Key, pair.Value.After - pair.Value.Before);
            foreach (var row in Enumerate(Call(definition, "GetIngredientItems", before.CraftedLevel.Value)))
                AddExpectedItem(expected, Get(row, "Item1")!, checked(Convert.ToInt32(Get(row, "Item2")) * before.RemainingBatches));
        }
        else AddExpectedItem(expected, Get(definition, "item")!, before.RemainingBatches);
        var captured = new List<CraftingJobEvent>();
        using var listener = CommandJobEvents.Subscribe("vgmodapi.crafting-command-receipt", fact =>
        { if (fact.Job.Handle.Equals(request.Job)) captured.Add(fact); });
        if (!CommandLive(request.SessionId, player) || !ReferenceEquals(ResolveStation(request.Station!), station))
            return CommandResult(request, CraftingCommandStatus.StaleHandle, "Cancellation context changed before invocation.", true);
        Call(parent, "CancelJob", job); // Owning parent, never the native job helper's static current station.
        if (!CommandLive(request.SessionId, player)) return CommandResult(request, CraftingCommandStatus.Uncertain, "Session changed during cancellation.", true);
        RefreshCommandUi?.Invoke(station);
        if (!CommandLive(request.SessionId, player)) return CommandResult(request, CraftingCommandStatus.Uncertain, "Session changed during cancellation refresh.", true);
        var delta = checked(Convert.ToInt64(Get(player, "credits")) - credits);
        var cancelled = captured.Where(fact => fact.Kind == CraftingJobEventKind.Cancelled).ToArray();
        var deliveries = cancelled.SelectMany(fact => fact.Deliveries).ToArray();
        var success = CommandObserver?.IsHealthy == true && cancelled.Length == 1 && !NativeJobs(parent).Contains(job) && delta == refund &&
            deliveries.All(item => item.Status == CraftingDeliveryStatus.Verified && item.Resource != null && expected.ContainsKey(item.Resource)) &&
            expected.All(pair => deliveries.Where(item => pair.Key.Equals(item.Resource)).Sum(item => item.VerifiedAmount ?? double.NaN) == pair.Value);
        return new CraftingCommandResult(request.RequestId, success ? CraftingCommandStatus.Succeeded : CraftingCommandStatus.Uncertain,
            success ? "Removal, captured-level input refunds and current-cost credit refund verified." : "Cancellation effects are partial or unverified; do not retry blindly.",
            true, creditDelta: delta, deliveries: deliveries);
    }
    private static void AddExpectedItem(Dictionary<RecipeResourceId, double> expected, object item, int amount)
    {
        if (amount < 0) throw new InvalidOperationException("Invalid item refund.");
        var id = Resource(Text(item, "identifier"), RecipeResourceKind.Item);
        expected.TryGetValue(id, out var prior); expected[id] = prior + amount;
    }
}
