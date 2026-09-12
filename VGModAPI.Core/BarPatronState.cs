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
    internal BarPatronState WithRemoved(bool removed) => new(Id, Station, Name, Description, Seed, Mission, MissionId, Portrait, IsMale, removed);
    internal StoryMissionDefinitionId? Mission { get; }
    internal Guid? MissionId { get; }

    internal BarPatronState(BarPatronId id, string station, string name, string description, string seed,
        StoryMissionDefinitionId? mission = null, Guid? missionId = null, CharacterPortrait? portrait = null, bool isMale = true, bool removed = false)
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
        // Definition and run are independent facts: a patron offers a definition, and separately may
        // be bound to a run of it. Requiring both (or neither) erased the "offers X, not yet bound"
        // state the BarPatronDefinition model already expects.
        if (missionId == Guid.Empty) throw new ArgumentException("A bound mission needs a nonempty identity.", nameof(missionId));
        if (missionId.HasValue && !mission.HasValue)
            throw new ArgumentException("A bound mission run needs the definition it runs.", nameof(mission));
        if (mission.HasValue)
        {
            _ = new StoryMissionDefinitionId(mission.Value.Provider, mission.Value.LocalId);
            if (mission.Value.Provider != id.Provider) throw new ArgumentException("A patron cannot claim another provider's mission.");
        }
        Mission = mission; MissionId = missionId;
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
