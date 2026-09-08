using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class RecipeCatalogNativeSource
{
    private CraftingCommandResult QueueNative(CraftingCommandRequest request, object player, object station)
    {
        var recipeId = request.Recipe!;
        if (recipeId.ProviderId != "vanilla") return CommandResult(request, CraftingCommandStatus.Unsupported, "Recipe provider is not supported by this native adapter.");
        object? definition; object? parent;
        var forge = recipeId.LocalId.StartsWith("forge/", StringComparison.Ordinal);
        if (forge)
        {
            parent = Get(station, "forge");
            if (parent == null) return CommandResult(request, CraftingCommandStatus.Unsupported, "Station has no Forge.");
            var matches = Enumerate(Get(parent, "recipes")).Where(recipe => "forge/" + Text(recipe, "identifier") == recipeId.LocalId).Distinct().ToArray();
            if (matches.Length != 1) return CommandResult(request, CraftingCommandStatus.Rejected, "Recipe is locked, absent or ambiguous at this station.");
            definition = matches[0];
            if (ReadForge(definition, RecipeAvailability.Available).Availability != RecipeAvailability.Available)
                return CommandResult(request, CraftingCommandStatus.Unsupported, "Recipe contains unsupported definitions.");
        }
        else if (recipeId.LocalId.StartsWith("refining/", StringComparison.Ordinal))
        {
            parent = Get(station, "refinery"); var item = FindItem(recipeId.LocalId.Substring("refining/".Length));
            definition = item == null ? null : Component(Get(item, "gameObject")!, "Behaviour.Mining.OreItemData");
            if (parent == null || definition == null) return CommandResult(request, CraftingCommandStatus.Unsupported, "Refinery or ore definition unavailable.");
        }
        else return CommandResult(request, CraftingCommandStatus.Unsupported, "This process is not a queued production job.");
        var quote = Quote(request.Station!, recipeId, request.Count, RefineryInputPolicy.Manual, 1);
        if (quote.Status != RecipeQuoteStatus.Available) return CommandResult(request, CraftingCommandStatus.Rejected, quote.Detail);
        if (quote.Blockers.Contains(RecipeBlocker.QueueFull)) return CommandResult(request, CraftingCommandStatus.QueueFull, "Queue is full.");
        if (quote.Blockers.Contains(RecipeBlocker.MissingIngredients)) return CommandResult(request, CraftingCommandStatus.MissingInputs, "Inputs are missing.");
        if (quote.Blockers.Contains(RecipeBlocker.InventoryUnavailable)) return CommandResult(request, CraftingCommandStatus.StorageUnavailable, "Input inventory is unavailable.");
        var prepared = false;
        if (!quote.CreditsRequired.HasValue)
        {
            // An explicit action may perform native price initialization; observational quote reads never do so.
            prepared = true; _ = Get(definition, forge ? "craftingCost" : "refinementCost");
        }
        if (!CommandLive(request.SessionId, player) || !ReferenceEquals(ResolveStation(request.Station!), station))
            return CommandResult(request, CraftingCommandStatus.StaleHandle, "Context changed during price preparation.", prepared);
        quote = Quote(request.Station!, recipeId, request.Count, RefineryInputPolicy.Manual, 2);
        var refusal = QueueRefusal(quote);
        if (refusal.HasValue) return CommandResult(request, refusal.Value, "Fresh requirements refuse queue admission.", prepared);
        if (!ProtectionAllows(request, station, player, definition, forge))
            return CommandResult(request, CraftingCommandStatus.ProtectedInputs, "Native consumption would use protected items or lacks enough permitted stock.", prepared);
        var before = new HashSet<object>(NativeJobs(parent!), NativeObjectIdentity.Instance);
        if (before.Count >= Convert.ToInt32(Get(parent!, "maxJobs"))) return CommandResult(request, CraftingCommandStatus.QueueFull, "Queue filled before admission.", prepared);
        var credits = Convert.ToInt64(Get(player, "credits"));
        var expectedInputs = quote.Inputs.ToDictionary(input => input.Resource, input => input.Available!.Value - input.Required);
        if (forge)
        {
            foreach (var input in quote.Inputs.Where(input => input.Resource.Kind == RecipeResourceKind.RefinedMaterial))
                expectedInputs[input.Resource] = input.Available!.Value;
            // Vanilla subtracts each float row separately; aggregating first changes rounding.
            foreach (var row in Enumerate(Call(definition, "GetIngredientMaterials", 0)))
            {
                var id = Resource(Get(row, "Item1")!.ToString()!, RecipeResourceKind.RefinedMaterial);
                expectedInputs[id] = (float)((float)expectedInputs[id] - Convert.ToSingle(Get(row, "Item2")) * request.Count);
            }
        }
        if (!CommandLive(request.SessionId, player) || !ReferenceEquals(ResolveStation(request.Station!), station))
            return CommandResult(request, CraftingCommandStatus.StaleHandle, "Context changed before queue admission.", prepared);
        var admitted = forge || request.Protection == CraftingProtectionPolicy.NativeConsumption
            ? Convert.ToBoolean(Call(parent!, "TryStartJob", definition, request.Count))
            : Convert.ToBoolean(Call(parent!, "StartJob", definition, request.Count, true));
        if (!CommandLive(request.SessionId, player) || !ReferenceEquals(ResolveStation(request.Station!), station))
            return CommandResult(request, CraftingCommandStatus.Uncertain, "Context changed after queue invocation; do not retry.", true);
        RefreshCommandUi?.Invoke(station);
        if (!CommandLive(request.SessionId, player)) return CommandResult(request, CraftingCommandStatus.Uncertain, "Session changed during UI refresh.", true);
        var added = NativeJobs(parent!).Where(job => !before.Contains(job)).ToArray();
        var debit = checked(Convert.ToInt64(Get(player, "credits")) - credits);
        var after = Quote(request.Station!, recipeId, request.Count, RefineryInputPolicy.Manual, 3);
        var inputsMatch = after.Status == RecipeQuoteStatus.Available && expectedInputs.All(pair => after.Inputs.SingleOrDefault(input => input.Resource.Equals(pair.Key))?.Available == pair.Value);
        var exactJob = added.Length == 1 && ReferenceEquals(Get(added[0], forge ? "recipe" : "ore"), definition) &&
            Convert.ToInt32(Get(added[0], "initialAmount")) == request.Count;
        var success = CommandObserver?.IsHealthy == true && admitted && exactJob && inputsMatch && debit == -quote.CreditsRequired!.Value;
        var handles = added.Select(job => SnapshotJob(request.Station!, parent!, job, forge ? RecipeProcess.Forge : RecipeProcess.Refining).Handle).ToArray();
        var unchanged = added.Length == 0 && debit == 0 && InputsUnchanged(quote, after);
        return new CraftingCommandResult(request.RequestId, success ? CraftingCommandStatus.Succeeded : unchanged && !admitted ? CraftingCommandStatus.Rejected : CraftingCommandStatus.Uncertain,
            success ? "Queue entry, credits and accessible input debits verified." : "Native admission did not establish the requested economic outcome; never retry an uncertain result blindly.",
            true, creditDelta: debit, jobs: handles);
    }
    private static CraftingCommandStatus? QueueRefusal(RecipeQuote quote)
    {
        if (quote.Status != RecipeQuoteStatus.Available || !quote.CreditsRequired.HasValue) return CraftingCommandStatus.Unsupported;
        if (quote.Blockers.Contains(RecipeBlocker.QueueFull)) return CraftingCommandStatus.QueueFull;
        if (quote.Blockers.Contains(RecipeBlocker.InsufficientCredits)) return CraftingCommandStatus.InsufficientCredits;
        if (quote.Blockers.Contains(RecipeBlocker.InventoryUnavailable)) return CraftingCommandStatus.StorageUnavailable;
        if (quote.Blockers.Contains(RecipeBlocker.MissingIngredients)) return CraftingCommandStatus.MissingInputs;
        return null; // Conditional future outputs are not admission requirements.
    }
    private bool ProtectionAllows(CraftingCommandRequest request, object station, object player, object definition, bool forge)
    {
        if (request.Protection == CraftingProtectionPolicy.NativeConsumption) return true;
        var items = forge ? Enumerate(Call(definition, "GetIngredientItems", 0)).Select(row =>
            (Item: Get(row, "Item1")!, Count: checked(Convert.ToInt32(Get(row, "Item2")) * request.Count))).ToArray() :
            new[] { (Item: Get(definition, "item")!, Count: request.Count) };
        var inventories = new List<object>();
        var ship = Get(player, "currentSpaceShip");
        if (!forge && Convert.ToBoolean(Get(Get(station, "refinery")!, "cargoAccessible")) && ship != null) inventories.Add(Get(ship, "cargo")!);
        inventories.Add(Get(station, "materialStorage")!);
        if (forge)
        {
            inventories.Add(Get(player, "globalInventory")!); inventories.Add(Get(player, "dataInventory")!);
            if (IsLocal(station) && ship != null) inventories.Add(Get(ship, "cargo")!);
        }
        if (inventories.Any(inventory => inventory == null) || inventories.Distinct(NativeObjectIdentity.Instance).Count() != inventories.Count) return false;
        foreach (var group in items.GroupBy(item => item.Item, NativeObjectIdentity.Instance))
        {
            var needed = checked(group.Sum(item => item.Count));
            if (request.Protection == CraftingProtectionPolicy.ProtectFavouritesAndMissionItems && Convert.ToInt32(Call(player, "RequiredItemCountForMissions", group.Key)) > 0) return false;
            foreach (var inventory in inventories)
            {
                foreach (var row in Enumerate(Get(inventory, "items")))
                {
                    if (needed == 0) break;
                    var item = Get(row, "item"); if (!ReferenceEquals(item, group.Key)) continue;
                    var count = Convert.ToInt32(Get(row, "count")); if (count <= 0) continue;
                    if (Convert.ToBoolean(Get(row, "favourite"))) { if (forge) return false; continue; }
                    needed -= Math.Min(needed, count);
                }
            }
            if (needed != 0) return false;
        }
        return true;
    }
    private static bool InputsUnchanged(RecipeQuote before, RecipeQuote after) => after.Status == RecipeQuoteStatus.Available && before.Inputs.All(input =>
        input.Available.HasValue && after.Inputs.SingleOrDefault(item => item.Resource.Equals(input.Resource))?.Available == input.Available);
}
