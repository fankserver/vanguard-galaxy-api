using System;
using System.Text;

namespace VGModAPI;

/// <summary>Immutable contact data. Narrative callbacks and voice queues are not serialized.</summary>
public sealed class BarPatronDefinition
{
    public string LocalId { get; }
    public string StationId { get; }
    public string Name { get; }
    public string Description { get; }
    public string Seed { get; }
    /// <summary>Named NPC portrait or a game character's portrait. Null uses the default contact portrait; the seat/body seed is independent.</summary>
    public CharacterPortrait? Portrait { get; }
    public BarPatronRetention Retention { get; }
    public StoryContentId? Mission { get; }
    public Guid? Occurrence { get; }

    public BarPatronDefinition(string localId, string stationId, string name, string description, string seed,
        BarPatronRetention retention = BarPatronRetention.Persistent, StoryContentId? mission = null, Guid? occurrence = null,
        CharacterPortrait? portrait = null)
    {
        _ = new BarPatronId("validation", localId);
        if (!Enum.IsDefined(typeof(BarPatronRetention), retention)) throw new ArgumentOutOfRangeException(nameof(retention));
        LocalId = localId;
        StationId = Text(stationId, 128, nameof(stationId));
        Name = Text(name, 128, nameof(name));
        Description = Text(description, 1024, nameof(description));
        Seed = Text(seed, 128, nameof(seed));
        Portrait = portrait;
        if (mission.HasValue != occurrence.HasValue || occurrence == Guid.Empty)
            throw new ArgumentException("Mission definition and nonempty occurrence must be supplied together.");
        if (mission.HasValue) _ = new StoryContentId(mission.Value.Provider, mission.Value.LocalId);
        Mission = mission; Occurrence = occurrence; Retention = retention;
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
