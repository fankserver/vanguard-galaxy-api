using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class RecipeCatalogNativeSource
{
    private Guid _quoteSession;
    private readonly Dictionary<Guid, object> _quoteStations = new();
    internal void BindQuotes() => RecipeQuoteBindings.Validate(_assembly);
    public void Invalidate() { _quoteStations.Clear(); _quoteSession = Guid.Empty; InvalidateJobs(); }
    public RecipeStationHandle? CurrentStation(Guid sessionId)
    {
        var station = GetStatic("Source.Galaxy.POI.SpaceStation", "current");
        return station == null ? null : IssueStation(sessionId, station);
    }
    internal RecipeStationHandle? IssueStation(Guid sessionId, object station)
    {
        if (_quoteSession != sessionId) { Invalidate(); _quoteSession = sessionId; }
        if (Get(station, "forge") == null && Get(station, "refinery") == null || !StationStillPresent(station)) return null;
        var key = _quoteStations.FirstOrDefault(pair => ReferenceEquals(pair.Value, station)).Key;
        if (key == Guid.Empty)
        {
            if (_quoteStations.Count >= 256) throw new RecipeCatalogLimitException();
            key = Guid.NewGuid(); _quoteStations.Add(key, station);
        }
        return new RecipeStationHandle(sessionId, key, Text(station, "name"));
    }
    public RecipeQuote Quote(RecipeStationHandle handle, RecipeId id, int batches, RefineryInputPolicy policy, long revision)
    {
        if (handle.SessionId != _quoteSession || !_quoteStations.TryGetValue(handle.InstanceId, out var station))
            return RecipeQuoteService.Failure(RecipeQuoteStatus.StaleHandle, handle, id, batches, "Station handle was not issued in this session.");
        if (!StationStillPresent(station)) return RecipeQuoteService.Failure(RecipeQuoteStatus.StaleHandle, handle, id, batches, "Station was removed or replaced.");
        var player = GetStatic("Source.Player.GamePlayer", "current");
        if (player == null) return RecipeQuoteService.Failure(RecipeQuoteStatus.SessionUnavailable, handle, id, batches, "Player unavailable.");
        if (id.ProviderId != "vanilla") return RecipeQuoteService.Failure(RecipeQuoteStatus.RecipeUnavailable, handle, id, batches, "Unsupported recipe provider.");
        if (id.LocalId.StartsWith("forge/", StringComparison.Ordinal)) return QuoteForge(station, player, handle, id, batches, revision);
        if (id.LocalId.StartsWith("refining/", StringComparison.Ordinal)) return QuoteRefining(station, player, handle, id, batches, policy, revision);
        if (id.LocalId.StartsWith("extraction/", StringComparison.Ordinal)) return QuoteExtraction(station, player, handle, id, batches, revision);
        return RecipeQuoteService.Failure(RecipeQuoteStatus.RecipeUnavailable, handle, id, batches, "Recipe not found.");
    }
    private RecipeQuote QuoteForge(object station, object player, RecipeStationHandle handle, RecipeId id, int batches, long revision)
    {
        var forge = Get(station, "forge");
        if (forge == null) return RecipeQuoteService.Failure(RecipeQuoteStatus.StationUnavailable, handle, id, batches, "Station has no Forge.");
        var matches = Enumerate(Get(forge, "recipes")).Where(recipe => "forge/" + Text(recipe, "identifier") == id.LocalId).Distinct().ToArray();
        if (matches.Length != 1) return RecipeQuoteService.Failure(RecipeQuoteStatus.RecipeUnavailable, handle, id, batches, "Recipe unavailable or ambiguous at this station.");
        var recipe = matches[0]; var definition = ReadForge(recipe, RecipeAvailability.Available);
        if (definition.Availability != RecipeAvailability.Available) return RecipeQuoteService.Failure(RecipeQuoteStatus.Unsupported, handle, id, batches, definition.Detail);
        var level = Convert.ToInt32(Call(recipe, "GetAdjustedOutputLevel"));
        var requirements = new List<RecipeIngredientRequirement>();
        foreach (var row in Enumerate(Call(recipe, "GetIngredientMaterials", level)))
        {
            var material = Get(row, "Item1")!;
            var amount = CheckedMaterial(Convert.ToSingle(Get(row, "Item2")), batches);
            requirements.Add(new RecipeIngredientRequirement(Resource(material.ToString()!, RecipeResourceKind.RefinedMaterial), amount,
                new[] { new RecipeInventoryBalance(RecipeInventoryKind.PlayerRefinedMaterials, null, true, Convert.ToDouble(Call(player, "CountRefinedMaterial", material))) }));
        }
        foreach (var row in Enumerate(Call(recipe, "GetIngredientItems", level)))
        {
            var item = Get(row, "Item1") ?? throw new InvalidOperationException("Missing ingredient item.");
            var amount = checked(Convert.ToInt32(Get(row, "Item2")) * batches);
            if (amount <= 0) throw new InvalidOperationException("Invalid scaled item requirement.");
            requirements.Add(new RecipeIngredientRequirement(Resource(Text(item, "identifier"), RecipeResourceKind.Item), amount,
                new[] {
                    ItemBalance(RecipeInventoryKind.StationMaterials, handle, Get(station, "materialStorage"), item),
                    ItemBalance(RecipeInventoryKind.PlayerArmory, null, Get(player, "globalInventory"), item),
                    ItemBalance(RecipeInventoryKind.PlayerData, null, Get(player, "dataInventory"), item),
                    CargoBalance(station, player, item, false) }));
        }
        var outputs = new List<RecipeOutputPreview>();
        var bonus = Skill("industrialForgeBonusCraft", "currentIncrease");
        foreach (var output in definition.Outputs)
        {
            var count = checked((int)output.Amount * batches);
            var destinations = OutputDestinations(station, player, OutputItem(recipe, output.Resource), (int)output.Amount);
            outputs.Add(new RecipeOutputPreview(output.Resource, count, 1, "Destination is resolved at delivery from item category, cargo capacity, station type and player preference.", destinations));
            if (bonus > 0) outputs.Add(new RecipeOutputPreview(output.Resource, count, Math.Min(1, bonus), "Independent bonus roll per completed batch; amount is the maximum across requested batches.", destinations));
        }
        // Both Forge and refinery charge through GamePlayer.RemoveCredits(float).
        long? cost = CanReadForgeCost(recipe) ? checked((long)(float)(checked(Convert.ToInt64(Get(recipe, "craftingCost")) * batches))) : null;
        var seconds = Convert.ToDouble(Get(recipe, "craftingTime")) / Convert.ToDouble(Get(forge, "craftingSpeed"));
        return BuildQuote(handle, id, batches, revision, player, requirements, outputs, cost, seconds,
            Enumerate(Get(forge, "jobs")).Count(), Convert.ToInt32(Get(forge, "maxJobs")), level);
    }
    private RecipeQuote QuoteRefining(object station, object player, RecipeStationHandle handle, RecipeId id, int batches, RefineryInputPolicy policy, long revision)
    {
        var refinery = Get(station, "refinery");
        if (refinery == null) return RecipeQuoteService.Failure(RecipeQuoteStatus.StationUnavailable, handle, id, batches, "Station has no refinery.");
        var item = FindItem(id.LocalId.Substring("refining/".Length));
        var ore = item == null ? null : Component(Get(item, "gameObject")!, "Behaviour.Mining.OreItemData");
        if (ore == null) return RecipeQuoteService.Failure(RecipeQuoteStatus.RecipeUnavailable, handle, id, batches, "Refinable item missing.");
        var automatic = policy == RefineryInputPolicy.AutomaticSelection;
        var reserved = automatic ? Convert.ToInt32(Call(player, "RequiredItemCountForMissions", item!)) : 0;
        var cargoAccess = IsLocal(station) && GetStatic("Behaviour.UI.Spacestation.SpaceStationInterior", "instance") != null;
        var cargo = Get(Get(player, "currentSpaceShip")!, "cargo");
        var balances = new[] {
            RefiningBalance(RecipeInventoryKind.StationMaterials, handle, Get(station, "materialStorage"), item!, reserved, automatic, true),
            RefiningBalance(RecipeInventoryKind.ShipCargo, null, cargo, item!, reserved, automatic, cargoAccess) };
        var requirements = new[] { new RecipeIngredientRequirement(Resource(Text(item!, "identifier"), RecipeResourceKind.Item), batches, balances) };
        var outputs = new List<RecipeOutputPreview>();
        var rewards = !(bool)Get(ore, "ignoreExtraRewards")!;
        var bonus = rewards && Skill("industrialRefBonusCraft1", "isActive") > 0 ? .1 : 0;
        foreach (var row in Enumerate(Get(ore, "contents")))
        {
            var material = Get(row, "product")!; var amount = CheckedMaterial(Convert.ToSingle(Get(row, "yield")), batches);
            outputs.Add(new RecipeOutputPreview(Resource(material.ToString()!, RecipeResourceKind.RefinedMaterial), amount, 1, "Player-wide refined materials.", new[] { RecipeInventoryKind.PlayerRefinedMaterials }));
            if (bonus > 0) outputs.Add(new RecipeOutputPreview(Resource(material.ToString()!, RecipeResourceKind.RefinedMaterial), amount, bonus,
                "Independent double-yield roll per batch.", new[] { RecipeInventoryKind.PlayerRefinedMaterials }));
        }
        var crystalChance = rewards && Get(item!, "itemCategory")!.ToString() == "Ore" ? Skill("industrialCrystalRefineChance", "currentIncrease") : 0;
        if (crystalChance > 0) outputs.Add(new RecipeOutputPreview(null, batches, Math.Min(1, crystalChance), "Random crystal reward; item identity is unknown until completion.",
            MayDepositInCargo(station, player)
                ? new[] { RecipeInventoryKind.ShipCargo, RecipeInventoryKind.PlayerArmory, RecipeInventoryKind.StationMaterials }
                : new[] { RecipeInventoryKind.PlayerArmory, RecipeInventoryKind.StationMaterials }));
        // Vanilla refinery charges through a float amount even though its inputs are integer costs.
        long? cost = HasCachedItemCost(item!) ? checked((long)(float)(checked(Convert.ToInt64(Get(ore, "refinementCost")) * batches))) : null;
        var extra = automatic && (bool)Get(ore, "disableAutoRefine")! ? new[] { RecipeBlocker.AutomaticRefiningDisabled } : Array.Empty<RecipeBlocker>();
        return BuildQuote(handle, id, batches, revision, player, requirements, outputs, cost, Convert.ToDouble(Get(ore, "refinementTime")),
            Enumerate(Get(refinery, "jobs")).Count(), Convert.ToInt32(Get(refinery, "maxJobs")), null, extra, policy);
    }
    private RecipeQuote QuoteExtraction(object station, object player, RecipeStationHandle handle, RecipeId id, int count, long revision)
    {
        if (Get(station, "refinery") == null) return RecipeQuoteService.Failure(RecipeQuoteStatus.StationUnavailable, handle, id, count, "Station has no refinery.");
        var materialDebit = (float)count;
        if ((double)materialDebit != count) return RecipeQuoteService.Failure(RecipeQuoteStatus.InvalidRequest, handle, id, count,
            "Extraction count cannot be represented exactly by native material arithmetic.");
        var type = _assembly.GetType("Source.Item.RefinedMaterial", true)!;
        var name = id.LocalId.Substring("extraction/".Length);
        if (!Enum.GetNames(type).Contains(name, StringComparer.Ordinal)) return RecipeQuoteService.Failure(RecipeQuoteStatus.RecipeUnavailable, handle, id, count, "Material unavailable.");
        var material = Enum.Parse(type, name); var item = FindItem("Canister" + name);
        if (item == null) return RecipeQuoteService.Failure(RecipeQuoteStatus.RecipeUnavailable, handle, id, count, "Material canister unavailable.");
        var costMethod = _assembly.GetType("Source.Mining.Refinery", true)!.GetMethod("GetExtractCost", new[] { type, typeof(int) })!;
        var cost = Convert.ToInt64(costMethod.Invoke(null, new[] { material, (object)count }));
        var inputs = new[] { new RecipeIngredientRequirement(Resource(name, RecipeResourceKind.RefinedMaterial), count,
            new[] { new RecipeInventoryBalance(RecipeInventoryKind.PlayerRefinedMaterials, null, true, Convert.ToDouble(Call(player, "CountRefinedMaterial", material))) }) };
        var output = new[] { new RecipeOutputPreview(Resource(Text(item, "identifier"), RecipeResourceKind.Item), count, 1, "Native extraction forces canisters into current ship cargo; no queued job.", new[] { RecipeInventoryKind.ShipCargo }) };
        return BuildQuote(handle, id, count, revision, player, inputs, output, cost, null, null, null, null,
            !IsLocal(station) ? new[] { RecipeBlocker.InventoryUnavailable } : Array.Empty<RecipeBlocker>());
    }
    private RecipeQuote BuildQuote(RecipeStationHandle handle, RecipeId id, int batches, long revision, object player,
        IEnumerable<RecipeIngredientRequirement> inputs, IEnumerable<RecipeOutputPreview> outputs, long? cost, double? seconds, int? used, int? capacity, int? level, IEnumerable<RecipeBlocker>? extra = null, RefineryInputPolicy policy = RefineryInputPolicy.Manual)
    {
        // A repeated resource row still spends from the same inventory; do not count its stock twice.
        var requirements = inputs.GroupBy(input => input.Resource)
            .Select(group => new RecipeIngredientRequirement(group.Key, group.Sum(input => input.Required), group.First().Inventories)).ToArray();
        var results = outputs.ToArray();
        if (requirements.Length > 256 || results.Length > 512) throw new RecipeCatalogLimitException();
        if (cost < 0 || seconds.HasValue && (!double.IsFinite(seconds.Value) || seconds <= 0))
            return RecipeQuoteService.Failure(RecipeQuoteStatus.Unsupported, handle, id, batches, "Invalid native cost or timing.");
        if (cost.HasValue) cost = checked((long)(float)cost.Value); // Include extraction's native debit conversion.
        var blockers = new List<RecipeBlocker>(extra ?? Array.Empty<RecipeBlocker>());
        if (!cost.HasValue) blockers.Add(RecipeBlocker.PricingUnavailable);
        else if (!(bool)Call(player, "CanAfford", (float)cost.Value)!) blockers.Add(RecipeBlocker.InsufficientCredits);
        if (requirements.Any(input => !input.Available.HasValue)) blockers.Add(RecipeBlocker.InventoryUnavailable);
        if (requirements.Any(input => input.Missing > 0)) blockers.Add(RecipeBlocker.MissingIngredients);
        if (used.HasValue && capacity.HasValue && used >= capacity) blockers.Add(RecipeBlocker.QueueFull);
        if (results.Length == 0 || results.Any(output => output.PossibleDestinations.Count == 0)) blockers.Add(RecipeBlocker.OutputUnresolved);
        return new RecipeQuote(RecipeQuoteStatus.Available, "Advisory native requirements; execution must revalidate.", handle, id, batches, revision,
            requirements, results, blockers, level, cost, Convert.ToInt64(Get(player, "credits")), seconds, used, capacity, policy);
    }
    private bool CanReadForgeCost(object recipe)
    {
        if (Convert.ToInt32(Get(recipe, "customCost")) > 0 || Convert.ToInt32(Get(recipe, "dynamicCost")) >= 0) return true;
        // Cold item.cost can scan recipe outputs and instantiate builder previews. Never prime it in a read.
        foreach (var row in Enumerate(Call(recipe, "GetIngredientItems", 0)))
        {
            var item = Get(row, "Item1");
            if (item == null || !HasCachedItemCost(item)) return false;
        }
        return true; // Material values are an inspected constant enum table; warm item costs do not build previews.
    }
    private static bool HasCachedItemCost(object item)
    {
        var value = Convert.ToSingle(Get(item, "calcCost"));
        return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;
    }
    private bool StationStillPresent(object station)
    {
        var map = GetStatic("Source.Galaxy.GalaxyMapData", "current");
        return map != null && ReferenceEquals(Call(map, "GetPointOfInterest", Text(station, "guid")), station);
    }
    private object? OutputItem(object recipe, RecipeResourceId resource)
    {
        foreach (var row in Enumerate(Get(recipe, "results")))
        {
            var prefab = Get(row, "item"); if (prefab == null) continue;
            var item = Component(prefab, ItemType);
            if (item != null && resource.Kind == RecipeResourceKind.Item && Text(item, "identifier") == resource.LocalId) return item;
            foreach (var builderType in new[] { "Behaviour.Equipment.Builder.EquipmentBuilder", "Behaviour.Item.Builder.ItemBuilder" })
            {
                var expectedKind = builderType == "Behaviour.Equipment.Builder.EquipmentBuilder" ? RecipeResourceKind.EquipmentTemplate : RecipeResourceKind.ItemTemplate;
                if (resource.Kind != expectedKind) continue;
                var builder = Component(prefab, builderType);
                if (builder != null && Text(builder, "identifier") == resource.LocalId) return Get(builder, "prefab");
            }
        }
        return null;
    }
    private RecipeInventoryKind[] OutputDestinations(object station, object player, object? item, int amount)
    {
        var choices = new List<RecipeInventoryKind>();
        if (item == null) return choices.ToArray();
        var cargo = Get(Get(player, "currentSpaceShip")!, "cargo");
        if (cargo != null && MayDepositInCargo(station, player) &&
            !(bool)Call(cargo, "IsFull", Convert.ToSingle(Get(item, "m3")) * amount)!) choices.Add(RecipeInventoryKind.ShipCargo);
        if ((bool)Call(item, "CanGoInArmory")!) choices.Add(RecipeInventoryKind.PlayerArmory);
        else if ((bool)Call(item, "CanGoInMaterials")!) choices.Add(RecipeInventoryKind.StationMaterials);
        return choices.ToArray();
    }
    private bool MayDepositInCargo(object station, object player) => IsLocal(station) &&
        GetStatic("Behaviour.UI.Spacestation.SpaceStationInterior", "instance") != null &&
        (bool)Get(player, "forgeDepositInCargo")! && !_assembly.GetType("Source.Galaxy.POI.IndustryStation", true)!.IsInstanceOfType(station);
    private object? FindItem(string id) => Enumerate(GetStatic(ItemType, "all")).SingleOrDefault(item => Text(item, "identifier") == id);
    private bool IsLocal(object station) => ReferenceEquals(GetStatic("Source.Galaxy.MapPointOfInterest", "current"), station);
    private RecipeInventoryBalance CargoBalance(object station, object player, object item, bool interiorRequired)
    {
        var accessible = IsLocal(station) && (!interiorRequired || GetStatic("Behaviour.UI.Spacestation.SpaceStationInterior", "instance") != null);
        return accessible ? ItemBalance(RecipeInventoryKind.ShipCargo, null, Get(Get(player, "currentSpaceShip")!, "cargo"), item) :
            new RecipeInventoryBalance(RecipeInventoryKind.ShipCargo, null, false, null);
    }
    private static RecipeInventoryBalance ItemBalance(RecipeInventoryKind kind, RecipeStationHandle? handle, object? inventory, object item) =>
        new(kind, handle, true, inventory == null ? null : Convert.ToDouble(Call(inventory, "GetCount", item)));
    private static RecipeInventoryBalance RefiningBalance(RecipeInventoryKind kind, RecipeStationHandle? handle, object? inventory, object item, int reserved, bool automatic, bool accessible)
    {
        if (!accessible) return new(kind, handle, false, null);
        if (inventory == null) return new(kind, handle, true, null);
        long amount = 0;
        foreach (var stack in Enumerate(Get(inventory, "items")))
        {
            if (!ReferenceEquals(Get(stack, "item"), item) || automatic && (bool)Get(stack, "favourite")!) continue;
            amount = checked(amount + Math.Max(0, Convert.ToInt32(Get(stack, "count")) - reserved));
        }
        return new(kind, handle, true, amount);
    }
    private double Skill(string name, string property)
    {
        var node = GetStatic("Behaviour.Crew.SkilltreeNode", name) ?? throw new InvalidOperationException("Skill node unavailable.");
        return Convert.ToDouble(Get(node, property));
    }
    private static double CheckedMaterial(float amount, int batches)
    {
        var total = amount * batches;
        if (float.IsNaN(total) || float.IsInfinity(total) || total <= 0) throw new OverflowException("Invalid material quantity.");
        return total;
    }
    private static object? Call(object instance, string name, params object[] args)
    {
        var method = instance.GetType().GetMethods(Flags).SingleOrDefault(candidate => candidate.Name == name && !candidate.IsStatic &&
            candidate.GetParameters().Length == args.Length && candidate.GetParameters().Select((parameter, index) => parameter.ParameterType.IsInstanceOfType(args[index])).All(value => value));
        return (method ?? throw new MissingMethodException(instance.GetType().FullName, name)).Invoke(instance, args);
    }
}
