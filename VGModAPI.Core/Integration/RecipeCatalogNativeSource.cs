using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Reads definitions only. Never invokes preview builders or crafting/delivery methods.</summary>
internal sealed class RecipeCatalogNativeSource : IRecipeCatalogSource
{
    private const string RecipeType = "Behaviour.Crafting.CraftingRecipe";
    private const string ItemType = "Behaviour.Item.InventoryItemType";
    private readonly Assembly _assembly;
    private readonly Func<object, Type, object?> _component;
    private readonly Func<string, string> _translate;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal RecipeCatalogNativeSource(Assembly assembly, Func<object, Type, object?> component, Func<string, string> translate)
    {
        _assembly = assembly; _component = component; _translate = translate;
        foreach (var name in new[] { RecipeType, ItemType, "Source.Mining.Forge", "Behaviour.Mining.OreItemData",
            "Behaviour.Equipment.Builder.EquipmentBuilder", "Behaviour.Item.Builder.ItemBuilder" })
            _ = assembly.GetType(name, true);
        RecipeCatalogBindings.Validate(assembly);
    }
    public RecipeCatalogSnapshot Read(Guid sessionId, bool includeUnavailable)
    {
        var forge = GetStatic("Source.Mining.Forge", "current");
        if (forge == null) return RecipeCatalogService.Failure(RecipeCatalogStatus.StationUnavailable, sessionId, "An accessible station Forge is required.");
        var definitions = new Dictionary<RecipeId, RecipeSnapshot>();
        var nativeIdentities = new Dictionary<string, object>(StringComparer.Ordinal);
        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in Enumerate(Get(forge, "recipes")))
        {
            CheckIdentity(nativeIdentities, "forge/" + Text(recipe, "identifier"), recipe);
            available.Add(Text(recipe, "identifier"));
            Add(definitions, ReadForge(recipe, RecipeAvailability.Available));
        }
        if (includeUnavailable)
        {
            foreach (var parent in Enumerate(GetStatic(RecipeType, "all")))
                foreach (var variant in Enumerate(Get(parent, "subRecipes")))
                {
                    CheckIdentity(nativeIdentities, "forge/" + Text(variant, "identifier"), variant);
                    if (!available.Contains(Text(variant, "identifier"))) Add(definitions, ReadForge(variant, RecipeAvailability.Locked));
                }
        }
        foreach (var item in Enumerate(GetStatic(ItemType, "all")))
        {
            var ore = Component(Get(item, "gameObject")!, "Behaviour.Mining.OreItemData");
            if (ore == null) continue;
            var id = Text(item, "identifier");
            CheckIdentity(nativeIdentities, "refining/" + id, item);
            var outputs = new List<RecipeResourceAmount>();
            var invalid = false;
            foreach (var product in Enumerate(Get(ore, "contents")))
            {
                var amount = Convert.ToDouble(Get(product, "yield"));
                if (!Positive(amount)) { invalid = true; continue; }
                outputs.Add(new RecipeResourceAmount(Resource(Get(product, "product")!.ToString()!, RecipeResourceKind.RefinedMaterial), amount));
            }
            if (outputs.Count > 256) throw new RecipeCatalogLimitException();
            Add(definitions, new RecipeSnapshot(new RecipeId("vanilla", "refining/" + id), null, _translate(Text(item, "displayName")),
                RecipeProcess.Refining, invalid || outputs.Count == 0 ? RecipeAvailability.Unresolved : RecipeAvailability.Available,
                "Base yields; skill-dependent bonus outputs require a context quote.",
                new[] { new RecipeResourceAmount(Resource(id, RecipeResourceKind.Item), 1) }, outputs));
        }
        return new RecipeCatalogSnapshot(RecipeCatalogStatus.Available, sessionId, "Definitions only; use context quotes for costs and conditional rewards.",
            definitions.Values.OrderBy(value => value.Id.LocalId, StringComparer.Ordinal));
    }
    private RecipeSnapshot ReadForge(object recipe, RecipeAvailability availability)
    {
        var id = Text(recipe, "identifier");
        var parent = Get(recipe, "parentRecipe");
        var parentId = parent == null ? null : Text(parent, "identifier");
        var scaled = (bool)Get(recipe, "levelingItem")!;
        var inputs = new List<RecipeResourceAmount>(); var outputs = new List<RecipeResourceAmount>();
        var invalid = false; var generated = false;
        foreach (var row in Enumerate(Get(recipe, "materials")))
        {
            var quantity = Convert.ToDouble(Get(row, "amount"));
            if (!Positive(quantity)) { invalid = true; continue; }
            inputs.Add(new RecipeResourceAmount(Resource(Get(row, "material")!.ToString()!, RecipeResourceKind.RefinedMaterial), quantity, scaled));
        }
        foreach (var row in Enumerate(Get(recipe, "itemMaterials")))
        {
            var prefab = Get(row, "item");
            var item = prefab == null ? null : Component(prefab, ItemType);
            var count = Convert.ToInt32(Get(row, "count"));
            if (item == null || count <= 0) { invalid = true; continue; }
            inputs.Add(new RecipeResourceAmount(Resource(Text(item, "identifier"), RecipeResourceKind.Item), count, scaled));
        }
        foreach (var row in Enumerate(Get(recipe, "results")))
        {
            var prefab = Get(row, "item"); var count = Convert.ToInt32(Get(row, "count"));
            if (prefab == null || count <= 0) { invalid = true; continue; }
            var item = Component(prefab, ItemType);
            if (item != null)
            {
                outputs.Add(new RecipeResourceAmount(Resource(Text(item, "identifier"), RecipeResourceKind.Item), count)); continue;
            }
            var builder = Component(prefab, "Behaviour.Equipment.Builder.EquipmentBuilder");
            var kind = RecipeResourceKind.EquipmentTemplate;
            if (builder == null) { builder = Component(prefab, "Behaviour.Item.Builder.ItemBuilder"); kind = RecipeResourceKind.ItemTemplate; }
            if (builder == null) { invalid = true; continue; }
            outputs.Add(new RecipeResourceAmount(Resource(Text(builder, "identifier"), kind), count)); generated = true;
        }
        if (inputs.Count > 256 || outputs.Count > 256) throw new RecipeCatalogLimitException();
        return new RecipeSnapshot(new RecipeId("vanilla", "forge/" + id),
            parentId == null || parentId == id ? null : new RecipeId("vanilla", "forge/" + parentId),
            _translate(Text(recipe, "displayName")), RecipeProcess.Forge,
            invalid || outputs.Count == 0 ? RecipeAvailability.Unsupported : availability,
            invalid ? "Recipe has unsupported or unresolved resource rows." : "Nominal inputs; level scaling requires a context quote.",
            inputs, outputs, Get(recipe, "craftingRarity")!.ToString(), generated);
    }
    private static void CheckIdentity(Dictionary<string, object> identities, string id, object instance)
    {
        if (identities.TryGetValue(id, out var previous) && !ReferenceEquals(previous, instance))
            throw new InvalidOperationException("Conflicting native recipe identity: " + id);
        identities[id] = instance;
    }
    private static bool Positive(double number) => !double.IsNaN(number) && !double.IsInfinity(number) && number > 0 && number <= int.MaxValue;
    private static RecipeResourceId Resource(string id, RecipeResourceKind kind) => new("vanilla", id, kind);
    private object? Component(object prefab, string type) => _component(prefab, _assembly.GetType(type, true)!);
    private object? GetStatic(string type, string member) => ReadMember(RequireMember(_assembly.GetType(type, true)!, member), null);
    private static object? Get(object instance, string member) => ReadMember(RequireMember(instance.GetType(), member), instance);
    private static string Text(object instance, string member)
    {
        var text = Get(instance, member) as string ?? throw new InvalidOperationException("Missing string: " + member);
        if (member == "identifier" && (string.IsNullOrWhiteSpace(text) || text.Length > 500 || text.Any(char.IsControl)))
            throw new InvalidOperationException("Missing or invalid native identity.");
        return text;
    }
    private static MemberInfo RequireMember(Type type, string name) => (MemberInfo?)type.GetField(name, Flags) ?? type.GetProperty(name, Flags) ?? throw new MissingMemberException(type.FullName, name);
    private static object? ReadMember(MemberInfo member, object? instance) => member is FieldInfo field ? field.GetValue(instance) : ((PropertyInfo)member).GetValue(instance);
    private static IEnumerable<object> Enumerate(object? source)
    {
        if (source is not IEnumerable enumerable) throw new InvalidOperationException("Native catalog collection unavailable.");
        var count = 0;
        foreach (var item in enumerable)
        {
            if (++count > 65536) throw new RecipeCatalogLimitException();
            if (item == null) throw new InvalidOperationException("Null native recipe row.");
            yield return item;
        }
    }
    private static void Add(Dictionary<RecipeId, RecipeSnapshot> definitions, RecipeSnapshot value)
    {
        if (definitions.ContainsKey(value.Id)) return; // Native availability lists may repeat the same recipe.
        if (definitions.Count >= 16384) throw new RecipeCatalogLimitException();
        definitions.Add(value.Id, value);
    }
}
