using System;

namespace VGModAPI;

/// <summary>Plain stackable goods, not equipment, custom scripts or a private vanilla category.</summary>
public enum OwnedItemStorage { Materials, Armory }
public enum OwnedItemStatus { Succeeded, Unavailable, NotReady, InvalidDefinition, Duplicate, Rejected }

/// <summary>Immutable fields for a plain trade-goods item. Vanilla owns quantities and favourite flags.</summary>
public sealed class OwnedItemDefinition
{
    public string LocalId { get; }
    public int Revision { get; }
    public string Name { get; }
    public string Description { get; }
    public string IconItemId { get; }
    public float Volume { get; }
    public int BaseCost { get; }
    public OwnedItemStorage Storage { get; }
    public OwnedItemDefinition(string localId, int revision, string name, string description, string iconItemId,
        float volume, int baseCost, OwnedItemStorage storage)
    {
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId)); Revision = revision;
        Name = name ?? throw new ArgumentNullException(nameof(name)); Description = description ?? throw new ArgumentNullException(nameof(description));
        IconItemId = iconItemId ?? throw new ArgumentNullException(nameof(iconItemId)); Volume = volume; BaseCost = baseCost; Storage = storage;
    }
}

public interface IOwnedItemService : IServiceStatus
{
    IOwnedItemProvider? AcquireProvider(object pluginInstance);
}
public interface IOwnedItemProvider : IDisposable
{
    string ProviderId { get; }
    OwnedItemStatus Register(OwnedItemDefinition definition);
}

public sealed class OwnedItemReference
{
    public string ProviderId { get; }
    public string LocalId { get; }
    public OwnedItemReference(string providerId, string localId) { ProviderId = providerId; LocalId = localId; }
}
