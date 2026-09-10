using System;
using System.Text;

namespace VGModAPI;

/// <summary>How a contact looks and sits: seed, portrait and seated body are independent native inputs.</summary>
public sealed class BarPatronPresentation
{
    public string Seed { get; }
    /// <summary>Named NPC portrait or a game character's portrait. Null uses the default contact portrait; the seat/body seed is independent.</summary>
    public CharacterPortrait? Portrait { get; }
    /// <summary>Selects the native male or female seated body, independently of portrait and seed.</summary>
    public bool IsMale { get; }
    public BarPatronPresentation(string seed, CharacterPortrait? portrait = null, bool isMale = true)
    {
        Seed = BarPatronDefinition.Text(seed, 128, nameof(seed));
        Portrait = portrait; IsMale = isMale;
    }
}

/// <summary>Immutable contact data. Narrative callbacks and voice queues are not serialized.</summary>
public sealed class BarPatronDefinition
{
    public string LocalId { get; }
    public string StationId { get; }
    public string Name { get; }
    public string Description { get; }
    public BarPatronPresentation Presentation { get; }
    public string Seed => Presentation.Seed;
    public CharacterPortrait? Portrait => Presentation.Portrait;
    public bool IsMale => Presentation.IsMale;
    public BarPatronRetention Retention { get; }
    /// <summary>Same-owner story definition this contact depends on. The API resolves the current admitted occurrence itself.</summary>
    public StoryContentId? Mission { get; }

    public BarPatronDefinition(string localId, string stationId, string name, string description,
        BarPatronPresentation presentation, BarPatronRetention retention = BarPatronRetention.Persistent, StoryContentId? mission = null)
    {
        Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        _ = new BarPatronId("validation", localId);
        if (!Enum.IsDefined(typeof(BarPatronRetention), retention)) throw new ArgumentOutOfRangeException(nameof(retention));
        LocalId = localId;
        StationId = Text(stationId, 128, nameof(stationId));
        Name = Text(name, 128, nameof(name));
        Description = Text(description, 1024, nameof(description));
        if (mission.HasValue) _ = new StoryContentId(mission.Value.Provider, mission.Value.LocalId);
        Mission = mission; Retention = retention;
    }

    internal static string Text(string value, int maxBytes, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Nonempty text is required.", parameter);
        foreach (char character in value)
            if (char.IsControl(character)) throw new ArgumentException("Control characters are not supported.", parameter);
        if (new UTF8Encoding(false, true).GetByteCount(value) > maxBytes) throw new ArgumentException("Text exceeds its UTF-8 bound.", parameter);
        return value;
    }
}
