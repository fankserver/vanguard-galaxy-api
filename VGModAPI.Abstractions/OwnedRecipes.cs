using System;
using System.Collections.Generic;

namespace VGModAPI;

/// <summary>Exactly one vanilla catalog ID or provider-scoped owned definition.</summary>
public sealed class RecipeItemReference
{
    public string? VanillaId { get; }
    public OwnedItemReference? Owned { get; }
    private RecipeItemReference(string? vanillaId, OwnedItemReference? owned) { VanillaId = vanillaId; Owned = owned; }
    public static RecipeItemReference Vanilla(string id) => new(id ?? throw new ArgumentNullException(nameof(id)), null);
    public static RecipeItemReference FromOwned(OwnedItemReference reference) => new(null, reference ?? throw new ArgumentNullException(nameof(reference)));
}
public sealed class OwnedRecipeIngredient
{
    public RecipeItemReference Item { get; }
    public int Count { get; }
    public OwnedRecipeIngredient(RecipeItemReference item, int count) { Item = item ?? throw new ArgumentNullException(nameof(item)); Count = count; }
}
/// <summary>Fixed-output plain-goods recipe. Economics are supplied by its author.</summary>
public sealed class OwnedRecipeDefinition
{
    public string LocalId { get; }
    public int Revision { get; }
    public string Name { get; }
    public int Credits { get; }
    public float Seconds { get; }
    public IReadOnlyList<OwnedRecipeIngredient> Ingredients { get; }
    public OwnedRecipeIngredient Result { get; }
    public OwnedRecipeDefinition(string localId, int revision, string name, int credits, float seconds,
        IReadOnlyList<OwnedRecipeIngredient> ingredients, OwnedRecipeIngredient result)
    {
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId)); Revision = revision;
        Name = name ?? throw new ArgumentNullException(nameof(name)); Credits = credits; Seconds = seconds;
        if (ingredients == null) throw new ArgumentNullException(nameof(ingredients));
        var copy = new OwnedRecipeIngredient[ingredients.Count]; for (int i = 0; i < copy.Length; i++) copy[i] = ingredients[i];
        Ingredients = Array.AsReadOnly(copy); Result = result ?? throw new ArgumentNullException(nameof(result));
    }
}
public enum OwnedRecipeStatus { Succeeded, PendingDependencies, Duplicate, InvalidDefinition, Unavailable, Rejected }
public interface IOwnedRecipeService : IServiceStatus
{
    IOwnedRecipeProvider? AcquireProvider(object pluginInstance);
}
public interface IOwnedRecipeProvider : IDisposable
{
    string ProviderId { get; }
    OwnedRecipeStatus Register(OwnedRecipeDefinition definition);
    /// <summary>Resolved recipe handle for existing catalog/quote/command APIs; null while dependencies are pending.</summary>
    RecipeId? Find(string localId);
}
