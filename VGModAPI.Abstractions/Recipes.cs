using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

/// <summary>Provider and local identity are separate; display names never identify content.</summary>
public sealed class RecipeId : IEquatable<RecipeId>
{
    public string ProviderId { get; }
    public string LocalId { get; }
    public RecipeId(string providerId, string localId)
    {
        ProviderId = RecipeValues.Identity(providerId, nameof(providerId));
        LocalId = RecipeValues.Identity(localId, nameof(localId));
    }
    public bool Equals(RecipeId? other) => other != null && ProviderId == other.ProviderId && LocalId == other.LocalId;
    public override bool Equals(object? obj) => obj is RecipeId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(ProviderId, LocalId);
    public override string ToString() => ProviderId + ":" + LocalId;
}

public enum RecipeProcess { Forge, Refining, MaterialExtraction }
public enum RecipeResourceKind { Item, RefinedMaterial, EquipmentTemplate, ItemTemplate }
public enum RecipeAvailability { Available, Locked, Unsupported, Unresolved }
public enum RecipeCatalogStatus { Available, IntegrationUnavailable, SessionUnavailable, StationUnavailable, LimitExceeded, NativeFailure }

/// <summary>Template identity describes possible generated items, not an already-created inventory instance.</summary>
public sealed class RecipeResourceId : IEquatable<RecipeResourceId>
{
    public string ProviderId { get; }
    public string LocalId { get; }
    public RecipeResourceKind Kind { get; }
    public RecipeResourceId(string providerId, string localId, RecipeResourceKind kind)
    {
        if (!Enum.IsDefined(typeof(RecipeResourceKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ProviderId = RecipeValues.Identity(providerId, nameof(providerId));
        LocalId = RecipeValues.Identity(localId, nameof(localId)); Kind = kind;
    }
    public bool Equals(RecipeResourceId? other) => other != null && ProviderId == other.ProviderId && LocalId == other.LocalId && Kind == other.Kind;
    public override bool Equals(object? obj) => obj is RecipeResourceId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(ProviderId, LocalId, Kind);
}

/// <summary>One nominal batch quantity. LevelScaled quantities require a context quote before use as costs.</summary>
public sealed class RecipeResourceAmount
{
    public RecipeResourceId Resource { get; }
    public double Amount { get; }
    public bool LevelScaled { get; }
    public bool Conditional { get; }
    public RecipeResourceAmount(RecipeResourceId resource, double amount, bool levelScaled = false, bool conditional = false)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        if (double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0 || amount > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(amount));
        if (resource.Kind != RecipeResourceKind.RefinedMaterial && Math.Truncate(amount) != amount)
            throw new ArgumentException("Item quantities must be integral.", nameof(amount));
        Amount = amount; LevelScaled = levelScaled; Conditional = conditional;
    }
}

/// <summary>A copied definition for a specific variant. Inputs/outputs are lists, never a one-output assumption.</summary>
public sealed class RecipeSnapshot
{
    public RecipeId Id { get; }
    public RecipeId? ParentId { get; }
    public string DisplayName { get; }
    public RecipeProcess Process { get; }
    public RecipeAvailability Availability { get; }
    public string Detail { get; }
    public string? Rarity { get; }
    public bool OutputLevelDependsOnPlayer { get; }
    public IReadOnlyList<RecipeResourceAmount> Inputs { get; }
    public IReadOnlyList<RecipeResourceAmount> Outputs { get; }
    public RecipeSnapshot(RecipeId id, RecipeId? parentId, string displayName, RecipeProcess process,
        RecipeAvailability availability, string detail, IEnumerable<RecipeResourceAmount> inputs,
        IEnumerable<RecipeResourceAmount> outputs, string? rarity = null, bool outputLevelDependsOnPlayer = false)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id)); ParentId = parentId;
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        if (DisplayName.Length > 4096) throw new ArgumentOutOfRangeException(nameof(displayName));
        if (!Enum.IsDefined(typeof(RecipeProcess), process)) throw new ArgumentOutOfRangeException(nameof(process));
        if (!Enum.IsDefined(typeof(RecipeAvailability), availability)) throw new ArgumentOutOfRangeException(nameof(availability));
        Process = process; Availability = availability; Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Rarity = rarity; OutputLevelDependsOnPlayer = outputLevelDependsOnPlayer;
        Inputs = RecipeValues.Copy(inputs, 256); Outputs = RecipeValues.Copy(outputs, 256);
        if (Outputs.Count == 0 && availability == RecipeAvailability.Available)
            throw new ArgumentException("An available recipe requires a resolved output.", nameof(outputs));
    }
}

/// <summary>Immutable, bounded catalog for the current station/session. Failure is not an empty successful catalog.</summary>
public sealed class RecipeCatalogSnapshot
{
    public RecipeCatalogStatus Status { get; }
    public Guid? SessionId { get; }
    public string Detail { get; }
    public IReadOnlyList<RecipeSnapshot> Recipes { get; }
    public RecipeCatalogSnapshot(RecipeCatalogStatus status, Guid? sessionId, string detail, IEnumerable<RecipeSnapshot> recipes)
    {
        if (!Enum.IsDefined(typeof(RecipeCatalogStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status; SessionId = sessionId; Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Recipes = RecipeValues.Copy(recipes, 16384);
        var ids = new HashSet<RecipeId>();
        foreach (var recipe in Recipes) if (!ids.Add(recipe.Id)) throw new ArgumentException("Duplicate recipe identity.", nameof(recipes));
        if (status != RecipeCatalogStatus.Available && Recipes.Count != 0)
            throw new ArgumentException("Failed catalog cannot contain partial success data.", nameof(recipes));
    }

    /// <summary>Exact resource identity lookup; returns every matching variant, without choosing a production route.</summary>
    public IReadOnlyList<RecipeSnapshot> FindProducers(RecipeResourceId resource, bool includeUnavailable = false)
    {
        if (resource == null) throw new ArgumentNullException(nameof(resource));
        var matches = new List<RecipeSnapshot>();
        foreach (var recipe in Recipes)
        {
            if (!includeUnavailable && recipe.Availability != RecipeAvailability.Available) continue;
            foreach (var output in recipe.Outputs)
                if (output.Resource.Equals(resource)) { matches.Add(recipe); break; }
        }
        return matches.AsReadOnly();
    }
}

/// <summary>Main-thread observational access. Read again after station/session/catalog changes; snapshots are not reservations.</summary>
public interface IRecipeCatalog
{
    RecipeCatalogSnapshot Read(bool includeUnavailable = false);
}

internal static class RecipeValues
{
    internal static string Identity(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512) throw new ArgumentException("A bounded identity is required.", parameter);
        foreach (var character in value) if (char.IsControl(character)) throw new ArgumentException("Control characters are not valid identities.", parameter);
        return value;
    }
    internal static ReadOnlyCollection<T> Copy<T>(IEnumerable<T> source, int limit) where T : class
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        var copy = new List<T>();
        foreach (var value in source)
        {
            if (copy.Count >= limit) throw new ArgumentOutOfRangeException(nameof(source), "Collection limit exceeded.");
            copy.Add(value ?? throw new ArgumentException("Null collection entry.", nameof(source)));
        }
        return copy.AsReadOnly();
    }
}
