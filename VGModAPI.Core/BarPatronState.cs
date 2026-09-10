using System;
using System.Text;

namespace VGModAPI.Core;

/// <summary>Immutable persisted author data. No callback, native object, seat or voice queue is retained.</summary>
internal sealed class BarPatronState
{
    internal BarPatronId Id { get; }
    internal string Station { get; }
    internal string Name { get; }
    internal string Description { get; }
    internal string Seed { get; }
    internal CharacterPortrait? Portrait { get; }
    internal bool IsMale { get; }
    internal bool Removed { get; }
    internal BarPatronState WithRemoved(bool removed) => new(Id, Station, Name, Description, Seed, Mission, Occurrence, Portrait, IsMale, removed);
    internal StoryContentId? Mission { get; }
    internal Guid? Occurrence { get; }

    internal BarPatronState(BarPatronId id, string station, string name, string description, string seed,
        StoryContentId? mission = null, Guid? occurrence = null, CharacterPortrait? portrait = null, bool isMale = true, bool removed = false)
    {
        _ = new BarPatronId(id.Provider, id.LocalId);
        Id = id;
        Station = Text(station, 128, nameof(station));
        Name = Text(name, 128, nameof(name));
        Description = Text(description, 1024, nameof(description));
        Seed = Text(seed, 128, nameof(seed));
        if (portrait?.RegistryName is { } registry)
        {
            Text(registry, 2048, nameof(portrait));
            if (registry.StartsWith(StoryCharacterService.LookupPrefix, StringComparison.Ordinal))
                throw new ArgumentException("Borrow portraits from game characters, not introduced ones.", nameof(portrait));
        }
        Portrait = portrait; IsMale = isMale; Removed = removed;
        if (mission.HasValue != occurrence.HasValue || occurrence == Guid.Empty)
            throw new ArgumentException("A mission reference needs both definition and nonempty occurrence identity.");
        if (mission.HasValue)
        {
            _ = new StoryContentId(mission.Value.Provider, mission.Value.LocalId);
            if (mission.Value.Provider != id.Provider) throw new ArgumentException("A patron cannot claim another provider's mission.");
        }
        Mission = mission; Occurrence = occurrence;
    }

    private static string Text(string value, int maxBytes, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Nonempty text is required.", parameter);
        foreach (char character in value)
            if (char.IsControl(character)) throw new ArgumentException("Control characters are not supported.", parameter);
        if (new UTF8Encoding(false, true).GetByteCount(value) > maxBytes) throw new ArgumentException("Text exceeds its UTF-8 bound.", parameter);
        return value;
    }
}
