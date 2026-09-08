using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class CraftingJobObserver
{
    private Scope? BeginTransfer(string key, object instance, object[] args, Guid session)
    {
        var owner = _scopes.LastOrDefault(scope => scope.Transfer == null);
        if (owner == null) return null;
        if (owner.CallbackContext != _service.CallbackContext)
        {
            // Subscriber work is not an output of a native operation enclosing dispatch.
            // If it touches an in-flight addition, its enclosing quantity delta is no longer attributable.
            foreach (var active in _scopes)
                if (active.Transfer != null && ReferenceEquals(active.Instance, instance)) active.Transfer.NestedUnknown = true;
            return null;
        }
        var material = key == "jobMaterialAdd";
        if (material ? !(owner.Key == "jobBatchRefinery" || owner.Key.StartsWith("jobCancel", StringComparison.Ordinal)) :
            !(owner.Key.StartsWith("jobRoute", StringComparison.Ordinal) || owner.Key.StartsWith("jobCancel", StringComparison.Ordinal) || owner.Key == "commandExtract")) return null;
        var state = new TransferState(owner, args[0], Convert.ToDouble(args[1]));
        var scope = new Scope(key, instance, args, _epoch, session, _source.NativePlayer)
        { Transfer = state, Station = owner.Station, Parent = owner.Parent, Job = owner.Job, Process = owner.Process };
        if (material)
        {
            if (!ReferenceEquals(instance, scope.Player)) { owner.Unresolved = true; return null; }
            state.BeforeAmount = MaterialAmount(instance, args[0]); state.Destination = RecipeInventoryKind.PlayerRefinedMaterials;
        }
        else
        {
            state.Destination = Destination(owner, instance);
            if (!state.Destination.HasValue) { owner.Unresolved = true; return null; }
            foreach (var row in Rows(instance)) state.BeforeRows.Add(row, Convert.ToDouble(Value(row, "count")));
        }
        Push(scope); return scope;
    }
    private void EndTransfer(Scope scope, object? result, Exception? error)
    {
        var state = scope.Transfer!;
        var material = scope.Key == "jobMaterialAdd";
        var verified = error == null && !state.NestedUnknown;
        double total = 0, own = 0;
        if (material)
        {
            total = MaterialAmount(scope.Instance, state.Resource) - state.BeforeAmount;
            own = total - state.NestedAmount;
            verified &= RecipeCatalogNativeSource.Finite(own) && own >= 0 && ReferenceEquals(scope.Player, scope.Instance);
        }
        else
        {
            verified &= result != null && ReferenceEquals(Value(result, "inventory"), scope.Instance) && Rows(scope.Instance).Any(row => ReferenceEquals(row, result));
            if (verified)
            {
                state.BeforeRows.TryGetValue(result!, out var before); state.NestedRows.TryGetValue(result!, out var nested);
                total = Convert.ToDouble(Value(result!, "count")) - before; own = total - nested;
                // Nonstandard stack effects are not equivalent to a count receipt.
                var storedItem = Value(result!, "item");
                verified &= own >= 0 && own == state.Requested && storedItem != null &&
                    Equals(Value(storedItem, "identifier"), Value(state.Resource, "identifier")) &&
                    Equals(Value(storedItem, "itemLevel"), Value(state.Resource, "itemLevel")) &&
                    Equals(Value(storedItem, "rarity"), Value(state.Resource, "rarity"));
            }
        }
        var ancestor = _scopes.LastOrDefault(item => item.Transfer != null && item.Key == scope.Key && ReferenceEquals(item.Instance, scope.Instance) &&
            (!material || Equals(item.Transfer.Resource, state.Resource)));
        if (ancestor != null)
        {
            if (!verified) ancestor.Transfer!.NestedUnknown = true;
            else if (material) ancestor.Transfer!.NestedAmount += total;
            else
            {
                ancestor.Transfer!.NestedRows.TryGetValue(result!, out var prior);
                ancestor.Transfer.NestedRows[result!] = prior + total;
            }
        }
        RecipeResourceId? resource = null;
        int? level = null; string? rarity = null;
        if (material)
        {
            if (state.Resource.GetType().IsEnum && Enum.IsDefined(state.Resource.GetType(), state.Resource))
                resource = new RecipeResourceId("vanilla", state.Resource.ToString()!, RecipeResourceKind.RefinedMaterial);
        }
        else
        {
            var identifier = Value(state.Resource, "identifier") as string;
            if (!string.IsNullOrWhiteSpace(identifier) && identifier!.Length <= 500 && !identifier.Any(char.IsControl))
                resource = new RecipeResourceId("vanilla", identifier, RecipeResourceKind.Item);
            level = Convert.ToInt32(Value(state.Resource, "itemLevel")); rarity = Value(state.Resource, "rarity")?.ToString();
        }
        verified &= resource != null;
        if (state.Owner.Deliveries.Count >= 1024) throw new RecipeCatalogLimitException();
        state.Owner.Deliveries.Add(new CraftingDeliverySnapshot(resource, state.Requested, verified ? own : null, state.Destination,
            verified ? CraftingDeliveryStatus.Verified : CraftingDeliveryStatus.Unresolved,
            verified ? "Observed destination quantity delta, excluding nested transfers." : "Transfer effect could not be attributed as a quantity receipt.", level, rarity));
        // A positive, exact float-rounded addition is a fulfilled native material transfer.
        // Keep zero/rounded-away additions and ambiguous nested rounding unresolved at batch level.
        var materialFulfilled = !material || own > 0 && (own == state.Requested || state.NestedAmount == 0 &&
            own == (double)(float)((float)state.BeforeAmount + (float)state.Requested) - state.BeforeAmount);
        state.Owner.Unresolved |= !verified || !materialFulfilled;
    }
    private RecipeInventoryKind? Destination(Scope owner, object inventory)
    {
        var player = owner.Player; if (player == null) return null;
        var station = _source.ResolveStation(owner.Station!); if (station == null) return null;
        if (ReferenceEquals(Value(station, "materialStorage"), inventory)) return RecipeInventoryKind.StationMaterials;
        if (ReferenceEquals(Value(player, "globalInventory"), inventory)) return RecipeInventoryKind.PlayerArmory;
        if (ReferenceEquals(Value(player, "dataInventory"), inventory)) return RecipeInventoryKind.PlayerData;
        var ship = Value(player, "currentSpaceShip");
        if (ship != null && ReferenceEquals(Value(ship, "cargo"), inventory)) return RecipeInventoryKind.ShipCargo;
        return null;
    }
    private static double MaterialAmount(object player, object material) => Convert.ToDouble(RecipeCatalogNativeSource.InvokeMember(player, "CountRefinedMaterial", material));
    private static IEnumerable<object> Rows(object inventory)
    {
        if (Value(inventory, "items") is not IEnumerable rows) throw new InvalidOperationException("Inventory rows unavailable.");
        var count = 0;
        foreach (var row in rows)
        {
            if (++count > 65536) throw new RecipeCatalogLimitException();
            if (row == null) throw new InvalidOperationException("Null inventory row.");
            yield return row;
        }
    }
    internal sealed class TransferState
    {
        internal readonly Scope Owner;
        internal readonly object Resource;
        internal readonly double Requested;
        internal double BeforeAmount, NestedAmount;
        internal bool NestedUnknown;
        internal RecipeInventoryKind? Destination;
        internal readonly Dictionary<object, double> BeforeRows = new(NativeObjectIdentity.Instance), NestedRows = new(NativeObjectIdentity.Instance);
        internal TransferState(Scope owner, object resource, double requested) { Owner = owner; Resource = resource; Requested = requested; }
    }
}
