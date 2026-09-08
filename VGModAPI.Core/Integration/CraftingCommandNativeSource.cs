using System;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class RecipeCatalogNativeSource : ICraftingCommandBackend
{
    internal CraftingJobObserver? CommandObserver { get; set; }
    internal Action<Exception>? CommandReport { get; set; }
    public CraftingCommandResult Execute(CraftingCommandRequest request)
    {
        var player = NativePlayer;
        if (!CommandLive(request.SessionId, player)) return CommandResult(request, CraftingCommandStatus.SessionUnavailable, "Gameplay player unavailable.");
        if (CommandObserver?.IsHealthy != true || CommandJobEvents == null)
            return CommandResult(request, CraftingCommandStatus.IntegrationUnavailable, "Job/transfer observation is unavailable.");
        if (request.Kind == CraftingCommandKind.SetSetting) return ConfigureNative(request, player!);
        var station = request.Station == null ? null : ResolveStation(request.Station);
        if (station == null) return CommandResult(request, CraftingCommandStatus.StaleHandle, "Station handle was not issued here or has been replaced.");
        return request.Kind switch
        {
            CraftingCommandKind.Queue => QueueNative(request, player!, station),
            CraftingCommandKind.Cancel => CancelNative(request, player!, station),
            CraftingCommandKind.Extract => ExtractNative(request, player!, station),
            _ => CommandResult(request, CraftingCommandStatus.InvalidRequest, "Unsupported command kind.")
        };
    }
    private CraftingCommandResult ExtractNative(CraftingCommandRequest request, object player, object station)
    {
        var resource = request.Material!;
        if (resource.ProviderId != "vanilla") return CommandResult(request, CraftingCommandStatus.Unsupported, "Unsupported refined material provider.");
        var type = _assembly.GetType("Source.Item.RefinedMaterial", true)!;
        if (!Enum.TryParse(type, resource.LocalId, out var material) || material == null || !Enum.IsDefined(type, material))
            return CommandResult(request, CraftingCommandStatus.Unsupported, "Unknown refined material.");
        var refinery = Get(station, "refinery");
        if (refinery == null || !Convert.ToBoolean(Get(refinery, "cargoAccessible")))
            return CommandResult(request, CraftingCommandStatus.StorageUnavailable, "Extraction requires the owning station interior and accessible cargo.");
        var ship = Get(player, "currentSpaceShip"); var cargo = ship == null ? null : Get(ship, "cargo");
        var canister = FindItem("Canister" + material);
        if (cargo == null || canister == null) return CommandResult(request, CraftingCommandStatus.StorageUnavailable, "Canister or cargo unavailable.");
        if (Convert.ToBoolean(Call(canister, "CanGoInDataInventory")) || Get(canister, "itemBuilder") != null || Get(canister, "equipmentBuilder") != null)
            return CommandResult(request, CraftingCommandStatus.Unsupported, "Canister does not have supported cargo item semantics.");
        var volume = Convert.ToDouble(Get(canister, "m3")) * request.Count;
        if (!Finite(volume) || volume < 0 || volume > float.MaxValue || Convert.ToBoolean(Call(cargo, "IsFull", (float)volume)))
            return CommandResult(request, CraftingCommandStatus.StorageUnavailable, "Cargo cannot hold the requested canisters.");
        var id = new RecipeId("vanilla", "extraction/" + resource.LocalId);
        var quote = Quote(request.Station!, id, request.Count, RefineryInputPolicy.Manual, 1);
        var refusal = QueueRefusal(quote);
        if (refusal.HasValue) return CommandResult(request, refusal.Value, "Fresh extraction requirements refuse admission.");
        var balance = Convert.ToSingle(Call(player, "CountRefinedMaterial", material));
        var credits = Convert.ToInt64(Get(player, "credits"));
        if (!Finite(balance) || balance < request.Count || (double)balance > int.MaxValue)
            return CommandResult(request, CraftingCommandStatus.Unsupported, "Material balance is outside supported native admission arithmetic.");
        if (!CommandLive(request.SessionId, player) || !ReferenceEquals(ResolveStation(request.Station!), station))
            return CommandResult(request, CraftingCommandStatus.StaleHandle, "Extraction context changed before invocation.");
        var scope = CommandObserver!.BeginExtraction(request.Station!, refinery);
        if (scope == null) return CommandResult(request, CraftingCommandStatus.IntegrationUnavailable, "Transfer observer cannot capture extraction.");
        Exception? failure = null;
        try { Call(refinery, "ExtractMaterial", material, request.Count); }
        catch (Exception error) { failure = error; try { CommandReport?.Invoke(error); } catch { } }
        finally { CommandObserver.End(scope, null, failure); }
        if (!CommandLive(request.SessionId, player)) return CommandResult(request, CraftingCommandStatus.Uncertain, "Session changed during extraction; do not retry.", true);
        if (failure == null) RefreshCommandUi?.Invoke(station);
        if (!CommandLive(request.SessionId, player)) return CommandResult(request, CraftingCommandStatus.Uncertain, "Session changed during extraction refresh.", true);
        var delta = checked(Convert.ToInt64(Get(player, "credits")) - credits);
        var remaining = Convert.ToSingle(Call(player, "CountRefinedMaterial", material));
        var canisterId = Resource(Text(canister, "identifier"), RecipeResourceKind.Item);
        var deliveries = scope.Deliveries.ToArray();
        var success = failure == null && ReferenceEquals(Get(player, "currentSpaceShip"), ship) && ReferenceEquals(Get(ship!, "cargo"), cargo) && CommandObserver.IsHealthy && !scope.Unresolved && remaining == balance - request.Count && delta == -quote.CreditsRequired!.Value &&
            deliveries.Length > 0 && deliveries.All(item => item.Status == CraftingDeliveryStatus.Verified && canisterId.Equals(item.Resource) && item.Destination == RecipeInventoryKind.ShipCargo) &&
            deliveries.Sum(item => item.VerifiedAmount ?? double.NaN) == request.Count;
        return new CraftingCommandResult(request.RequestId, success ? CraftingCommandStatus.Succeeded : CraftingCommandStatus.Uncertain,
            success ? "Immediate material debit, fee and cargo delivery verified; no refinery job was queued." : "Extraction has partial or unverified effects; do not retry blindly.",
            true, creditDelta: delta, deliveries: deliveries);
    }
}
