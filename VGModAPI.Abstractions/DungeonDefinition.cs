using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI;

/// <summary>Stable namespaced definition identity, distinct from each saved dungeon occurrence.</summary>
public sealed class DungeonDefinitionId : IEquatable<DungeonDefinitionId>
{
    public string ProviderId { get; }
    public string LocalId { get; }
    public DungeonDefinitionId(string providerId, string localId)
    { DungeonLayoutIds.Check(providerId); DungeonLayoutIds.Check(localId); ProviderId = providerId; LocalId = localId; }
    public bool Equals(DungeonDefinitionId? other) => other != null && ProviderId == other.ProviderId && LocalId == other.LocalId;
    public override bool Equals(object? obj) => Equals(obj as DungeonDefinitionId);
    public override int GetHashCode() => HashCode.Combine(ProviderId, LocalId);
}

/// <summary>Bounded item reward using an existing native item identifier, not an item-type declaration.</summary>
public sealed class DungeonLootDefinition
{
    public string ItemId { get; }
    public int Amount { get; }
    public DungeonLootDefinition(string itemId, int amount)
    {
        DungeonNativeIds.Check(itemId); if (amount < 1 || amount > 10000) throw new ArgumentOutOfRangeException(nameof(amount));
        ItemId = itemId; Amount = amount;
    }
}

public sealed class DungeonChoiceDefinition
{
    public string Id { get; }
    public string Text { get; }
    public string? RequiredCrewId { get; }
    public IReadOnlyList<DungeonLootDefinition> Loot { get; }
    public DungeonChoiceDefinition(string id, string text, string? requiredCrewId = null, IEnumerable<DungeonLootDefinition>? loot = null)
    {
        DungeonLayoutIds.Check(id); DungeonDefinitionText.Check(text, 1000);
        if (requiredCrewId != null) DungeonNativeIds.Check(requiredCrewId);
        var rewards = (loot ?? Array.Empty<DungeonLootDefinition>()).ToArray();
        if (rewards.Length > 16 || rewards.Any(r => r == null)) throw new ArgumentException("At most sixteen valid loot entries per choice.");
        Id = id; Text = text; RequiredCrewId = requiredCrewId; Loot = Array.AsReadOnly(rewards);
    }
}

/// <summary>Contextual authored event and persisted choice IDs. Provider callbacks are registered separately, never serialized.</summary>
public sealed class DungeonEventDefinition
{
    public string Id { get; }
    public string CompartmentId { get; }
    public string Text { get; }
    public IReadOnlyList<DungeonChoiceDefinition> Choices { get; }
    public DungeonEventDefinition(string id, string compartmentId, string text, IEnumerable<DungeonChoiceDefinition> choices)
    {
        DungeonLayoutIds.Check(id); DungeonLayoutIds.Check(compartmentId); DungeonDefinitionText.Check(text, 4000);
        var copy = (choices ?? throw new ArgumentNullException(nameof(choices))).ToArray();
        if (copy.Length < 1 || copy.Length > 8 || copy.Any(c => c == null) || copy.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("An event requires one to eight uniquely identified choices.");
        Id = id; CompartmentId = compartmentId; Text = text; Choices = new ReadOnlyCollection<DungeonChoiceDefinition>(copy);
    }
}

/// <summary>Immutable dungeon authoring data. Catalog validation occurs before registration can succeed.</summary>
public sealed class DungeonDefinition
{
    public int Version { get; }
    public string Name { get; }
    public DungeonLayout Layout { get; }
    public string? FactionId { get; }
    public IReadOnlyList<DungeonEventDefinition> Events { get; }
    public bool AllowHazards { get; }
    public bool AllowScheduledReinforcements { get; }
    public DungeonDefinition(int version, string name, DungeonLayout layout, string? factionId = null, IEnumerable<DungeonEventDefinition>? events = null,
        bool allowHazards = true, bool allowScheduledReinforcements = true)
    {
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        DungeonDefinitionText.Check(name, 256); if (factionId != null) DungeonNativeIds.Check(factionId);
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        var copy = (events ?? Array.Empty<DungeonEventDefinition>()).ToArray();
        if (copy.Length > 128 || copy.Any(e => e == null) || copy.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("At most 128 uniquely identified events supported.");
        var rooms = new HashSet<string>(layout.Compartments.Select(c => c.Id), StringComparer.Ordinal);
        if (copy.Any(e => !rooms.Contains(e.CompartmentId))) throw new ArgumentException("Event compartment does not exist.");
        Version = version; Name = name; FactionId = factionId; Events = Array.AsReadOnly(copy);
        AllowHazards = allowHazards; AllowScheduledReinforcements = allowScheduledReinforcements;
    }
}

internal static class DungeonDefinitionText
{
    internal static void Check(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.IndexOf('\0') >= 0)
            throw new ArgumentException("Text is empty, too long or contains a null character.", nameof(value));
    }
}
